import type { AudioDevice } from '@betterxeneon/shared';

/**
 * Shortens a list of device names by detecting common prefixes (e.g. "SteelSeries Sonar - ")
 * and replacing them with an abbreviation derived from the first PascalCase word's caps.
 *
 * SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device) → "SS - Gaming"
 * SteelSeries Sonar - Aux (SteelSeries Sonar Virtual Audio Device)    → "SS - Aux"
 * Speakers (4- SteelSeries Arena 7)                                    → "Speakers (Arena 7)"
 *
 * Devices in a group of 1 are kept as-is (with trailing parens trimmed if very long).
 */
export function shortenDeviceNames(devices: AudioDevice[]): Map<string, string> {
  const result = new Map<string, string>();
  if (devices.length === 0) return result;

  // 1) Strip trailing parenthesized "redundant" suffixes for grouping purposes.
  //    Keep only the first parens if they look like real disambiguation (short).
  const stripped = devices.map(d => ({
    id: d.id,
    full: d.name,
    base: stripRedundantSuffix(d.name),
  }));

  // 2) Sort by base name so adjacent items share prefixes.
  const sorted = [...stripped].sort((a, b) => a.base.localeCompare(b.base));

  // 3) Group adjacent items whose pairwise LCP is long enough.
  const MIN_PREFIX_CHARS = 8;
  type Group = { commonPrefix: string; items: typeof sorted };
  const groups: Group[] = [];
  for (const item of sorted) {
    const last = groups.at(-1);
    if (last) {
      const lcp = longestCommonPrefix(last.commonPrefix, item.base);
      if (lcp.length >= MIN_PREFIX_CHARS) {
        last.commonPrefix = lcp;
        last.items.push(item);
        continue;
      }
    }
    groups.push({ commonPrefix: item.base, items: [item] });
  }

  // 4) Apply abbreviation per group.
  for (const group of groups) {
    if (group.items.length === 1) {
      const only = group.items[0]!;
      result.set(only.id, prettifySingleton(only.base));
      continue;
    }

    const prefix = trimToBoundary(group.commonPrefix);
    const abbrev = abbreviateFirstWord(prefix);

    for (const item of group.items) {
      const remainder = item.base.slice(prefix.length).replace(/^[\s\-–—:|]+/, '').trim();
      result.set(item.id, remainder ? `${abbrev} - ${remainder}` : prettifySingleton(item.base));
    }
  }

  return result;
}

const FILLER_WORDS = new Set(['virtual', 'audio', 'device', 'sound', 'output', 'input', 'playback', 'speaker', 'speakers']);

function stripRedundantSuffix(name: string): string {
  // Drop trailing parens that are decorative (only repeat the head's brand words +
  // generic filler like "Virtual Audio Device"). Keep parens that introduce real
  // new info, like "(Plantronics Voyager Focus)" on a generic "Headphones" device.
  const m = name.match(/^(.*?)\s*\(([^)]*)\)\s*$/);
  if (!m) return name.trim();
  const [, head, inner] = m;
  if (!head || !inner) return name.trim();

  const headTokens = new Set(tokens(head));
  const innerTokens = tokens(inner);
  if (innerTokens.length === 0) return head.trim();

  const repeated = innerTokens.filter(t => headTokens.has(t)).length;
  const filler = innerTokens.filter(t => FILLER_WORDS.has(t) && !headTokens.has(t)).length;
  const meaningful = innerTokens.length - repeated - filler;

  // Drop if (a) at least one repeat AND nothing genuinely new, or (b) the parens
  // are >= 70% repeats+filler.
  if (repeated >= 1 && meaningful === 0) return head.trim();
  if ((repeated + filler) / innerTokens.length >= 0.7) return head.trim();

  return name.trim();
}

function tokens(s: string): string[] {
  return s
    .toLowerCase()
    .replace(/[^a-z0-9 ]+/g, ' ')
    .split(/\s+/)
    .filter(t => t.length > 1);
}

function longestCommonPrefix(a: string, b: string): string {
  const len = Math.min(a.length, b.length);
  let i = 0;
  while (i < len && a[i] === b[i]) i++;
  return a.slice(0, i);
}

function trimToBoundary(prefix: string): string {
  // Trim back to the last word/separator boundary so we don't cut mid-word.
  const m = prefix.match(/^(.*?[\s\-–—:|])\s*$/);
  if (m && m[1]) return m[1].replace(/[\s\-–—:|]+$/, '').trim();
  const lastSpace = prefix.lastIndexOf(' ');
  if (lastSpace > 0) return prefix.slice(0, lastSpace).trim();
  return prefix.trim();
}

function abbreviateFirstWord(prefix: string): string {
  const firstWord = (prefix.split(/\s+/).find(Boolean) ?? '').replace(/[^A-Za-z0-9]/g, '');
  // PascalCase / camelCase: extract uppercase letters.
  const caps = firstWord.match(/[A-Z]/g)?.join('') ?? '';
  if (caps.length >= 2) return caps;
  // Otherwise: first 3 chars uppercased.
  return firstWord.slice(0, 3).toUpperCase() || 'AUD';
}

function prettifySingleton(name: string): string {
  // For unique device names, strip the leading "N- " prefix Windows adds for indexed
  // duplicates ("4- SteelSeries Arena 7") and shorten anything in parens that's
  // dominated by "Audio Device" / "Virtual" filler.
  const trailingParens = name.match(/^(.*?)\s*\(([^)]*)\)\s*$/);
  if (trailingParens) {
    const [, head, inner] = trailingParens;
    if (head && inner) {
      const cleanedInner = inner.replace(/^\d+-\s*/, '').trim();
      const condensed = cleanedInner
        .replace(/\b(Virtual|Audio|Device|Sound|Output)\b/gi, '')
        .replace(/\s+/g, ' ')
        .trim();
      if (condensed.length > 0 && condensed.length < cleanedInner.length) {
        return `${head.trim()} (${condensed})`;
      }
      return `${head.trim()} (${cleanedInner})`;
    }
  }
  return name.trim();
}
