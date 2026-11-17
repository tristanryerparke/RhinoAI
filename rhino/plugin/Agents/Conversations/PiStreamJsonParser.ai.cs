using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acp;
using ContentBlock = Acp.ContentBlock; // disambiguate from Rhino.AI.Server.ContentBlock

namespace Rhino.AI;

// The pi stream-json strategy (earendil-works/pi-coding-agent): turns one `pi --mode rpc` stdout
// line into ACP session/update events and frames one user turn for stdin. pi's RPC mode is a
// long-lived JSONL process like Claude's interactive stream-json, so the same StreamJsonAgent runner
// drives it; this parser owns only the pi-specific launch args and translation. RhinoApp-free so it
// Compile Include's into a test project (mirrors the Claude/Codex parsers).
internal sealed class PiStreamJsonParser : IStreamJsonParser
{
    private AgentDefinition Definition { get; }

    public PiStreamJsonParser(AgentDefinition definition)
    {
        Definition = definition;
    }

    public string DisplayName => Definition.Name;

    public string NotFoundMessage => "pi CLI not found. Install pi (npm install -g @earendil-works/pi-coding-agent).";

    // One process holds the session; every prompt is one stdin line. Like Claude, unlike Codex.
    public bool IsOneTurnPerProcess => false;

    // `pi auth check` refuses to run without --provider or --model. With a panel-selected model we
    // can ask about exactly that model; without one pi's own default provider is unknowable from
    // here, so the probe stays off (Unknown) and pi's ordinary error text surfaces instead.
    public IReadOnlyList<string> AuthStatusArguments
    {
        get
        {
            string model = AISettings.EffectiveModel(Definition);
            return model.Length > 0 ? ["auth", "check", "--model", model, "--json"] : [];
        }
    }

    // pi signs in per provider in its own terminal (`/login` is TUI-only), so there is no hidden
    // browser flow to launch from the panel.
    public IReadOnlyList<string> LoginArguments => [];

