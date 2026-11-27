using System.Text;
using System.Text.RegularExpressions;

namespace ApiLookup;

public static partial class Tokenizer
{
    [GeneratedRegex(@"[A-Za-z0-9]+")]
    private static partial Regex AlnumChunkRegex();

    /// <summary>
    /// Expands C# identifiers into FTS5-friendly search tokens:
    /// e.g. "CreateAutoCADOffScreenDevice" ->
    /// "CreateAutoCADOffScreenDevice create auto cad off screen device createauto autocad cadoff offscreen screendevice"
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
                var chunk = m.Value;
                Add(chunk);
                var parts = SplitCamelCase(chunk);
                if (parts.Count > 1)
                {
                    foreach (var p in parts) Add(p);
                    // Adjacent pair joins so "offscreen" matches "OffScreen" and "autocad" matches "AutoCAD"
                    for (var i = 0; i + 1 < parts.Count; i++)
                        Add(parts[i] + parts[i + 1]);
                }
            }
        }

        return string.Join(' ', ordered);
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
    /// Returns null if the query contains no searchable alphanumeric tokens.
    /// </summary>
    public static string? BuildFtsQuery(string query)
    {
        var clauses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in AlnumChunkRegex().Matches(query))
        {
            var chunk = m.Value;
            var parts = SplitCamelCase(chunk);
            if (parts.Count > 1)
            {
                // Match either the whole chunk prefix OR all its CamelCase parts
                var subTerms = parts.Select(p => $"\"{EscapeFts(p)}\"*");
                clauses.Add($"(\"{EscapeFts(chunk)}\"* OR ({string.Join(" AND ", subTerms)}))");
            }
            else if (seen.Add(chunk))
            {
                clauses.Add($"\"{EscapeFts(chunk)}\"*");
            }
        }

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    private static string EscapeFts(string term) => term.Replace("\"", "\"\"");
}
