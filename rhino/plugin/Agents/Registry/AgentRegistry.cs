using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

using Rhino.Runtime;

namespace Rhino.AI;

/// <summary>
/// Registry of Agents, their models and everything related.
/// </summary>
internal class AgentRegistry
{

    private List<AgentDefinition> BuiltInDefinitions { get; } = [];
    private List<AgentDefinition> CustomDefinitions { get; } = [];
    public IReadOnlyList<AgentDefinition> AllDefinitions
        => BuiltInDefinitions.Union(CustomDefinitions).ToList();

    public static AgentRegistry Instance { get; } = new();

    public AgentRegistry()
    {
        LoadDefinitions();
        _ = FillPiModelsAsync();
    }

    private sealed record AgentDefinitions(List<AgentDefinition>  Definitions);
    private void LoadDefinitions()
    {
        using Stream? stream = typeof(AgentRegistry).Assembly.GetManifestResourceStream("Rhino.AI.Agents.Definitions.json");
        if (stream is null) return;
        using StreamReader reader = new(stream);

        string json = reader.ReadToEnd();

        try
        {
            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                IgnoreReadOnlyFields = false,
                IgnoreReadOnlyProperties = false,
                IncludeFields = true,
            };

            // Build In's
            AgentDefinitions? definitions = JsonSerializer.Deserialize<AgentDefinitions>(json, options);
            if (definitions is not null)
            {
                BuiltInDefinitions.AddRange(definitions. Definitions);
            }
        }
        catch (Exception ex)
        {
            HostUtils.LogDebugEvent($"Failed to load Agent Definitions {ex.Message}.\n");
        }
    }

    public bool TryGet(string name, out AgentDefinition def)
    {
        foreach (AgentDefinition definition in AllDefinitions)
        {
            if (!string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            def = definition;
            return true;
        }

        def = default!;
        return false;
    }

    // Pi's Definitions.json ships without a model list on purpose: which models exist depends on the
    // user's own pi logins and model catalog. `pi --list-models` prints them as a whitespace table
    // (provider, model, then capability columns), so the Pi definition is filled in the background
    // once the registry loads; the settings dialog and the panel read the live list whenever they
    // open. A failed run leaves the shipped (default)-only list in place rather than faulting startup.
    private static async Task FillPiModelsAsync()
    {
        try
        {
            AgentDefinition? pi = null;
            foreach (AgentDefinition definition in Instance.AllDefinitions)
                if (string.Equals(definition.Name, "Pi", StringComparison.OrdinalIgnoreCase))
                    pi = definition;
            if (pi is null) return;

            if (!CliProcess.TryResolve(pi.SearchPaths.GetPaths(), out string path)) return;

            ProcessStartInfo psi = new()
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            CliProcess.ConfigureFileName(psi, path);
            // A GUI-launched Rhino hands children a minimal PATH; the node homes the pi shim needs
            // are prepended exactly like the Pi parser does for its own spawn.
            if (OperatingSystem.IsMacOS())
            {
                string piDir = Path.GetDirectoryName(path) ?? string.Empty;
                string extra = string.Join(Path.PathSeparator.ToString(), new[]
                {
                    piDir,
                    "/opt/homebrew/bin",
                    "/usr/local/bin",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin"),
                }.Where(static d => d.Length > 0));
                psi.Environment["PATH"] = extra + Path.PathSeparator + psi.Environment["PATH"];
            }
            psi.AddArgument("--list-models");
            CliProcess.ConfigureEncoding(psi);

            using Process? proc = Process.Start(psi);
            if (proc is null) return;
            string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            List<ModelSpec> models = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string raw in output.Split('\n'))
            {
                string[] cols = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 2 || (cols[0] == "provider" && cols[1] == "model")) continue;
                // The full provider/model id is what pi's --model expects; the display stays short and
                // only carries the provider when two providers share a model name.
                string id = $"{cols[0]}/{cols[1]}";
                string display = seen.Add(cols[1]) ? cols[1] : $"{cols[1]} ({cols[0]})";
                models.Add(new ModelSpec(display, id));
            }
            if (models.Count > 0)
                pi.Models = models;
        }
        catch (Exception ex)
        {
            HostUtils.LogDebugEvent($"[Pi] model list unavailable: {ex.Message}.\n");
        }
    }
}
