using System.Text;
using System.Text.Json;

namespace ApiLookup;

public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void WriteJson(object value)
        => Console.WriteLine(JsonSerializer.Serialize(value, JsonOpts));

    public static void WriteIndexStats(IndexStats stats, bool json)
    {
        if (json) { WriteJson(stats); return; }

        var action = stats.Rebuilt ? "Built" : "Up-to-date";
        var sizeMb = stats.DbSizeBytes / (1024.0 * 1024.0);
        Console.WriteLine($"[{action}] {(stats.IndexName != null ? $"index '{stats.IndexName}'" : "auto-cache")}");
        Console.WriteLine($"  DB:         {stats.DbPath} ({sizeMb:F2} MB)");
        Console.WriteLine($"  Assemblies: {stats.AssemblyCount} (with XML docs: {stats.XmlDocCount})");
        Console.WriteLine($"  Types:      {stats.TypeCount:N0}");
        Console.WriteLine($"  Members:    {stats.MemberCount:N0}");
        Console.WriteLine($"  Elapsed:    {stats.ElapsedMs} ms");
    }

    public static void WriteIndexList(IReadOnlyList<IndexInfo> indexes, bool json)
    {
        if (json) { WriteJson(indexes); return; }

        if (indexes.Count == 0)
        {
            Console.WriteLine("No cached indexes found.");
            Console.WriteLine($"Cache directory: {IndexStore.DefaultCacheDirectory()}");
            return;
        }

        Console.WriteLine($"Cached indexes ({indexes.Count}):\n");
        foreach (var idx in indexes)
        {
            var sizeMb = idx.DbSizeBytes / (1024.0 * 1024.0);
            Console.WriteLine($"  {idx.Name}");
            Console.WriteLine($"    Assemblies: {idx.AssemblyCount} | Types: {idx.TypeCount:N0} | Members: {idx.MemberCount:N0} | Size: {sizeMb:F2} MB");
            if (!string.IsNullOrEmpty(idx.Sources))
                Console.WriteLine($"    Sources:    {idx.Sources}");
            Console.WriteLine($"    Built:      {idx.BuiltAtUtc} | Path: {idx.DbPath}");
        }
    }

    public static void WriteSearchHits(string query, IReadOnlyList<SearchHit> hits, bool json)
    {
        if (json) { WriteJson(hits); return; }

        if (hits.Count == 0)
        {
            Console.WriteLine($"No results for \"{query}\".");
            return;
        }

        Console.WriteLine($"Search \"{query}\" ({hits.Count} hits):\n");
        foreach (var h in hits)
        {
            var tag = h.EntityType == "type" ? $"[{h.Kind}]" : $"[{h.Kind}]";
            if (h.EntityType == "type")
            {
                Console.WriteLine($"{tag,-12} {h.FullName}  ({h.AssemblyName})");
                Console.WriteLine($"             {h.Signature}");
            }
            else
            {
                Console.WriteLine($"{tag,-12} {h.FullName}  ({h.AssemblyName})");
                Console.WriteLine($"             {h.Signature}");
            }
            if (!string.IsNullOrEmpty(h.Summary))
                Console.WriteLine($"             /// {Truncate(h.Summary, 160)}");
        }
    }

    public static void WriteAmbiguousTypes(string query, IReadOnlyList<TypeRow> candidates, bool json)
    {
        if (json) { WriteJson(candidates); return; }

        Console.WriteLine($"Multiple types match \"{query}\" ({candidates.Count}). Re-run with full name, --namespace, or --assembly:\n");
        foreach (var t in candidates)
        {
            Console.WriteLine($"  [{t.Kind,-9}] {t.FullName}  ({t.AssemblyName})");
            if (!string.IsNullOrEmpty(t.Summary))
                Console.WriteLine($"              /// {Truncate(t.Summary, 140)}");
            Console.WriteLine($"              Assembly: {t.AssemblyPath}");
        }
    }

    public static void WriteTypeDetail(
        TypeRow type,
        IReadOnlyList<MemberRow> ownMembers,
        IReadOnlyList<(TypeRow BaseType, IReadOnlyList<MemberRow> Members)> inheritedGroups,
        IReadOnlyList<MemberRow> extensionMethods,
        bool showFullDocs,
        bool json)
    {
        if (json)
        {
            WriteJson(new
            {
                type,
                members = ownMembers,
                inherited = inheritedGroups.Select(g => new { baseType = g.BaseType.FullName, members = g.Members }),
                extensionMethods,
            });
            return;
        }

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

        sb.AppendLine(IndexStore.FormatTypeHeader(type));
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
        Console.Write(sb.ToString());
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

    public static void WriteHierarchy(HierarchyResult result, bool json)
    {
        if (json) { WriteJson(result); return; }

        Console.WriteLine($"Type: {result.Target.FullName}  ({result.Target.AssemblyName})");
        if (!string.IsNullOrEmpty(result.Target.Interfaces))
            Console.WriteLine($"Interfaces: {result.Target.Interfaces}");

        if (result.AncestorChain.Count > 0)
        {
            Console.WriteLine("\nBase Chain:");
            Console.WriteLine($"  {result.Target.FullName}");
            for (var i = 0; i < result.AncestorChain.Count; i++)
                Console.WriteLine($"{new string(' ', (i + 1) * 2)}-> {result.AncestorChain[i]}");
        }

        Console.WriteLine($"\nDerived / Implementing Types ({result.DerivedTypes.Count}):");
        if (result.DerivedTypes.Count == 0)
        {
            Console.WriteLine("  (none in indexed assemblies)");
            return;
        }
        foreach (var d in result.DerivedTypes)
        {
            var via = !string.IsNullOrEmpty(d.BaseSimple) ? $" : {d.BaseSimple}" : "";
            Console.WriteLine($"  [{d.Kind,-9}] {d.FullName}{via}  ({d.AssemblyName})");
        }
    }

    public static void WriteByType(string typeName, string role, IReadOnlyList<MemberRow> members, bool json)
    {
        if (json) { WriteJson(members); return; }

        if (members.Count == 0)
        {
            Console.WriteLine($"No members found referencing type \"{typeName}\" (role={role}).");
            return;
        }

        Console.WriteLine($"Members referencing \"{typeName}\" (role={role}, {members.Count} hits):\n");
        foreach (var m in members)
        {
            Console.WriteLine($"[{m.Kind,-8}] {m.DeclaringFullName}  ({m.AssemblyName})");
            Console.WriteLine($"           {m.Signature}");
            if (!string.IsNullOrEmpty(m.Summary))
                Console.WriteLine($"           /// {Truncate(m.Summary, 150)}");
        }
    }

    public static void WriteAssemblies(IReadOnlyList<AssemblyRow> assemblies, bool json)
    {
        if (json) { WriteJson(assemblies); return; }

        Console.WriteLine($"Indexed assemblies ({assemblies.Count}):\n");
        foreach (var a in assemblies)
        {
            var xmlTag = a.XmlPath != null ? " [XML]" : "";
            Console.WriteLine($"  {a.Name} v{a.Version} ({a.TypeCount} types){xmlTag}");
            Console.WriteLine($"    {a.Path}");
        }
    }

    private static string Truncate(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";
}