    // `pi auth check --model X --json` -> {"status":"ready"|"not_ready"|"invalid", ...}. Anything
    // unrecognised MUST come back Unknown, never SignedOut: a sign-in window the user did not need
    // is worse than a turn that fails with its ordinary error.
    public CliLogin.State ReadAuthState(string output, int exitCode)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("status", out JsonElement status))
                return CliLogin.State.Unknown;
            return status.GetString() switch
            {
                "ready" => CliLogin.State.SignedIn,
                "not_ready" => CliLogin.State.SignedOut,
                _ => CliLogin.State.Unknown,
            };
        }
        catch (JsonException)
        {
            return CliLogin.State.Unknown;
        }
    }

    public void ConfigureArguments(ProcessStartInfo psi, string mcpUrl, string agentSessionId, IReadOnlyList<string> mcpServers, bool resume, IReadOnlyList<ContentBlock> prompt)
    {
        // Same {"mcpServers":{...}} shape pi's mcp.json expects; rhino points at this doc's HTTP
        // listener so the agent always operates on the exact doc. Extra servers the runner resolved
        // are merged in, but never the reserved 'rhino' key. pi's timeout is per-request SECONDS,
        // so 3600 matches the one-hour ceiling the Claude parser raises.
        JsonObject servers = new()
        {
            ["rhino"] = new JsonObject { ["url"] = mcpUrl, ["timeout"] = 3600 },
        };
        foreach (string entry in mcpServers)
            if (JsonNode.Parse(entry) is JsonObject obj)
                foreach (KeyValuePair<string, JsonNode?> kvp in obj)
                    if (kvp.Key != "rhino" && kvp.Value is JsonNode node)
                        servers[kvp.Key] = node.DeepClone();

        DirectoryInfo configDir = PrepareConfigDir(agentSessionId, servers);
        psi.Environment["PI_CODING_AGENT_DIR"] = configDir.FullName;
        // Sessions must outlive the scratch config dir (temp cleanup) or a Rhino restart could never
        // resume: park them in a stable app-data folder instead.
        SessionStoreDir.Create();
        psi.Environment["PI_CODING_AGENT_SESSION_DIR"] = SessionStoreDir.FullName;
        // No update ping on every spawn.
        psi.Environment["PI_SKIP_VERSION_CHECK"] = "1";

        // A Dock-launched Rhino passes children a minimal PATH with no homebrew/nvm entries, and an
        // npm pi shim is a script whose `env node` shebang needs node findable: prepend the resolved
        // pi's own directory (it sits beside node for brew/nvm/npm layouts) plus the standard node
        // homes, so the shim finds its runtime however pi was installed. CliProcess.ConfigureFileName
        // has already run, so psi.FileName is the resolved path on macOS.
        if (OperatingSystem.IsMacOS())
        {
            string piDir = Path.GetDirectoryName(psi.FileName) ?? string.Empty;
            string extra = string.Join(Path.PathSeparator, new[]
            {
                piDir,
                "/opt/homebrew/bin",
                "/usr/local/bin",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin"),
            }.Where(static d => d.Length > 0));
            psi.Environment["PATH"] = extra + Path.PathSeparator + psi.Environment["PATH"];
        }

        psi.AddArgument("--mode");
        psi.AddArgument("rpc");

        // Package/extension installs (bun installs, git clones) belong to the user's terminal pi,
        // not to a Rhino panel turn; skipping discovered extensions also cuts seconds off every
        // spawn. MCP support is itself a built-in extension, so it is re-added explicitly or the
        // rhino tools above would never load.
        psi.AddArgument("--no-extensions");
        psi.AddArgument("--extension");
        psi.AddArgument("builtin:mcp");

        // Only the MCP tools: pi's built-in read/bash/edit/write would reach the user's whole
        // machine, while the RhinoAI MCP server is the doc-scoped surface the panel trusts.
        psi.AddArgument("--tools");
        psi.AddArgument(string.Join(",", servers.Select(server => $"mcp__{server.Key}__*")));

        // cmd.exe (the unavoidable host for an npm pi.cmd shim on Windows) cannot carry a multi-line
        // argument, so the composed prompt travels as a file path instead of inline text - pi accepts
        // either for --append-system-prompt.
        psi.AddArgument("--append-system-prompt");
        psi.AddArgument(WriteSystemPrompt(configDir));
        // pi's --session-id opens the existing session with that id or creates it, so fresh spawn
        // and respawn-after-crash pass the same argument (unlike Claude's --session-id/--resume
        // split, the 'resume' flag needs no different spelling here).
        psi.AddArgument("--session-id");
        psi.AddArgument(agentSessionId);

        if (AISettings.EffectiveModel(Definition) is { Length: > 0 } model)
        {
            psi.AddArgument("--model");
            psi.AddArgument(model);
        }
    }

    // ACP content blocks -> one pi RPC prompt line. pi takes a single message string, so text blocks
    // concatenate; images ride the images array as base64 ImageContent. A leading '/' switches to the
    // slash-command path below.
    public string FormatTurn(IReadOnlyList<ContentBlock> prompt)
    {
        List<string> texts = [];
        JsonArray? images = null;
        foreach (ContentBlock block in prompt)
        {
            switch (block)
            {
                case TextContentBlock text:
                    texts.Add(text.Text);
                    break;
                case ImageContentBlock image:
                    images ??= new JsonArray();
                    images.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = image.Data,
                        ["mimeType"] = image.MimeType,
                    });
                    break;
            }
        }

        string message = string.Join("\n\n", texts);
        if (message.StartsWith('/'))
            return FormatSlashCommand(message);

        JsonObject command = new()
        {
            ["type"] = "prompt",
            ["message"] = message,
        };
        if (images is not null)
            command["images"] = images;
        return command.ToJsonString(McpSerializer.Options);
    }

    // ---- slash commands ----------------------------------------------------------------------

    // pi's built-in commands are TUI concepts: sent as prompt text they reach the model as literal
    // words ("/model is a built-in pi command..."). RPC mode has no passthrough for them, so the
    // mappable ones are translated here into their RPC equivalents, and the turn completes on the
    // command's response record instead of agent_settled. Anything else starting with '/' rides the
    // normal prompt path unchanged: extension, skill, and template commands execute natively in pi.
    //
    // PendingCommand is written by FormatTurn on the write path before the line is sent and read by
    // Parse on the read loop only after pi has processed that line, so the handoff needs no lock;
    // the runner serializes turns, so at most one command is ever outstanding.
    private string PendingCommand { get; set; } = string.Empty;
    private string PendingArg { get; set; } = string.Empty;
    private string PendingNote { get; set; } = string.Empty;

    // Built-ins with no RPC equivalent: they need pi's own terminal (pickers, sign-in, exports).
    private static readonly HashSet<string> TerminalOnlyCommands =
    [
        "settings", "scoped-models", "login", "resume", "copy", "export", "share",
        "bug", "trust", "mcp", "reload", "hotkeys",
    ];

    private string FormatSlashCommand(string message)
    {
        string trimmed = message.TrimStart();
        int sp = trimmed.IndexOf(' ');
        string head = (sp < 0 ? trimmed : trimmed[..sp]).TrimStart('/');
        string arg = sp < 0 ? string.Empty : trimmed[(sp + 1)..].Trim();

        JsonObject? command = head switch
        {
            "model" when arg.Length > 0 => SetModel(arg),
            "model" => new JsonObject { ["type"] = "get_available_models" },
            "thinking" when arg.Length > 0 => new JsonObject { ["type"] = "set_thinking_level", ["level"] = arg.ToLowerInvariant() },
            "thinking" => new JsonObject { ["type"] = "get_available_thinking_levels" },
            "new" => new JsonObject { ["type"] = "new_session" },
            "compact" => arg.Length > 0
                ? new JsonObject { ["type"] = "compact", ["customInstructions"] = arg }
                : new JsonObject { ["type"] = "compact" },
            "name" when arg.Length > 0 => new JsonObject { ["type"] = "set_session_name", ["name"] = arg },
            "name" or "session" => new JsonObject { ["type"] = "get_state" },
            _ when TerminalOnlyCommands.Contains(head) => new JsonObject { ["type"] = "get_state" },
            _ => null,
        };
        if (command is null)
        {
            // Not a known built-in: pass it through as a prompt so pi can run it as an extension,
            // skill (/skill:name), or template command, exactly as the TUI would.
            JsonObject passthrough = new() { ["type"] = "prompt", ["message"] = message };
            return passthrough.ToJsonString(McpSerializer.Options);
        }

        PendingCommand = command["type"]?.GetValue<string>() ?? string.Empty;
        PendingArg = arg;
        PendingNote = TerminalOnlyCommands.Contains(head)
            ? $"/{head} needs pi's own terminal (picker, sign-in, or file access); it is not available from the Rhino panel."
            : string.Empty;
        return command.ToJsonString(McpSerializer.Options);
    }

    // "/model provider/id" or "/model id" -> set_model. A bare id is sent without provider and pi's
    // own error text surfaces if it cannot be resolved.
    private static JsonObject SetModel(string arg)
    {
        int slash = arg.IndexOf('/');
        JsonObject command = new() { ["type"] = "set_model" };
        if (slash > 0)
        {
            command["provider"] = arg[..slash];
            command["modelId"] = arg[(slash + 1)..];
        }
        else
        {
            command["modelId"] = arg;
        }
        return command;
    }

    // ---- turn state ---------------------------------------------------------------------------

    // pi splits what Claude's terminal `result` line carries across several records: each assistant
    // message_end holds that response's stop reason/usage/error, and agent_settled (later, after
    // retries or queued work) is the actual terminal event. The read loop is single-threaded and
    // this parser instance belongs to one agent process, so fields carry the turn's tail from the
    // last message_end to the settled line that consumes it. Cleared on agent_start.
    private string LastStopReason { get; set; } = string.Empty;
    private string LastError { get; set; } = string.Empty;
    private TokenUsage LastUsage { get; set; } = TokenUsage.Empty;

    public ParsedLine Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("type", out JsonElement typeEl))
            return ParsedLine.None;

        // Unknown record kinds fall through to ParsedLine.None on purpose: RPC mode interleaves
        // records we do not translate (extension_ui_request, compaction, retry, queue, agent_end),
        // and a new one must never fault the turn. This is a deliberate fail-soft, not an
        // unhandled-case discard.
        return typeEl.GetString() switch
        {
            "message_update" => EmitTextDelta(root),
            "message_end" => ReadAssistantTail(root),
            "tool_execution_start" => EmitToolCall(root),
            "tool_execution_end" => EmitToolResult(root),
            "agent_start" => ResetTurnState(),
            "agent_settled" => CompleteTurn(root),
            "response" => EmitResponse(root),
            _ => ParsedLine.None,
        };
    }

    private static ParsedLine EmitTextDelta(JsonElement root)
    {
        // thinking deltas have no ACP home in this panel (Claude parity: ignored).
        if (!root.TryGetProperty("assistantMessageEvent", out JsonElement evt)
            || Str(evt, "type") != "text_delta")
            return ParsedLine.None;
        string delta = Str(evt, "delta");
        if (delta.Length == 0)
            return ParsedLine.None;
        return ParsedLine.Emit(new AgentMessageChunkSessionUpdate
        {
            Content = new TextContentBlock { Text = delta },
        });
    }

    private ParsedLine ReadAssistantTail(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || Str(message, "role") != "assistant")
            return ParsedLine.None;

        LastStopReason = Str(message, "stopReason");
        LastError = Str(message, "errorMessage");
        LastUsage = ReadUsage(message);
        return ParsedLine.None;
    }

    private static ParsedLine EmitToolCall(JsonElement root)
    {
        // Without toolCallId the later tool_execution_end can never correlate back to this chip, so
        // skip rather than emit a phantom keyed on "" (the absence sentinel, not a real id).
        if (!TryStr(root, "toolCallId", out string toolCallId))
            return ParsedLine.None;
        return ParsedLine.Emit(new ToolCallSessionUpdate
        {
            ToolCallId = toolCallId,
            Title = Str(root, "toolName"),
            // Clone so the element outlives the `using JsonDocument` above.
            RawInput = root.TryGetProperty("args", out JsonElement args) ? args.Clone() : null,
        });
    }

    private static ParsedLine EmitToolResult(JsonElement root)
    {
        if (!TryStr(root, "toolCallId", out string toolCallId))
            return ParsedLine.None;

        // pi's result is a ToolResult object whose content array matches Claude's tool_result
        // content shape; fall back to the whole result when content is absent.
        JsonElement? output = null;
        if (root.TryGetProperty("result", out JsonElement result))
            output = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out JsonElement content)
                ? content.Clone()
                : result.Clone();

        return ParsedLine.Emit(new ToolCallUpdateSessionUpdate
        {
            ToolCallId = toolCallId,
            Status = TryBool(root, "isError") == true ? ToolCallStatus.Failed : ToolCallStatus.Completed,
            RawOutput = output,
        });
    }

    private ParsedLine ResetTurnState()
    {
        LastStopReason = string.Empty;
        LastError = string.Empty;
        LastUsage = TokenUsage.Empty;
        return ParsedLine.None;
    }

    // The terminal event. agent_settled.aborted covers a user cancel; a stashed stopReason of
    // "error" (or an errorMessage) is pi calling the run failed, which must say why rather than end
    // silently; "length" is the output-token ceiling; anything else is a clean EndTurn.
    private ParsedLine CompleteTurn(JsonElement root)
    {
        string stop = LastStopReason;
        string error = LastError;
        TokenUsage usage = LastUsage;
        ResetTurnState();

        if (TryBool(root, "aborted") == true)
            return ParsedLine.Complete(Acp.StopReason.Cancelled, usage);

        if (stop == "error" || error.Length > 0)
        {
            string detail = error.Length > 0 ? error : stop;
            return detail.Length > 0
                ? ParsedLine.Failed(Acp.StopReason.Refusal, new AgentMessageChunkSessionUpdate
                {
                    Content = new TextContentBlock { Text = $"{DisplayName} failed: {detail}" },
                })
                : ParsedLine.Complete(Acp.StopReason.Refusal, usage);
        }

        return stop switch
        {
            "length" => ParsedLine.Complete(Acp.StopReason.MaxTokens, usage),
            _ => ParsedLine.Complete(Acp.StopReason.EndTurn, usage),
        };
    }

    // One response record: a resolved slash command when one is outstanding, otherwise a rejected
    // prompt command (the one response worth translating: the run never starts, so no agent_settled
    // would ever arrive and the turn would hang until process exit). Signing-in and config problems
    // land in the prompt branch as success:false with an error string.
    private ParsedLine EmitResponse(JsonElement root)
    {
        if (PendingCommand.Length > 0 && Str(root, "command") == PendingCommand)
            return ResolveCommandResponse(root);
        return EmitRejectedPrompt(root);
    }

    // The response to a translated slash command ends the turn with a human note. Cleared before
    // building the note so an exception mid-format can never leave a stale pending command eating
    // the next unrelated response.
    private ParsedLine ResolveCommandResponse(JsonElement root)
    {
        string command = PendingCommand;
        string arg = PendingArg;
        string note = PendingNote;
        PendingCommand = PendingArg = PendingNote = string.Empty;
        root.TryGetProperty("data", out JsonElement data);

        if (TryBool(root, "success") != true)
        {
            string error = Str(root, "error");
            return Note($"/{command} failed{(error.Length > 0 ? $": {error}" : ".")}", failed: true);
        }

        // A pending note (terminal-only built-ins) replaces any data rendering.
        if (note.Length > 0)
            return Note(note);

        return command switch
        {
            "set_model" => Note($"Model set to {arg}."),
            "set_thinking_level" => Note($"Thinking level set to {arg}."),
            "set_session_name" => Note($"Session name set to {arg}."),
            "new_session" => TryBool(data, "cancelled") == true
                ? Note("New session was cancelled by an extension.")
                : Note("Started a new pi session."),
            "compact" => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("estimatedTokensAfter", out JsonElement after)
                ? Note($"Compacted the session (~{after.GetInt32():N0} tokens).")
                : Note("Compacted the session."),
            "get_available_models" => ModelList(data),
            "get_available_thinking_levels" => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("levels", out JsonElement levels)
                ? Note($"Thinking levels: {string.Join(", ", levels.EnumerateArray().Select(static l => l.GetString() ?? string.Empty))}.")
                : Note("No thinking levels available for this model."),
            "get_state" => SessionState(data),
            _ => Note($"/{command} done."),
        };
    }

    private static ParsedLine ModelList(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("models", out JsonElement models)
            || models.ValueKind != JsonValueKind.Array)
            return Note("No models are configured.");
        List<string> lines = [];
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
                continue;
            string id = Str(model, "id");
            string name = Str(model, "name");
            string provider = Str(model, "provider");
            string label = name.Length > 0 ? name : id;
            string full = provider.Length > 0 ? $"{provider}/{id}" : id;
            lines.Add($"- {label} ({full})");
        }
        return lines.Count > 0
            ? Note($"Available models:\n{string.Join("\n", lines)}\nSwitch with /model <provider/id>.")
            : Note("No models are configured.");
    }

    private static ParsedLine SessionState(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return Note("No session state available.");
        string name = Str(data, "sessionName");
        string model = data.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.Object
            ? Str(m, "name") is { Length: > 0 } n ? n : Str(m, "id")
            : string.Empty;
        string id = Str(data, "sessionId");
        string messages = data.TryGetProperty("messageCount", out JsonElement count) && count.ValueKind == JsonValueKind.Number
            ? $" · {count.GetInt32():N0} messages"
            : string.Empty;
        string shownName = name.Length > 0 ? name : "(unnamed)";
        string shownModel = model.Length > 0 ? model : "(none)";
        return Note($"Session: {shownName}{messages}\nModel: {shownModel}\nId: {id}");
    }

    // A status note IS the whole turn: one assistant chunk plus completion, so the panel shows the
    // note and releases the turn without waiting for an agent_settled that will never come.
    private static ParsedLine Note(string text, bool failed = false)
    {
        AgentMessageChunkSessionUpdate chunk = new()
        {
            Content = new TextContentBlock { Text = text },
        };
        return failed
            ? ParsedLine.Failed(Acp.StopReason.Refusal, chunk)
            : new ParsedLine([chunk], true, Acp.StopReason.EndTurn, TokenUsage.Empty, null);
    }

    // A rejected prompt command is the one response worth translating: the run never starts, so no
    // agent_settled would ever arrive and the turn would hang until process exit. Signing-in and
    // config problems land here (pi reports them as success:false with an error string).
    private ParsedLine EmitRejectedPrompt(JsonElement root)
    {
        if (Str(root, "command") != "prompt" || TryBool(root, "success") != false)
            return ParsedLine.None;

        string detail = Str(root, "error");
        return ParsedLine.Failed(Acp.StopReason.Refusal, new AgentMessageChunkSessionUpdate
        {
            Content = new TextContentBlock { Text = $"{DisplayName} failed: {detail}" },
        });
    }

    // The assistant message's usage object: input/output token counts plus an optional cost.total
    // (dollars). Every field is best-effort: a message without usage degrades to TokenUsage.Empty
    // rather than faulting the turn. Per-response in pi (not per-run like Claude's result), so a
    // multi-turn tool loop reports the final response's counts.
    private static TokenUsage ReadUsage(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
            return TokenUsage.Empty;

        int input = ReadInt(usage, "input");
        int output = ReadInt(usage, "output");
        decimal? cost = usage.TryGetProperty("cost", out JsonElement c)
            && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("total", out JsonElement t)
            && t.ValueKind == JsonValueKind.Number
            && t.TryGetDecimal(out decimal d)
            ? d
            : null;
        return new TokenUsage(input, output, cost);
    }

    // ---- scratch config -----------------------------------------------------------------------

    // pi has no --mcp-config flag: it reads mcp.json from its config dir. Redirect the whole dir to
    // a per-session scratch copy so this doc's rhino URL never thrashes the user's real ~/.pi/agent
    // (two open docs would otherwise fight over the reserved 'rhino' key), and seed the identity
    // files so OAuth/API-key logins survive the redirect.
    private static DirectoryInfo PrepareConfigDir(string agentSessionId, JsonObject servers)
    {
        DirectoryInfo dir = new(Path.Combine(Path.GetTempPath(), "RhinoAI", "pi", agentSessionId));
        dir.Create();

        foreach (string file in (string[])["settings.json", "auth.json", "models.json"])
        {
            try
            {
                string source = Path.Combine(UserPiAgentDir, file);
                if (!File.Exists(source))
                    continue;
                if (file == "settings.json" && StripSessionScope(source) is string stripped)
                {
                    File.WriteAllText(Path.Combine(dir.FullName, file), stripped);
                    continue;
                }
                File.Copy(source, Path.Combine(dir.FullName, file), overwrite: true);
            }
            catch (IOException)
            {
                // A locked or half-written source file is skipped: pi still runs, with defaults.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        File.WriteAllText(Path.Combine(dir.FullName, "mcp.json"),
            new JsonObject { ["mcpServers"] = servers }.ToJsonString(McpSerializer.Options));
        return dir;
    }

    private static string UserPiAgentDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");

    // The user's real settings carry identity (default provider/model, keys) that must survive the
    // scratch redirect, but also session scope that must not: `packages` and `extensions` would git
    // clone and bun install the whole personal setup into a throwaway temp dir on first spawn.
    // Returns null when the file cannot be read or parsed, so the caller falls back to a plain copy.
    private static string? StripSessionScope(string source)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(source)) is not JsonObject settings)
                return null;
            settings.Remove("packages");
            settings.Remove("extensions");
            return settings.ToJsonString(McpSerializer.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string WriteSystemPrompt(DirectoryInfo configDir)
    {
        string path = Path.Combine(configDir.FullName, "system-prompt.txt");
        File.WriteAllText(path, AgentPrompts.Compose(AISettings.EffectivePrompt(Definition)));
        return path;
    }

    private static DirectoryInfo SessionStoreDir { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RhinoAI", "pi-sessions"));

    // ---- json helpers -------------------------------------------------------------------------

    // JSON-boundary string read. Str collapses the TryGetProperty+GetString+?? spray to one place for
    // display-only fields (a missing/non-string value is "" and that is fine downstream). TryStr is
    // for fields where absence is meaningful (tool ids): false means "not present", never a silent "".
    private static string Str(JsonElement obj, string name) => TryStr(obj, name, out string value) ? value : string.Empty;

    private static bool TryStr(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool? TryBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;
}
