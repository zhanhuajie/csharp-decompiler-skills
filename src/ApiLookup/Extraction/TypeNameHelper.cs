using System.Reflection.Metadata;
using System.Text;

namespace ApiLookup;

internal static class TypeNameHelper
{
    public static (string ns, string clrName, string displayFull, string bareFull, string displayName, string simpleName)
        BuildTypeNames(MetadataReader md, TypeDefinition td)
    {
        var chain = new List<TypeDefinition>();
        var current = td;
        while (true)
        {
            chain.Add(current);
            if (!current.IsNested) break;
            current = md.GetTypeDefinition(current.GetDeclaringType());
        }
        chain.Reverse();

        var ns = md.GetString(chain[0].Namespace);
        var clrParts = new List<string>(chain.Count);
        var dispParts = new List<string>(chain.Count);
        var bareParts = new List<string>(chain.Count);
        var totalOuterParams = 0;

        for (var i = 0; i < chain.Count; i++)
        {
            var cur = chain[i];
            var raw = md.GetString(cur.Name);
            clrParts.Add(raw);

            var bare = DisplaySignatureProvider.StripGenericArity(raw);
            bareParts.Add(bare);

            var allGp = ReadGenericParamNames(md, cur.GetGenericParameters());
            var ownGp = allGp.Count > totalOuterParams
                ? allGp.Skip(totalOuterParams).ToList()
                : new List<string>();
            totalOuterParams = allGp.Count;

            dispParts.Add(ownGp.Count > 0 ? $"{bare}<{string.Join(", ", ownGp)}>" : bare);
        }

        var clrJoined = string.Join("+", clrParts);
        var dispJoined = string.Join(".", dispParts);
        var bareJoined = string.Join(".", bareParts);
        var clrName = string.IsNullOrEmpty(ns) ? clrJoined : $"{ns}.{clrJoined}";
        var displayFull = string.IsNullOrEmpty(ns) ? dispJoined : $"{ns}.{dispJoined}";
        var bareFull = string.IsNullOrEmpty(ns) ? bareJoined : $"{ns}.{bareJoined}";
        var displayName = dispParts[^1];
        var simpleName = bareParts[^1];

        return (ns, clrName, displayFull, bareFull, displayName, simpleName);
    }

    public static (string ns, string clrName, string displayFull, string bareFull, string simpleName)
        BuildExportedTypeNames(MetadataReader md, ExportedType et)
    {
        var chain = new List<ExportedType>();
        var cur = et;
        while (true)
        {
            chain.Add(cur);
            if (cur.Implementation.Kind != HandleKind.ExportedType) break;
            cur = md.GetExportedType((ExportedTypeHandle)cur.Implementation);
        }
        chain.Reverse();

        var ns = md.GetString(chain[0].Namespace);
        var clrParts = new List<string>(chain.Count);
        var dispParts = new List<string>(chain.Count);
        var bareParts = new List<string>(chain.Count);

        foreach (var item in chain)
        {
            var raw = md.GetString(item.Name);
            clrParts.Add(raw);
            var bare = DisplaySignatureProvider.StripGenericArity(raw);
            bareParts.Add(bare);

            var tickIdx = raw.IndexOf('`');
            if (tickIdx >= 0 && int.TryParse(raw[(tickIdx + 1)..], out var arity) && arity > 0)
            {
                var gps = arity == 1
                    ? "T"
                    : string.Join(", ", Enumerable.Range(1, arity).Select(n => $"T{n}"));
                dispParts.Add($"{bare}<{gps}>");
            }
            else
            {
                dispParts.Add(bare);
            }
        }

        var clrJoined = string.Join("+", clrParts);
        var dispJoined = string.Join(".", dispParts);
        var bareJoined = string.Join(".", bareParts);
        var clrName = string.IsNullOrEmpty(ns) ? clrJoined : $"{ns}.{clrJoined}";
        var displayFull = string.IsNullOrEmpty(ns) ? dispJoined : $"{ns}.{dispJoined}";
        var bareFull = string.IsNullOrEmpty(ns) ? bareJoined : $"{ns}.{bareJoined}";
        return (ns, clrName, displayFull, bareFull, bareParts[^1]);
    }

