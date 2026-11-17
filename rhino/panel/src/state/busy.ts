import type { StringKey } from '../i18n/strings.js';

// One fixed word while a reply streams: the panel is a tool surface, not entertainment.
export const BUSY_KEYS: readonly StringKey[] = [
  'busy.working',
];

export const WORD_MS = 4000;

export function busyWord(elapsedMs: number, offset = 0, keys: readonly StringKey[] = BUSY_KEYS): StringKey | null {
  if (keys.length === 0) return null;
  const step = Math.floor(Math.max(0, elapsedMs) / WORD_MS);
  const index = (((offset + step) % keys.length) + keys.length) % keys.length;
  return keys[index] as StringKey;
}
