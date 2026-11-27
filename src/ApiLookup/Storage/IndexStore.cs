using Microsoft.Data.Sqlite;

namespace ApiLookup;

public sealed class IndexStore : IDisposable
{
    public const string SchemaVersion = IndexBuilder.SchemaVersion;

    private static readonly HashSet<string> NoisyExtensionInterfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.IDisposable",
        "System.IComparable",
        "System.IEquatable",
        "System.ICloneable",
        "IDisposable",
        "IComparable",
        "IEquatable",
        "ICloneable",
    };

    private const string TypeSelectColumns = @"
SELECT t.id, a.name, a.path, a.xml_path, t.full_name, t.bare_full_name, t.clr_name, t.namespace,
       t.name, t.simple_name, t.kind, t.accessibility, t.modifiers, t.base_type, t.base_simple,
       t.interfaces, t.generic_params, t.generic_constraints, t.delegate_signature, t.summary, t.remarks
FROM types t
JOIN assemblies a ON a.id = t.assembly_id";

    private const string MemberSelectColumns = @"
SELECT m.id, m.type_id, t.full_name, t.simple_name, a.name, a.path, m.kind, m.name, m.accessibility,
       m.modifiers, m.return_type, m.signature, m.ext_target_type, m.enum_value, m.summary, m.params_doc, m.returns_doc