    public static List<string> ReadGenericParamNames(MetadataReader md, GenericParameterHandleCollection handles)
    {
        var list = new List<string>(handles.Count);
        foreach (var h in handles)
            list.Add(md.GetString(md.GetGenericParameter(h).Name));
        return list;
    }

    public static HashSet<string> ReadValueTypeGenericParams(MetadataReader md, GenericParameterHandleCollection handles, IReadOnlySet<string>? inherited = null)
    {
        var set = inherited != null
            ? new HashSet<string>(inherited, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (var h in handles)
        {
            var gp = md.GetGenericParameter(h);
            if ((gp.Attributes & System.Reflection.GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                set.Add(md.GetString(gp.Name));
        }
        return set;
    }

    public static string ResolveEntityFullName(MetadataReader md, EntityHandle handle, GenericContext ctx)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullNameDisplaySignatureProvider.Instance.GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => FullNameDisplaySignatureProvider.Instance.GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => FullNameDisplaySignatureProvider.Instance.GetTypeFromSpecification(md, ctx, (TypeSpecificationHandle)handle, 0),
            _ => string.Empty,
        };
    }

    public static string ResolveEntityDisplayName(MetadataReader md, EntityHandle handle, GenericContext ctx)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => DisplaySignatureProvider.Instance.GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => DisplaySignatureProvider.Instance.GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => DisplaySignatureProvider.Instance.GetTypeFromSpecification(md, ctx, (TypeSpecificationHandle)handle, 0),
            _ => string.Empty,
        };
    }

    public static string StripGenerics(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || !fullName.Contains('<'))
            return fullName;

        var sb = new StringBuilder(fullName.Length);
        var depth = 0;
        foreach (var ch in fullName)
        {
            if (ch == '<')
                depth++;
            else if (ch == '>')
            {
                if (depth > 0) depth--;
            }
            else if (depth == 0)
                sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string StripTypeDecorations(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return string.Empty;
        var s = typeName.Trim().TrimEnd('?', '&', '*');

        while (s.EndsWith(']'))
        {
            var open = s.LastIndexOf('[');
            if (open < 0) break;
            s = s[..open].TrimEnd('?', '&', '*');
        }

        s = StripGenerics(s).TrimEnd('?', '&', '*');
        var brace = s.IndexOf('{');
        if (brace >= 0) s = s[..brace];
        return DisplaySignatureProvider.StripGenericArity(s);
    }

    public static string ExtractSimpleTypeName(string typeName)
    {
        var s = StripTypeDecorations(typeName);
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var lastSep = s.LastIndexOfAny(new[] { '.', '+' });
        var simple = lastSep >= 0 ? s[(lastSep + 1)..] : s;
        return DisplaySignatureProvider.NormalizePrimitiveAlias(simple);
    }

    public static List<ExtensionTargetRef> ResolveExtensionTargets(
        MetadataReader md,
        MethodDefinition method,
        string rawDocTargetType,
        GenericContext fullCtx)
    {
        var targets = new List<ExtensionTargetRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Trim only ByRef '@' (e.g. `ref this MyStruct` / `in this MyStruct`), preserving `[]` for array extensions!
        var trimmed = rawDocTargetType.TrimEnd('@');
        if (string.IsNullOrEmpty(trimmed) || trimmed.EndsWith('*'))
            return targets;

        // Check for generic method parameter (`0 / ``0) with type constraints: e.g. `this T entity where T : BaseEntity, IDrawable`
        if (trimmed.StartsWith("``", StringComparison.Ordinal) &&
            int.TryParse(trimmed[2..], out var gpIndex))
        {
            var gpHandles = method.GetGenericParameters();
            if (gpIndex >= 0 && gpIndex < gpHandles.Count)
            {
                var gp = md.GetGenericParameter(gpHandles.ElementAt(gpIndex));
                foreach (var ch in gp.GetConstraints())
                {
                    var constraint = md.GetGenericParameterConstraint(ch);
                    var cFull = StripGenerics(ResolveEntityFullName(md, constraint.Type, fullCtx));
                    if (string.IsNullOrEmpty(cFull) || cFull is "System.ValueType" or "ValueType" or "System.Object" or "object")
                        continue;
                    var cSimple = ExtractSimpleTypeName(cFull);
                    if (!string.IsNullOrEmpty(cSimple) && seen.Add(cFull))
                        targets.Add(new ExtensionTargetRef(cFull, cSimple));
                }
            }
            return targets;
        }

        if (trimmed.StartsWith('`'))
            return targets;

        // Preserve array rank suffix so `this Wall[]` does NOT match single `Wall`!
        var arraySuffix = string.Empty;
        var core = trimmed;
        if (core.EndsWith(']'))
        {
            var open = core.IndexOf('[');
            if (open > 0)
            {
                var rankCommas = core[open..].Count(c => c == ',');
                arraySuffix = rankCommas == 0 ? "[]" : $"[{new string(',', rankCommas)}]";
                core = core[..open];
            }
        }

        var brace = core.IndexOf('{');
        if (brace >= 0) core = core[..brace];
        core = DisplaySignatureProvider.StripGenericArity(core);
        if (string.IsNullOrEmpty(core))
            return targets;

        var leafSimple = ExtractSimpleTypeName(core) + arraySuffix;
        var fullTarget = core + arraySuffix;
        if (seen.Add(fullTarget))
            targets.Add(new ExtensionTargetRef(fullTarget, leafSimple));

        return targets;
    }

    public static bool IsGenericParamName(string name, GenericContext? ctx)
    {
        if (ctx == null) return false;
        for (var i = 0; i < ctx.TypeParams.Count; i++)
            if (string.Equals(ctx.TypeParams[i], name, StringComparison.Ordinal)) return true;
        for (var i = 0; i < ctx.MethodParams.Count; i++)
            if (string.Equals(ctx.MethodParams[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    public static List<string> ExtractReferencedSimpleTypeNames(string displayType, GenericContext? ctx = null)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(ReadOnlySpan<char> span)
        {
            var trimmed = span.Trim();
            if (trimmed.IsEmpty) return;

            var str = trimmed.ToString();
            var outerSimple = ExtractSimpleTypeName(str);
            if (!string.IsNullOrEmpty(outerSimple) &&
                !string.Equals(outerSimple, "void", StringComparison.OrdinalIgnoreCase) &&
                !IsGenericParamName(outerSimple, ctx) &&
                seen.Add(outerSimple))
            {
                results.Add(outerSimple);
            }

            var depth = 0;
            var ltStart = -1;
            for (var i = 0; i < str.Length; i++)
            {
                var ch = str[i];
                if (ch == '<')
                {
                    if (depth == 0) ltStart = i;
                    depth++;
                }
                else if (ch == '>')
                {
                    depth--;
                    if (depth == 0 && ltStart >= 0)
                    {
                        var inner = str.AsSpan(ltStart + 1, i - ltStart - 1);
                        SplitTopLevelArgs(inner, Collect);
                        ltStart = -1;
                    }
                }
            }
        }

        Collect(displayType.AsSpan());
        return results;
    }

    private delegate void SpanAction(ReadOnlySpan<char> span);

    private static void SplitTopLevelArgs(ReadOnlySpan<char> inner, SpanAction onArg)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') { if (depth > 0) depth--; }
            else if (ch == ',' && depth == 0)
            {
                onArg(inner[start..i]);
                start = i + 1;
            }
        }
        if (start < inner.Length)
            onArg(inner[start..]);
    }
}
