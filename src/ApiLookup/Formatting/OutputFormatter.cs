using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ApiLookup;

public static class OutputFormatter
{
    public static void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo, TextWriter? output = null)
        => (output ?? Console.Out).WriteLine(JsonSerializer.Serialize(value, typeInfo));

    public static string FormatTypeHeader(TypeRow t)
    {
        var parts = new List<string> { t.Accessibility };
        if (!string.IsNullOrEmpty(t.Modifiers)) parts.Add(t.Modifiers);
        parts.Add(t.Kind);
        parts.Add(t.Name);

        var bases = new List<string>();
        if (!string.IsNullOrEmpty(t.BaseSimple) &&
            t.BaseType is not ("System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate" or "System.Delegate"))
            bases.Add(t.BaseSimple);
        if (!string.IsNullOrEmpty(t.Interfaces))
            bases.Add(t.Interfaces);

        var header = bases.Count > 0
            ? $"{string.Join(" ", parts)} : {string.Join(", ", bases)}"
            : string.Join(" ", parts);

        if (!string.IsNullOrEmpty(t.GenericConstraints))
            header = $"{header} {t.GenericConstraints}";

        return header;
    }

    public static void WriteIndexStats(IndexStats stats, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(stats, ApiLookupJsonContext.Default.IndexStats, output); return; }

        var w = output ?? Console.Out;
        var action = stats.Rebuilt ? "Built" : "Up-to-date";
        var sizeMb = stats.DbSizeBytes / (1024.0 * 1024.0);
        w.WriteLine($"[{action}] {(stats.IndexName != null ? $"index '{stats.IndexName}'" : "auto-cache")}");
        w.WriteLine($"  DB:         {stats.DbPath} ({sizeMb:F2} MB)");
        w.WriteLine($"  Assemblies: {stats.AssemblyCount} (with XML docs: {stats.XmlDocCount})");
        w.WriteLine($"  Types:      {stats.TypeCount:N0}");
        w.WriteLine($"  Members:    {stats.MemberCount:N0}");
        w.WriteLine($"  Elapsed:    {stats.ElapsedMs} ms");
    }

    public static void WriteIndexList(IReadOnlyList<IndexInfo> indexes, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(indexes, ApiLookupJsonContext.Default.IReadOnlyListIndexInfo, output); return; }

        var w = output ?? Console.Out;
        if (indexes.Count == 0)
        {
            w.WriteLine("No cached indexes found.");
            w.WriteLine($"Cache directory: {IndexCatalog.DefaultCacheDirectory()}");
            return;
        }

        w.WriteLine($"Cached indexes ({indexes.Count}):\n");
        foreach (var idx in indexes)
        {
            var sizeMb = idx.DbSizeBytes / (1024.0 * 1024.0);
            w.WriteLine($"  {idx.Name}");
            w.WriteLine($"    Assemblies: {idx.AssemblyCount} | Types: {idx.TypeCount:N0} | Members: {idx.MemberCount:N0} | Size: {sizeMb:F2} MB");
            if (!string.IsNullOrEmpty(idx.Sources))
                w.WriteLine($"    Sources:    {idx.Sources}");
            w.WriteLine($"    Built:      {idx.BuiltAtUtc} | Path: {idx.DbPath}");
        }
    }

    public static void WriteDropIndex(DropIndexResult result, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(result, ApiLookupJsonContext.Default.DropIndexResult, output); return; }
        var w = output ?? Console.Out;
        w.WriteLine(result.Dropped
            ? $"Dropped index '{result.Name}'."
            : $"Index '{result.Name}' not found.");
    }

    public static void WritePruneResult(PruneResult result, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(result, ApiLookupJsonContext.Default.PruneResult, output); return; }
        var w = output ?? Console.Out;
        var freedMb = result.FreedBytes / (1024.0 * 1024.0);
        w.WriteLine($"Pruned {result.RemovedCount} auto-cache index(es), freed {freedMb:F2} MB.");
    }

    public static void WriteSearchHits(string query, IReadOnlyList<SearchHit> hits, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(hits, ApiLookupJsonContext.Default.IReadOnlyListSearchHit, output); return; }
        WriteSearchHitsText(query, hits, output ?? Console.Out);
    }

    public static void WriteSearchHitsWithRefs(
        string query,
        IReadOnlyList<AssemblyRow> assemblies,
        IReadOnlyList<SearchHit> hits,
        bool json,
        TextWriter? output = null)
    {
        if (json)
        {
            WriteJson(new SearchJsonResult(assemblies, hits), ApiLookupJsonContext.Default.SearchJsonResult, output);
            return;
        }

        var w = output ?? Console.Out;
        WriteAssemblies(assemblies, json: false, w);
        w.WriteLine();
        WriteSearchHitsText(query, hits, w);
    }

    private static void WriteSearchHitsText(string query, IReadOnlyList<SearchHit> hits, TextWriter w)
    {
        if (hits.Count == 0)
        {
            w.WriteLine($"No results for \"{query}\".");
            return;
        }

        w.WriteLine($"Search \"{query}\" ({hits.Count} hits):\n");
        foreach (var h in hits)
        {
            var tag = $"[{h.Kind}]";
            w.WriteLine($"{tag,-12} {h.FullName}  ({h.AssemblyName})");
            w.WriteLine($"             {h.Signature}");
            if (!string.IsNullOrEmpty(h.Summary))
                w.WriteLine($"             /// {Truncate(h.Summary, 160)}");
        }
    }

    public static void WriteAmbiguousTypes(string query, IReadOnlyList<TypeRow> candidates, bool json, TextWriter? output = null)
    {
        if (json)
        {
            WriteJson(new AmbiguousTypesResult(true, query, candidates), ApiLookupJsonContext.Default.AmbiguousTypesResult, output);
            return;
        }

        var w = output ?? Console.Out;
        w.WriteLine($"Multiple types match \"{query}\" ({candidates.Count}). Re-run with full name, --namespace, or --assembly:\n");
        foreach (var t in candidates)
        {
            w.WriteLine($"  [{t.Kind,-9}] {t.FullName}  ({t.AssemblyName})");
            if (!string.IsNullOrEmpty(t.Summary))
                w.WriteLine($"              /// {Truncate(t.Summary, 140)}");
            w.WriteLine($"              Assembly: {t.AssemblyPath}");
        }
    }

    public static void WriteTypeDetail(
        TypeRow type,
        IReadOnlyList<MemberRow> ownMembers,
        IReadOnlyList<(TypeRow BaseType, IReadOnlyList<MemberRow> Members)> inheritedGroups,
        IReadOnlyList<MemberRow> extensionMethods,
        bool showFullDocs,
        bool json,
        bool memberFiltered = false,
        TextWriter? output = null)
    {
        if (json)
        {
            var dto = new TypeDetailJson(
                type,
                ownMembers,
                inheritedGroups.Select(g => new InheritedGroupJson(g.BaseType.FullName, g.Members)).ToList(),
                extensionMethods);
            WriteJson(dto, ApiLookupJsonContext.Default.TypeDetailJson, output);
            return;
        }

        var w = output ?? Console.Out;
        var sb = new StringBuilder();
        sb.AppendLine($"// Assembly: {type.AssemblyName} ({type.AssemblyPath})");
        if (!string.IsNullOrEmpty(type.ClrName) && type.ClrName != type.FullName)
            sb.AppendLine($"// CLR Name: {type.ClrName}");
        if (!string.IsNullOrEmpty(type.Namespace))
            sb.AppendLine($"// Namespace: {type.Namespace}");

        if (!string.IsNullOrEmpty(type.Summary))
            sb.AppendLine($"/// {type.Summary}");
        if (showFullDocs && !string.IsNullOrEmpty(type.Remarks))
            sb.AppendLine($"/// Remarks: {type.Remarks}");

        if (type.Kind == "delegate" && !string.IsNullOrEmpty(type.DelegateSignature) && !memberFiltered)
        {
            sb.AppendLine($"{type.DelegateSignature};");
            if (extensionMethods.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("// ---- Extension Methods ----");
                foreach (var ext in extensionMethods)
                {
                    if (!string.IsNullOrEmpty(ext.Summary))
                        sb.AppendLine($"/// {Truncate(ext.Summary, 160)}");
                    sb.AppendLine($"{ext.Signature};  // from {ext.DeclaringFullName}");
                    if (showFullDocs) WriteParamReturnDocs(sb, ext, string.Empty);
                }
            }
            w.Write(sb.ToString());
            return;
        }

        sb.AppendLine(FormatTypeHeader(type));
        sb.AppendLine("{");

        WriteMemberList(sb, ownMembers, showFullDocs, indent: "    ");

        foreach (var (baseType, baseMembers) in inheritedGroups)
        {
            if (baseMembers.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine($"    // ---- Inherited from {baseType.FullName} ----");
            WriteMemberList(sb, baseMembers, showFullDocs, indent: "    ");
        }

        if (extensionMethods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    // ---- Extension Methods ----");
            foreach (var ext in extensionMethods)
            {
                if (!string.IsNullOrEmpty(ext.Summary))
                    sb.AppendLine($"    /// {Truncate(ext.Summary, 160)}");
                sb.AppendLine($"    {ext.Signature};  // from {ext.DeclaringFullName}");
                if (showFullDocs) WriteParamReturnDocs(sb, ext, "    ");
            }
        }

        sb.AppendLine("}");
        w.Write(sb.ToString());
    }

    private static void WriteMemberList(StringBuilder sb, IReadOnlyList<MemberRow> members, bool showFullDocs, string indent)
    {
        string? lastSection = null;
        foreach (var m in members)
        {
            var section = m.Kind switch
            {
                "enum" => "Values",
                "ctor" => "Constructors",
                "property" => "Properties",
                "method" => "Methods",
                "event" => "Events",
                "field" => "Fields",
                _ => "Members",
            };
            if (section != lastSection)
            {
                if (lastSection != null) sb.AppendLine();
                sb.AppendLine($"{indent}// {section}");
                lastSection = section;
            }

            if (!string.IsNullOrEmpty(m.Summary))
                sb.AppendLine($"{indent}/// {Truncate(m.Summary, showFullDocs ? 400 : 160)}");

            var suffix = m.Kind switch
            {
                "enum" => ",",
                "property" => "",
                _ => ";",
            };
            sb.AppendLine($"{indent}{m.Signature}{suffix}");

            if (showFullDocs)
                WriteParamReturnDocs(sb, m, indent);
        }
    }

    private static void WriteParamReturnDocs(StringBuilder sb, MemberRow m, string indent)
    {
        if (!string.IsNullOrEmpty(m.ParamsDoc))
        {
            foreach (var line in m.ParamsDoc.Split('\n'))
                sb.AppendLine($"{indent}///   @param {line}");
        }
        if (!string.IsNullOrEmpty(m.ReturnsDoc))
            sb.AppendLine($"{indent}///   @returns {m.ReturnsDoc}");
    }

    public static void WriteHierarchy(HierarchyResult result, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(result, ApiLookupJsonContext.Default.HierarchyResult, output); return; }

        var w = output ?? Console.Out;
        w.WriteLine($"Type: {result.Target.FullName}  ({result.Target.AssemblyName})");
        if (!string.IsNullOrEmpty(result.Target.Interfaces))
            w.WriteLine($"Interfaces: {result.Target.Interfaces}");

        if (result.AncestorChain.Count > 0)
        {
            w.WriteLine("\nBase Chain:");
            w.WriteLine($"  {result.Target.FullName}");
            for (var i = 0; i < result.AncestorChain.Count; i++)
                w.WriteLine($"{new string(' ', (i + 1) * 2)}-> {result.AncestorChain[i]}");
        }

        w.WriteLine($"\nDerived / Implementing Types ({result.DerivedTypes.Count}):");
        if (result.DerivedTypes.Count == 0)
        {
            w.WriteLine("  (none in indexed assemblies)");
            return;
        }
        foreach (var d in result.DerivedTypes)
        {
            var via = !string.IsNullOrEmpty(d.BaseSimple) ? $" : {d.BaseSimple}" : "";
            w.WriteLine($"  [{d.Kind,-9}] {d.FullName}{via}  ({d.AssemblyName})");
        }
    }

    public static void WriteByType(string typeName, string role, IReadOnlyList<MemberRow> members, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(members, ApiLookupJsonContext.Default.IReadOnlyListMemberRow, output); return; }

        var w = output ?? Console.Out;
        if (members.Count == 0)
        {
            w.WriteLine($"No members found referencing type \"{typeName}\" (role={role}).");
            return;
        }

        w.WriteLine($"Members referencing \"{typeName}\" (role={role}, {members.Count} hits):\n");
        foreach (var m in members)
        {
            w.WriteLine($"[{m.Kind,-8}] {m.DeclaringFullName}  ({m.AssemblyName})");
            w.WriteLine($"           {m.Signature}");
            if (!string.IsNullOrEmpty(m.Summary))
                w.WriteLine($"           /// {Truncate(m.Summary, 150)}");
        }
    }

    public static void WriteAssemblies(IReadOnlyList<AssemblyRow> assemblies, bool json, TextWriter? output = null)
    {
        if (json) { WriteJson(assemblies, ApiLookupJsonContext.Default.IReadOnlyListAssemblyRow, output); return; }

        var w = output ?? Console.Out;
        w.WriteLine($"Indexed assemblies ({assemblies.Count}):\n");
        foreach (var a in assemblies)
        {
            var xmlTag = a.XmlPath != null ? " [XML]" : "";
            w.WriteLine($"  {a.Name} v{a.Version} ({a.TypeCount} types){xmlTag}");
            w.WriteLine($"    {a.Path}");
        }
    }

    private static string Truncate(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";
}