FROM members m
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id";

    private readonly SqliteConnection _conn;
    public string DbPath { get; }

    private IndexStore(string dbPath, SqliteConnection conn)
    {
        DbPath = dbPath;
        _conn = conn;
    }

    public static string DefaultCacheDirectory() => IndexCatalog.DefaultCacheDirectory();
    public static string GetNamedIndexPath(string name) => IndexCatalog.GetNamedIndexPath(name);
    public static string GetAutoCachePath(string sourceKey) => IndexCatalog.GetAutoCachePath(sourceKey);
    public static string ResolveDbPath(string? indexName, string? explicitDbPath, string? sourceKey)
        => IndexCatalog.ResolveDbPath(indexName, explicitDbPath, sourceKey);
    public static IReadOnlyList<IndexInfo> ListIndexes() => IndexCatalog.ListIndexes();
    public static bool DropNamedIndex(string name) => IndexCatalog.DropNamedIndex(name);
    public static PruneResult PruneAutoIndexes() => IndexCatalog.PruneAutoIndexes();

    public static (IndexStore Store, IndexStats Stats) EnsureBuilt(
        string dbPath,
        string? indexName,
        ResolvedSources sources,
        string sourceDescription,
        bool includeNonPublic,
        bool forceRebuild,
        List<string>? warnings = null)
        => IndexBuilder.EnsureBuilt(dbPath, indexName, sources, sourceDescription, includeNonPublic, forceRebuild, warnings);

    public static IndexStore OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"Index database not found: {dbPath}", dbPath);
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return new IndexStore(dbPath, conn);
    }

    public IReadOnlyList<SearchHit> Search(
        string query,
        string? kindFilter = null,
        string? namespacePrefix = null,
        string? assemblyFilter = null,
        int limit = 30)
    {
        var ftsQuery = Tokenizer.BuildFtsQuery(query);
        if (ftsQuery == null) return Array.Empty<SearchHit>();

        // fts_entries has 5 UNINDEXED columns first (entity_type, entity_id, kind, assembly_name, namespace),
        // followed by 4 searchable columns (full_name, name, tokens, summary).
        var sql = @"
SELECT
    f.entity_type,
    f.entity_id,
    f.kind,
    f.assembly_name,
    f.namespace,
    f.full_name,
    f.name,
    f.summary,
    bm25(fts_entries, 0.0, 0.0, 0.0, 0.0, 0.0, 2.0, 6.0, 1.5, 0.8) AS rank_score,
    CASE
        WHEN LOWER(f.name) = LOWER($exact) AND f.entity_type = 'type' THEN 0
        WHEN LOWER(f.full_name) = LOWER($exact) THEN 1
        WHEN LOWER(f.name) = LOWER($exact) THEN 2
        WHEN LOWER(f.name) LIKE LOWER($prefix) ESCAPE '\' AND f.entity_type = 'type' THEN 3
        WHEN LOWER(f.name) LIKE LOWER($prefix) ESCAPE '\' THEN 4
        WHEN f.entity_type = 'type' THEN 5
        ELSE 6
    END AS priority
FROM fts_entries f
WHERE fts_entries MATCH $fts";

        if (!string.IsNullOrWhiteSpace(kindFilter))
        {
            if (kindFilter.Equals("type", StringComparison.OrdinalIgnoreCase))
                sql += " AND f.entity_type = 'type'";
            else if (kindFilter.Equals("member", StringComparison.OrdinalIgnoreCase))
                sql += " AND f.entity_type = 'member'";
            else
                sql += " AND LOWER(f.kind) = LOWER($kind)";
        }

        using var cmd = _conn.CreateCommand();
        var trimmedQuery = query.Trim();
        cmd.Parameters.AddWithValue("$fts", ftsQuery);
        cmd.Parameters.AddWithValue("$exact", trimmedQuery);
        cmd.Parameters.AddWithValue("$prefix", EscapeLike(trimmedQuery) + "%");
        cmd.Parameters.AddWithValue("$limit", limit);

        if (!string.IsNullOrWhiteSpace(kindFilter))
            cmd.Parameters.AddWithValue("$kind", kindFilter.Trim());

        if (!string.IsNullOrWhiteSpace(namespacePrefix))
        {
            var ns = namespacePrefix.Trim().TrimEnd('.');
            if (ns.Length > 0)
            {
                sql += @" AND (f.namespace = $nsExact COLLATE NOCASE OR LOWER(f.namespace) LIKE LOWER($nsPrefix) ESCAPE '\')";
                cmd.Parameters.AddWithValue("$nsExact", ns);
                cmd.Parameters.AddWithValue("$nsPrefix", EscapeLike(ns) + ".%");
            }
        }

        if (!string.IsNullOrWhiteSpace(assemblyFilter))
        {
            sql += @" AND LOWER(f.assembly_name) LIKE LOWER($asmFilter) ESCAPE '\'";
            cmd.Parameters.AddWithValue("$asmFilter", "%" + EscapeLike(assemblyFilter.Trim()) + "%");
        }

        sql += " ORDER BY priority ASC, rank_score ASC LIMIT $limit";
        cmd.CommandText = sql;

        var rawHits = new List<(string et, long eid, string kind, string aname, string ns, string full, string name, string? sum, double score)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rawHits.Add((
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetDouble(8)));
            }
        }

        if (rawHits.Count == 0) return Array.Empty<SearchHit>();

        // Batch-hydrate types and members in at most 2 queries instead of N+1.
        var typeIds = rawHits.Where(h => h.et == "type").Select(h => h.eid).Distinct().ToList();
        var memberIds = rawHits.Where(h => h.et == "member").Select(h => h.eid).Distinct().ToList();
        var typeMap = BatchLoadTypes(typeIds);
        var memberMap = BatchLoadMembers(memberIds);

        var hits = new List<SearchHit>(rawHits.Count);
        foreach (var h in rawHits)
        {
            if (h.et == "type")
            {
                if (!typeMap.TryGetValue(h.eid, out var tr)) continue;
                var decl = OutputFormatter.FormatTypeHeader(tr);
                hits.Add(new SearchHit("type", h.eid, tr.Kind, tr.AssemblyName, tr.AssemblyPath,
                    tr.Namespace, tr.FullName, tr.Name, decl, tr.Summary, h.score));
            }
            else
            {
                if (!memberMap.TryGetValue(h.eid, out var mr)) continue;
                hits.Add(new SearchHit("member", h.eid, mr.Kind, mr.AssemblyName, mr.AssemblyPath,
                    h.ns, $"{mr.DeclaringFullName}.{mr.Name}", mr.Name, mr.Signature, mr.Summary, h.score));
            }
        }

        return hits;
    }

    public IReadOnlyList<TypeRow> ResolveType(string query, string? namespacePrefix = null, string? assemblyFilter = null)
    {
        var q = query.Trim();
        var qBare = MetadataExtractor.StripGenerics(q);

        // 1. Indexed exact match on full_name, bare_full_name, or clr_name (COLLATE NOCASE)
        var exact = QueryTypes(
            TypeSelectColumns + @"
WHERE (t.full_name = $q OR t.bare_full_name = $qBare OR t.clr_name = $q)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$q", q);
                cmd.Parameters.AddWithValue("$qBare", qBare);
            },
            namespacePrefix,
            assemblyFilter);
        if (exact.Count > 0) return exact;

        // 2. Indexed simple_name / name match when query is unqualified
        if (!q.Contains('.') && !q.Contains('+'))
        {
            var bySimple = QueryTypes(
                TypeSelectColumns + @"
WHERE (t.simple_name = $qBare OR t.name = $q)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$q", q);
                    cmd.Parameters.AddWithValue("$qBare", qBare);
                },
                namespacePrefix,
                assemblyFilter);
            if (bySimple.Count > 0) return bySimple;
        }

        // 3. Suffix match (e.g. "PnP3dObjects.Equipment" or "Outer.Inner" or "Outer+Inner")
        var suffixPattern = "%." + EscapeLike(qBare);
        return QueryTypes(
            TypeSelectColumns + @"
WHERE (t.bare_full_name LIKE $suffix ESCAPE '\' OR t.clr_name LIKE $suffix ESCAPE '\')",
            cmd => cmd.Parameters.AddWithValue("$suffix", suffixPattern),
            namespacePrefix,
            assemblyFilter);
    }

    private List<TypeRow> QueryTypes(
        string baseSql,
        Action<SqliteCommand> bindParams,
        string? nsPrefix,
        string? asmFilter)
    {
        using var cmd = _conn.CreateCommand();
        bindParams(cmd);

        var sql = baseSql;
        AppendTypeFilters(ref sql, cmd, nsPrefix, asmFilter);
        sql += " ORDER BY t.full_name ASC, a.name ASC LIMIT 50";
        cmd.CommandText = sql;

        var list = new List<TypeRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadTypeRow(r));
        return list;
    }

    public IReadOnlyList<MemberRow> GetMembers(long typeId, string? memberFilter = null)
    {
        var sql = MemberSelectColumns + @"
WHERE m.type_id = $tid";

        if (!string.IsNullOrWhiteSpace(memberFilter))
            sql += @" AND m.name LIKE $mf ESCAPE '\'";

        sql += @"
ORDER BY CASE m.kind
    WHEN 'enum' THEN 0
    WHEN 'ctor' THEN 1
    WHEN 'property' THEN 2
    WHEN 'method' THEN 3
    WHEN 'event' THEN 4
    WHEN 'field' THEN 5
    ELSE 6
END, m.id ASC";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$tid", typeId);
        if (!string.IsNullOrWhiteSpace(memberFilter))
            cmd.Parameters.AddWithValue("$mf", "%" + EscapeLike(memberFilter.Trim()) + "%");

        var list = new List<MemberRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMemberRow(r));
        return list;
    }

    public List<TypeRow> GetBaseTypeChain(TypeRow target, int maxDepth = 16)
    {
        var chain = new List<TypeRow>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.BareFullName };
        var currentBase = target.BaseType;

        for (var i = 0; i < maxDepth && !string.IsNullOrEmpty(currentBase); i++)
        {
            var bareBase = MetadataExtractor.StripGenerics(currentBase);
            if (bareBase is "System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate" or "System.Delegate")
                break;

            if (!visited.Add(bareBase)) break;

            var matches = ResolveType(bareBase);
            if (matches.Count == 0) break;

            var baseRow = matches[0];
            chain.Add(baseRow);
            currentBase = baseRow.BaseType;
        }
        return chain;
    }

    public IReadOnlyList<MemberRow> GetExtensionMethods(TypeRow target, IReadOnlyList<TypeRow> baseChain, string? memberFilter = null)
    {
        var targetFulls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.BareFullName };
        var targetSimples = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.SimpleName };

        foreach (var b in baseChain)
        {
            targetFulls.Add(b.BareFullName);
            targetSimples.Add(b.SimpleName);
        }

        // Load exact normalized interfaces from type_interfaces table (handles comma-containing generic interfaces)
        var typeIds = new List<long>(1 + baseChain.Count) { target.Id };
        foreach (var b in baseChain) typeIds.Add(b.Id);

        foreach (var (ifaceFull, ifaceSimple) in LoadInterfacesForTypes(typeIds))
        {
            if (NoisyExtensionInterfaces.Contains(ifaceFull) || NoisyExtensionInterfaces.Contains(ifaceSimple))
                continue;
            targetFulls.Add(ifaceFull);
            if (!string.IsNullOrEmpty(ifaceSimple))
                targetSimples.Add(ifaceSimple);
        }

        if (targetSimples.Count == 0) return Array.Empty<MemberRow>();

        var simpleParams = targetSimples.Select((_, i) => $"$s{i}").ToList();
        var sql = MemberSelectColumns + $@"
