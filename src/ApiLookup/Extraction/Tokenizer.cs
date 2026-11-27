using System.Text.RegularExpressions;

namespace ApiLookup;

public static partial class Tokenizer
{
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex AlnumChunkRegex();

    public static bool IsCjk(char c) =>
        c is (>= '\u4E00' and <= '\u9FFF')
          or (>= '\u3400' and <= '\u4DBF')
          or (>= '\uF900' and <= '\uFAFF')
          or (>= '\u3040' and <= '\u30FF')
          or (>= '\uAC00' and <= '\uD7AF');

    /// <summary>
    /// Expands C# identifiers and CJK phrases into FTS5-friendly search tokens:
    /// e.g. "CreateAutoCADOffScreenDevice" ->
    /// "CreateAutoCADOffScreenDevice Create Auto CAD Off Screen Device CreateAuto AutoCAD CADOff OffScreen ScreenDevice"
    /// and "三维管道段" -> "三维管道段 三维 维管 管道 道段 段".
    /// </summary>
    public static string BuildSearchTokens(params string?[] inputs)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void Add(string t)
        {
            if (t.Length == 0) return;
            if (tokens.Add(t)) ordered.Add(t);
        }

        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (Match m in AlnumChunkRegex().Matches(raw))
            {
                foreach (var (seg, isCjk) in SplitUnicodeChunk(m.Value))
                {
                    if (isCjk)
                    {
                        Add(seg);
                        if (seg.Length >= 2)
                        {
                            for (var i = 0; i + 1 < seg.Length; i++)
                                Add(seg.Substring(i, 2));
                            // Emit trailing char so single-char prefix queries ("段"*) match every character position
                            Add(seg[^1..]);
                        }
                    }
                    else
                    {
                        Add(seg);
                        var parts = SplitCamelCase(seg);
                        if (parts.Count > 1)
                        {
                            foreach (var p in parts) Add(p);
                            // Adjacent pair joins so "offscreen" matches "OffScreen" and "autocad" matches "AutoCAD"
                            for (var i = 0; i + 1 < parts.Count; i++)
                                Add(parts[i] + parts[i + 1]);
                        }
                    }
                }
            }
        }

        return string.Join(' ', ordered);
    }

    /// <summary>
    /// Extracts CJK segments, their 2-character bigrams, and trailing char from an XML summary when CJK characters are present.
    /// Returns empty string for pure non-CJK summaries so English-only indexes incur zero bloat.
    /// </summary>
    public static string BuildCjkSummaryTokens(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return string.Empty;

        var hasCjk = false;
        foreach (var ch in summary)
        {
            if (IsCjk(ch))
            {
                hasCjk = true;
                break;
            }
        }
        if (!hasCjk) return string.Empty;

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        void Add(string t)
        {
            if (t.Length > 0 && tokens.Add(t))
                ordered.Add(t);
        }

        foreach (Match m in AlnumChunkRegex().Matches(summary))
        {
            foreach (var (seg, isCjk) in SplitUnicodeChunk(m.Value))
            {
                if (!isCjk) continue;
                Add(seg);
                if (seg.Length >= 2)
                {
                    for (var i = 0; i + 1 < seg.Length; i++)
                        Add(seg.Substring(i, 2));
                    Add(seg[^1..]);
                }
            }
        }

        return ordered.Count == 0 ? string.Empty : string.Join(' ', ordered);
    }

    public static List<(string Text, bool IsCjk)> SplitUnicodeChunk(string chunk)
    {
        var result = new List<(string Text, bool IsCjk)>();
        if (string.IsNullOrEmpty(chunk)) return result;

        var start = 0;
        var currentIsCjk = IsCjk(chunk[0]);
        for (var i = 1; i < chunk.Length; i++)
        {
            var isCjk = IsCjk(chunk[i]);
            if (isCjk != currentIsCjk)
            {
                result.Add((chunk[start..i], currentIsCjk));
                start = i;
                currentIsCjk = isCjk;
            }
        }
        result.Add((chunk[start..], currentIsCjk));
        return result;
    }

    public static List<string> SplitCamelCase(string input)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(input)) return parts;

        var start = 0;
        for (var i = 1; i < input.Length; i++)
        {
            var prev = input[i - 1];
            var curr = input[i];
            var next = i + 1 < input.Length ? input[i + 1] : '\0';

            var split =
                (char.IsLower(prev) && char.IsUpper(curr)) ||
                (char.IsUpper(prev) && char.IsUpper(curr) && next != '\0' && char.IsLower(next)) ||
                (char.IsLetter(prev) && char.IsDigit(curr)) ||
                (char.IsDigit(prev) && char.IsLetter(curr));

            if (split)
            {
                parts.Add(input[start..i]);
                start = i;
            }
        }
        parts.Add(input[start..]);
        return parts;
    }

    /// <summary>
    /// Converts a user search string into a safe FTS5 MATCH expression.
    /// Returns null if the query contains no searchable alphanumeric or CJK tokens.
    /// </summary>
    public static string? BuildFtsQuery(string query)
    {
        var clauses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in AlnumChunkRegex().Matches(query))
        {
            foreach (var (seg, isCjk) in SplitUnicodeChunk(m.Value))
            {
                if (isCjk)
                {
                    if (seg.Length >= 2)
                    {
                        var bgTerms = new List<string>();
                        for (var i = 0; i + 1 < seg.Length; i++)
                        {
                            var bg = seg.Substring(i, 2);
                            if (seen.Add("cjk:" + bg))
                                bgTerms.Add($"\"{EscapeFts(bg)}\"");
                        }
                        if (bgTerms.Count > 0)
                            clauses.Add(string.Join(" AND ", bgTerms));
                    }
                    else if (seen.Add("cjk:" + seg))
                    {
                        clauses.Add($"\"{EscapeFts(seg)}\"*");
                    }
                }
                else
                {
                    var parts = SplitCamelCase(seg);
                    // Avoid expanding 1-char prefixes (e.g. "IList" -> "I"* AND "List"*) which scan every 'i*' word in FTS5
                    if (parts.Count > 1 && parts.All(p => p.Length >= 2))
                    {
                        if (seen.Add(seg))
                        {
                            var subTerms = parts.Select(p => $"\"{EscapeFts(p)}\"*");
                            clauses.Add($"(\"{EscapeFts(seg)}\"* OR ({string.Join(" AND ", subTerms)}))");
                        }
                    }
                    else if (seen.Add(seg))
                    {
                        clauses.Add($"\"{EscapeFts(seg)}\"*");
                    }
                }
            }
        }

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    private static string EscapeFts(string term) => term.Replace("\"", "\"\"");
}
