using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SpeakRect
{
    /// <summary>
    /// Optional name packs for Speech → Names (phonetic TTS substitutions).
    /// Packs live in <c>NamePacks\</c> next to SpeakRect.exe — never auto-applied
    /// at startup (user picks via Packs…). Import enables each new rule and
    /// keeps the Names list A–Z by Find. Matching is always case-insensitive
    /// (engine: <see cref="SpeechRulesEngine"/>).
    /// </summary>
    public static class SpeechNamePacks
    {
        public sealed record PackInfo(
            string Id,
            string DisplayName,
            string Description,
            string FilePath,
            int RuleCount = 0);

        /// <summary>Folder next to the exe: <c>NamePacks\</c>.</summary>
        public static string PacksDir =>
            Path.Combine(AppSettings.AppDir, "NamePacks");

        /// <summary>
        /// Discover pack files under <see cref="PacksDir"/> (*.txt).
        /// Missing / empty folder → empty list (create on demand is caller's job).
        /// Skips docs (README / LICENSE / …). Never applies rules — UI import only.
        /// </summary>
        public static IReadOnlyList<PackInfo> Discover()
        {
            var list = new List<PackInfo>();
            string dir = PacksDir;
            try
            {
                if (!Directory.Exists(dir))
                    return list;
                foreach (string path in Directory.GetFiles(dir, "*.txt")
                             .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                {
                    if (IsDocumentationFile(path))
                        continue;
                    if (TryReadPackInfo(path, out var info) && info != null)
                        list.Add(info);
                }
            }
            catch
            {
                // Missing / locked folder — treat as no packs.
            }
            return list;
        }

        /// <summary>
        /// True for companion docs that live next to packs but are not importable
        /// (e.g. <c>README.txt</c>).
        /// </summary>
        public static bool IsDocumentationFile(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path) ?? "";
            if (stem.Length == 0)
                return true;
            return stem.Equals("README", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("LICENCE", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("NOTICE", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("THIRD_PARTY_NOTICES", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("HISTORY", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Ensure <see cref="PacksDir"/> exists so users can drop custom packs in.
        /// Returns the directory path (may still be missing if create failed).
        /// </summary>
        public static string EnsurePacksDir()
        {
            string dir = PacksDir;
            try { Directory.CreateDirectory(dir); }
            catch { /* best-effort */ }
            return dir;
        }

        /// <summary>Known packs (alias of <see cref="Discover"/> for UI).</summary>
        public static IReadOnlyList<PackInfo> All => Discover();

        /// <summary>
        /// Load a pack by id (filename stem or header Id=) or absolute/relative path.
        /// Unknown / unreadable → empty list. All rules disabled.
        /// </summary>
        public static List<SpeechRule> Create(string? packIdOrPath)
        {
            if (string.IsNullOrWhiteSpace(packIdOrPath))
                return new List<SpeechRule>();

            string key = packIdOrPath.Trim();

            // Absolute / existing path first.
            if (File.Exists(key))
                return LoadFile(key);

            // Relative under PacksDir.
            string underPacks = Path.Combine(PacksDir, key);
            if (File.Exists(underPacks))
                return LoadFile(underPacks);
            if (!key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                string withExt = underPacks + ".txt";
                if (File.Exists(withExt))
                    return LoadFile(withExt);
            }

            // Match by header Id= or filename stem (case-insensitive).
            foreach (var pack in Discover())
            {
                if (pack.Id.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(pack.FilePath)
                        .Equals(key, StringComparison.OrdinalIgnoreCase))
                    return LoadFile(pack.FilePath);
            }

            return new List<SpeechRule>();
        }

        /// <summary>
        /// Merge pack into existing rules. Skips Find text already present (cleaned match).
        /// New rules are <c>Enabled = true</c>. After merge the whole list is sorted
        /// A–Z by Find (ignore case) so users can scan for a name.
        /// Returns how many were added.
        /// </summary>
        public static int MergeInto(
            IList<SpeechRule> target,
            string packIdOrPath,
            out int skippedDuplicates)
        {
            skippedDuplicates = 0;
            var pack = Create(packIdOrPath);
            if (pack.Count == 0 || target == null)
                return 0;

            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in target)
            {
                string key = SpeechRulesEngine.ToCleanedLookup(r?.Match);
                if (key.Length > 0)
                    have.Add(key);
            }

            int added = 0;
            foreach (var r in pack)
            {
                string key = SpeechRulesEngine.ToCleanedLookup(r.Match);
                if (key.Length == 0)
                    continue;
                if (!have.Add(key))
                {
                    skippedDuplicates++;
                    continue;
                }
                r.Enabled = true;
                target.Add(r);
                added++;
            }

            if (added > 0)
                SortByMatch(target);

            return added;
        }

        /// <summary>Sort name rules A–Z by Find (case-insensitive). In-place.</summary>
        public static void SortByMatch(IList<SpeechRule> rules)
        {
            if (rules == null || rules.Count < 2)
                return;

            var sorted = rules
                .Where(r => r != null)
                .OrderBy(r => r.Match ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Replace ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            rules.Clear();
            foreach (var r in sorted)
                rules.Add(r);
        }

        /// <summary>
        /// Parse a pack file. Header keys: Id=, Name=, Description=.
        /// Rule lines: <c>Find | Say as</c> or <c>Find | Say as | Word|Phrase</c>.
        /// Also accepts tab-separated and <c>Find = Say as</c>.
        /// Matching is always case-insensitive at speak time.
        /// </summary>
        public static List<SpeechRule> LoadFile(string path)
        {
            var rules = new List<SpeechRule>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return rules;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch
            {
                return rules;
            }

            foreach (string raw in lines)
            {
                if (!TryParseRuleLine(raw, out string match, out string replace, out SpeechMatchKind kind))
                    continue;
                if (!SpeechRule.TryNormalize(match, replace, kind, enabled: true, out SpeechRule rule, out _))
                    continue;
                rules.Add(rule);
            }

            // Browse-friendly A–Z (engine still prefers longer Finds when speaking).
            rules.Sort((a, b) =>
            {
                int c = string.Compare(a.Match, b.Match, StringComparison.OrdinalIgnoreCase);
                return c != 0
                    ? c
                    : string.Compare(a.Replace, b.Replace, StringComparison.OrdinalIgnoreCase);
            });
            return rules;
        }

        /// <summary>
        /// Read pack metadata + approximate rule count in one pass (no rules applied).
        /// </summary>
        private static bool TryReadPackInfo(string path, out PackInfo? info)
        {
            info = null;
            string stem = Path.GetFileNameWithoutExtension(path) ?? "pack";
            string id = stem;
            string name = HumanizeStem(stem);
            string description = "Custom name pack.";
            int ruleCount = 0;
            bool inHeader = true;

            try
            {
                foreach (string raw in File.ReadLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0)
                        continue;
                    if (line.StartsWith(';') || line.StartsWith('#'))
                        continue;

                    if (inHeader)
                    {
                        // Header keys: Id= Name= Description= only; anything else ends header.
                        if (line.Contains('|') || line.Contains('\t'))
                        {
                            inHeader = false;
                        }
                        else
                        {
                            int eq = line.IndexOf('=');
                            if (eq <= 0)
                            {
                                inHeader = false;
                            }
                            else
                            {
                                string key = line[..eq].Trim();
                                string val = line[(eq + 1)..].Trim();
                                if (key.Equals("Id", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                                {
                                    id = val;
                                    continue;
                                }
                                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                                {
                                    name = val;
                                    continue;
                                }
                                if ((key.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
                                     key.Equals("Desc", StringComparison.OrdinalIgnoreCase)) && val.Length > 0)
                                {
                                    description = val;
                                    continue;
                                }
                                // First non-header key=value (Find=Say form) ends header.
                                inHeader = false;
                            }
                        }
                    }

                    if (TryParseRuleLine(raw, out string match, out string replace, out SpeechMatchKind kind) &&
                        SpeechRule.TryNormalize(match, replace, kind, enabled: false, out _, out _))
                        ruleCount++;
                }
            }
            catch
            {
                // Still expose the file so user can try import.
            }

            info = new PackInfo(id, name, description, path, ruleCount);
            return true;
        }

        private static string HumanizeStem(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem))
                return "Pack";
            // x-men → X-Men-ish: keep as-is but title-ish first char
            string t = stem.Trim().Replace('_', ' ');
            if (t.Length == 0)
                return "Pack";
            return char.ToUpperInvariant(t[0]) + t[1..];
        }

        /// <summary>
        /// Parse one rule line. Returns false for comments, blanks, and header keys.
        /// </summary>
        internal static bool TryParseRuleLine(
            string? raw,
            out string match,
            out string replace,
            out SpeechMatchKind kind)
        {
            match = "";
            replace = "";
            kind = SpeechMatchKind.Word;

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#'))
                return false;

            // Header metadata — not a rule.
            if (IsHeaderKeyLine(line))
                return false;

            // Prefer pipe: Find | Say as [| Kind]
            if (line.Contains('|'))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 2)
                    return false;
                match = parts[0].Trim();
                replace = parts[1].Trim();
                if (parts.Length >= 3 && SpeechRule.TryParseKind(parts[2].Trim(), out var k))
                    kind = k;
                return match.Length > 0;
            }

            // Tab: Find \t Say as [\t Kind]
            if (line.Contains('\t'))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 2)
                    return false;
                match = parts[0].Trim();
                replace = parts[1].Trim();
                if (parts.Length >= 3 && SpeechRule.TryParseKind(parts[2].Trim(), out var k))
                    kind = k;
                return match.Length > 0;
            }

            // Equals: Find = Say as  (first = only; replace may contain =)
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                match = line[..eq].Trim();
                replace = line[(eq + 1)..].Trim();
                // Reject known header keys already handled, and empty find.
                if (match.Length == 0)
                    return false;
                if (IsReservedHeaderKey(match))
                    return false;
                return true;
            }

            return false;
        }

        private static bool IsHeaderKeyLine(string line)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
                return false;
            // Only treat as header when there is no | / tab (those are rule forms).
            if (line.Contains('|') || line.Contains('\t'))
                return false;
            string key = line[..eq].Trim();
            return IsReservedHeaderKey(key);
        }

        private static bool IsReservedHeaderKey(string key) =>
            key.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Desc", StringComparison.OrdinalIgnoreCase);
    }
}