WHERE m.ext_target_simple IN ({string.Join(", ", simpleParams)})";

        if (!string.IsNullOrWhiteSpace(memberFilter))
            sql += @" AND m.name LIKE $mf ESCAPE '\'";

        sql += " ORDER BY t.full_name ASC, m.name ASC LIMIT 200";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var idx = 0;
        foreach (var s in targetSimples)
            cmd.Parameters.AddWithValue($"$s{idx++}", s);
        if (!string.IsNullOrWhiteSpace(memberFilter))
            cmd.Parameters.AddWithValue("$mf", "%" + EscapeLike(memberFilter.Trim()) + "%");

        var list = new List<MemberRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = ReadMemberRow(r);
            if (row.ExtTargetType != null && row.ExtTargetType.Contains('.'))
            {
                var extBare = MetadataExtractor.StripGenerics(row.ExtTargetType);
                if (targetFulls.Contains(extBare))
                    list.Add(row);
            }
            else
            {
                list.Add(row);
            }
        }
        return list;
    }

    public HierarchyResult GetHierarchy(TypeRow target, int limit = 100)
    {
        var ancestors = new List<string>();
        var currentBase = target.BaseType;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.BareFullName };

        for (var i = 0; i < 16 && !string.IsNullOrEmpty(currentBase); i++)
        {
            ancestors.Add(currentBase);
            var bare = MetadataExtractor.StripGenerics(currentBase);
            if (!visited.Add(bare)) break;
            var matches = ResolveType(bare);
            if (matches.Count == 0) break;
            currentBase = matches[0].BaseType;
        }

        var derived = new List<TypeRow>();
        var seenDerived = new HashSet<long> { target.Id };
        var queue = new Queue<(string BareFull, string Simple, string Kind)>();
        queue.Enqueue((target.BareFullName, target.SimpleName, target.Kind));

        while (queue.Count > 0 && derived.Count < limit)
        {
            var (curFull, curSimple, curKind) = queue.Dequeue();
            using var cmd = _conn.CreateCommand();

            if (curKind.Equals("interface", StringComparison.OrdinalIgnoreCase))
            {
                var useFull = curFull.Contains('.');
                cmd.CommandText = useFull
                    ? TypeSelectColumns + @"
JOIN type_interfaces ti ON ti.type_id = t.id
WHERE ti.iface_full = $full
ORDER BY t.full_name ASC LIMIT $rem"
                    : TypeSelectColumns + @"
JOIN type_interfaces ti ON ti.type_id = t.id
WHERE ti.iface_simple = $simple
ORDER BY t.full_name ASC LIMIT $rem";
                if (useFull) cmd.Parameters.AddWithValue("$full", curFull);
                else cmd.Parameters.AddWithValue("$simple", curSimple);
            }
            else
            {
                cmd.CommandText = TypeSelectColumns + @"
WHERE t.base_bare = $full
ORDER BY t.full_name ASC LIMIT $rem";
                cmd.Parameters.AddWithValue("$full", curFull);
            }
            cmd.Parameters.AddWithValue("$rem", limit - derived.Count);

            using var r = cmd.ExecuteReader();
            while (r.Read() && derived.Count < limit)
            {
                var row = ReadTypeRow(r);
                if (seenDerived.Add(row.Id))
                {
                    derived.Add(row);
                    queue.Enqueue((row.BareFullName, row.SimpleName, row.Kind));
                }
            }
        }

        return new HierarchyResult(target, ancestors, derived);
    }

    public IReadOnlyList<MemberRow> FindByType(
        string typeName,
        string role = "both",
        string? namespacePrefix = null,
        string? assemblyFilter = null,
        int limit = 40)
    {
        var simple = MetadataExtractor.ExtractSimpleTypeName(typeName);
        if (string.IsNullOrEmpty(simple)) return Array.Empty<MemberRow>();

        var normalizedRole = role.ToLowerInvariant();
        var sql = @"
SELECT m.id, m.type_id, t.full_name, t.simple_name, a.name, a.path, m.kind, m.name, m.accessibility,
       m.modifiers, m.return_type, m.signature, m.ext_target_type, m.enum_value, m.summary, m.params_doc, m.returns_doc
FROM member_type_refs r
JOIN members m ON m.id = r.member_id
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id
WHERE r.type_simple = $simple";

        if (normalizedRole == "return")
            sql += " AND r.role = 'return'";
        else if (normalizedRole == "param")
            sql += " AND r.role = 'param'";

        using var cmd = _conn.CreateCommand();
        cmd.Parameters.AddWithValue("$simple", simple);
        AppendTypeFilters(ref sql, cmd, namespacePrefix, assemblyFilter);

        if (normalizedRole == "both")
        {
            sql += @"
GROUP BY m.id
ORDER BY MIN(CASE WHEN r.role = 'return' THEN 0 ELSE 1 END), t.full_name ASC, m.name ASC
LIMIT $limit";
        }
        else
        {
            sql += @"
ORDER BY t.full_name ASC, m.name ASC
LIMIT $limit";
        }

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<MemberRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMemberRow(r));
        return list;
    }

    public IReadOnlyList<AssemblyRow> ListAssemblies()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name, path, version, xml_path, type_count FROM assemblies ORDER BY name ASC, path ASC";
        var list = new List<AssemblyRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AssemblyRow(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4)));
        }
        return list;
    }

    internal List<string> ExplainQueryPlan(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, val) in parameters)
            cmd.Parameters.AddWithValue(name, val);
        var lines = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            lines.Add(r.GetString(3));
        return lines;
    }

    private Dictionary<long, TypeRow> BatchLoadTypes(List<long> ids)
    {
        var map = new Dictionary<long, TypeRow>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = _conn.CreateCommand();
            var ph = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                ph[i] = $"$id{i}";
                cmd.Parameters.AddWithValue(ph[i], chunk[i]);
            }
            cmd.CommandText = $"{TypeSelectColumns} WHERE t.id IN ({string.Join(", ", ph)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = ReadTypeRow(r);
                map[row.Id] = row;
            }
        }
        return map;
    }

    private Dictionary<long, MemberRow> BatchLoadMembers(List<long> ids)
    {
        var map = new Dictionary<long, MemberRow>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = _conn.CreateCommand();
            var ph = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                ph[i] = $"$id{i}";
                cmd.Parameters.AddWithValue(ph[i], chunk[i]);
            }
            cmd.CommandText = $"{MemberSelectColumns} WHERE m.id IN ({string.Join(", ", ph)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = ReadMemberRow(r);
                map[row.Id] = row;
            }
        }
        return map;
    }

    private List<(string FullName, string SimpleName)> LoadInterfacesForTypes(List<long> typeIds)
    {
        var list = new List<(string, string)>();
        foreach (var chunk in typeIds.Chunk(500))
        {
            using var cmd = _conn.CreateCommand();
            var ph = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                ph[i] = $"$tid{i}";
                cmd.Parameters.AddWithValue(ph[i], chunk[i]);
            }
            cmd.CommandText = $"SELECT DISTINCT iface_full, iface_simple FROM type_interfaces WHERE type_id IN ({string.Join(", ", ph)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1)));
        }
        return list;
    }

    private static void AppendTypeFilters(ref string sql, SqliteCommand cmd, string? nsPrefix, string? asmFilter)
    {
        if (!string.IsNullOrWhiteSpace(nsPrefix))
        {
            var ns = nsPrefix.Trim().TrimEnd('.');
            if (ns.Length > 0)
            {
                sql += @" AND (t.namespace = $nsExact OR t.namespace LIKE $nsPrefix ESCAPE '\')";
                cmd.Parameters.AddWithValue("$nsExact", ns);
                cmd.Parameters.AddWithValue("$nsPrefix", EscapeLike(ns) + ".%");
            }
        }
        if (!string.IsNullOrWhiteSpace(asmFilter))
        {
            sql += @" AND a.name LIKE $asmFilter ESCAPE '\'";
            cmd.Parameters.AddWithValue("$asmFilter", "%" + EscapeLike(asmFilter.Trim()) + "%");
        }
    }

    internal static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static TypeRow ReadTypeRow(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        AssemblyName: r.GetString(1),
        AssemblyPath: r.GetString(2),
        XmlPath: r.IsDBNull(3) ? null : r.GetString(3),
        FullName: r.GetString(4),
        BareFullName: r.GetString(5),
        ClrName: r.GetString(6),
        Namespace: r.GetString(7),
        Name: r.GetString(8),
        SimpleName: r.GetString(9),
        Kind: r.GetString(10),
        Accessibility: r.GetString(11),
        Modifiers: r.GetString(12),
        BaseType: r.IsDBNull(13) ? null : r.GetString(13),
        BaseSimple: r.IsDBNull(14) ? null : r.GetString(14),
        Interfaces: r.GetString(15),
        GenericParams: r.GetString(16),
        GenericConstraints: r.GetString(17),
        DelegateSignature: r.IsDBNull(18) ? null : r.GetString(18),
        Summary: r.IsDBNull(19) ? null : r.GetString(19),
        Remarks: r.IsDBNull(20) ? null : r.GetString(20));

    private static MemberRow ReadMemberRow(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        TypeId: r.GetInt64(1),
        DeclaringFullName: r.GetString(2),
        DeclaringSimpleName: r.GetString(3),
        AssemblyName: r.GetString(4),
        AssemblyPath: r.GetString(5),
        Kind: r.GetString(6),
        Name: r.GetString(7),
        Accessibility: r.GetString(8),
        Modifiers: r.GetString(9),
        ReturnType: r.GetString(10),
        Signature: r.GetString(11),
        ExtTargetType: r.IsDBNull(12) ? null : r.GetString(12),
        EnumValue: r.IsDBNull(13) ? null : r.GetString(13),
        Summary: r.IsDBNull(14) ? null : r.GetString(14),
        ParamsDoc: r.IsDBNull(15) ? null : r.GetString(15),
        ReturnsDoc: r.IsDBNull(16) ? null : r.GetString(16));

    internal IndexStats ReadStats(string? indexName, bool rebuilt, long elapsedMs)
    {
        var asmCount = (int)ScalarLong("SELECT COUNT(*) FROM assemblies");
        var xmlCount = (int)ScalarLong("SELECT COUNT(*) FROM assemblies WHERE xml_path IS NOT NULL");
        var typeCount = (int)ScalarLong("SELECT COUNT(*) FROM types");
        var memCount = (int)ScalarLong("SELECT COUNT(*) FROM members");
        var size = File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
        return new IndexStats(DbPath, indexName, rebuilt, asmCount, xmlCount, typeCount, memCount, elapsedMs, size);
    }

    public string? GetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    internal long ScalarLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose() => _conn.Dispose();
}
