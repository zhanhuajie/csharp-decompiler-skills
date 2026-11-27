using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ApiLookup;

public sealed record XmlDocEntry(
    string? Summary,
    string? Remarks,
    string? Returns,
    IReadOnlyDictionary<string, string> Params);

public sealed class XmlDocBook
{
    public static readonly XmlDocBook Empty = new(
        new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal),
        new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal));

    private readonly Dictionary<string, XmlDocEntry> _exact;
    private readonly Dictionary<string, XmlDocEntry> _fallbackByPrefix;

    public XmlDocBook(
        Dictionary<string, XmlDocEntry> exact,
        Dictionary<string, XmlDocEntry> fallbackByPrefix)
    {
        _exact = exact;
        _fallbackByPrefix = fallbackByPrefix;
    }

    public int Count => _exact.Count;

    public XmlDocEntry? Find(string docId, string? fallbackKey = null)
    {
        if (_exact.TryGetValue(docId, out var entry))
            return entry;
        if (fallbackKey != null && _fallbackByPrefix.TryGetValue(fallbackKey, out var fb))
            return fb;
        return null;
    }
}

public static partial class XmlDocReader
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    public static string? FindXmlDocPath(string assemblyPath)
    {
        var xml = Path.ChangeExtension(assemblyPath, ".xml");
        if (File.Exists(xml)) return xml;

        var dir = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var fileName = Path.GetFileName(xml);

        foreach (var sub in new[] { "en", "en-US", "zh-Hans", "zh-CN" })
        {
            var candidate = Path.Combine(dir, sub, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static XmlDocBook Load(string? xmlPath)
    {
        if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
            return XmlDocBook.Empty;

        var exact = new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal);
        var fallback = new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal);

        try
        {
            var doc = XDocument.Load(xmlPath, LoadOptions.None);
            var members = doc.Root?.Element("members")?.Elements("member");
            if (members == null) return XmlDocBook.Empty;

            foreach (var m in members)
            {
                var name = m.Attribute("name")?.Value?.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                var summary = CleanText(m.Element("summary"));
                var remarks = CleanText(m.Element("remarks"));
                var returns = CleanText(m.Element("returns"));

                var paramDict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in m.Elements("param"))
                {
                    var pName = p.Attribute("name")?.Value?.Trim();
                    var pText = CleanText(p);
                    if (!string.IsNullOrEmpty(pName) && !string.IsNullOrEmpty(pText))
                        paramDict[pName] = pText;
                }

                if (summary == null && remarks == null && returns == null && paramDict.Count == 0)
                    continue;

                var entry = new XmlDocEntry(summary, remarks, returns, paramDict);
                exact[name] = entry;

                // Fallback key for methods with C++/CLI signature quirks: M:FullType.MethodName#paramCount
                if (name.StartsWith("M:", StringComparison.Ordinal))
                {
                    var fbKey = BuildMethodFallbackKey(name);
                    if (fbKey != null && !fallback.ContainsKey(fbKey))
                        fallback[fbKey] = entry;
                }
            }
        }
        catch
        {
            return XmlDocBook.Empty;
        }

        return new XmlDocBook(exact, fallback);
    }

    public static string? BuildMethodFallbackKey(string docId)
    {
        var parenIdx = docId.IndexOf('(');
        if (parenIdx < 0) return docId + "#0";

        var prefix = docId[..parenIdx];
        var closeIdx = docId.LastIndexOf(')');
        if (closeIdx <= parenIdx) return prefix + "#0";

        var inner = docId[(parenIdx + 1)..closeIdx];
        if (string.IsNullOrWhiteSpace(inner)) return prefix + "#0";

        var count = 1;
        var depth = 0;
        foreach (var ch in inner)
        {
            if (ch == '{' || ch == '[') depth++;
            else if (ch == '}' || ch == ']') depth--;
            else if (ch == ',' && depth == 0) count++;
        }
        return $"{prefix}#{count}";
    }

    private static string? CleanText(XElement? element)
    {
        if (element == null) return null;
        var sb = new StringBuilder();
        AppendNodeText(element, sb);
        var text = MultiSpaceRegex().Replace(sb.ToString(), " ").Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static void AppendNodeText(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see":
                        case "seealso":
                        {
                            var cref = child.Attribute("cref")?.Value
                                    ?? child.Attribute("langword")?.Value
                                    ?? child.Attribute("href")?.Value;
                            var inner = child.Value?.Trim();
                            if (!string.IsNullOrEmpty(inner))
                                sb.Append('`').Append(inner).Append('`');
                            else if (!string.IsNullOrEmpty(cref))
                                sb.Append('`').Append(FormatCref(cref)).Append('`');
                            break;
                        }
                        case "paramref":
                        case "typeparamref":
                        {
                            var p = child.Attribute("name")?.Value;
                            if (!string.IsNullOrEmpty(p))
                                sb.Append('`').Append(p).Append('`');
                            break;
                        }
                        case "c":
                        case "code":
                            sb.Append('`').Append(child.Value.Trim()).Append('`');
                            break;
                        case "para":
                            sb.Append(' ');
                            AppendNodeText(child, sb);
                            sb.Append(' ');
                            break;
                        default:
                            AppendNodeText(child, sb);
                            break;
                    }
                    break;
            }
        }
    }

    private static string FormatCref(string cref)
    {
        if (cref.Length > 2 && cref[1] == ':')
            cref = cref[2..];
        var paren = cref.IndexOf('(');
        var core = paren >= 0 ? cref[..paren] : cref;
        var lastDot = core.LastIndexOf('.');
        return lastDot >= 0 ? core[(lastDot + 1)..] : core;
    }
}
