using Microsoft.Data.Sqlite;

namespace ApiLookup;

/// <summary>
/// Read-only query layer over an index database. Returns raw rows/records only; presentation (signatures,
/// headers, JSON/text layout) is the responsibility of <see cref="OutputFormatter"/>.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private const int MaxBaseDepth = 16;

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

    private readonly SqliteConnection _conn;
    public string DbPath { get; }

    private IndexStore(string dbPath, SqliteConnection conn)
    {
        DbPath = dbPath;
        _conn = conn;
    }

    public static IndexStore OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"Index database not found: {dbPath}", dbPath);
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        try
        {
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA temp_store = MEMORY;";
                pragma.ExecuteNonQuery();
            }
            return new IndexStore(dbPath, conn);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public IReadOnlyList<SearchResult> Search(
        string query,
        string? kindFilter = null,
        string? namespacePrefix = null,
        string? assemblyFilter = null,
        int limit = 30)
    {
        var ftsQuery = Tokenizer.BuildFtsQuery(query);
        if (ftsQuery == null) return Array.Empty<SearchResult>();

        // fts_entries has 5 UNINDEXED columns first (entity_type, entity_id, kind, assembly_name, namespace),
        // followed by 4 searchable columns (full_name, name, tokens, summary).
        var sql = @"
SELECT
    f.entity_type,
    f.entity_id,
    f.namespace,
    bm25(fts_entries, 0.0, 0.0, 0.0, 0.0, 0.0, 2.0, 6.0, 1.5, 0.8) AS rank_score,
    CASE
        WHEN LOWER(f.name) = LOWER($exact) AND f.entity_type = 'type' THEN 0
        WHEN LOWER(f.full_name) = LOWER($exact) THEN 1
        WHEN LOWER(f.full_name) LIKE LOWER($dotSuffix) ESCAPE '\' THEN 2
        WHEN LOWER(f.name) = LOWER($exact) THEN 3
        WHEN LOWER(f.name) LIKE LOWER($prefix) ESCAPE '\' AND f.entity_type = 'type' THEN 4
        WHEN LOWER(f.name) LIKE LOWER($prefix) ESCAPE '\' THEN 5
        WHEN f.entity_type = 'type' THEN 6
        ELSE 7
    END AS priority
FROM fts_entries f
WHERE fts_entries MATCH $fts";

        using var cmd = _conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(kindFilter))
        {
            if (kindFilter.Equals(EntityTypes.Type, StringComparison.OrdinalIgnoreCase))
                sql += " AND f.entity_type = 'type'";
            else if (kindFilter.Equals(EntityTypes.Member, StringComparison.OrdinalIgnoreCase))
                sql += " AND f.entity_type = 'member'";
            else
            {
                sql += " AND LOWER(f.kind) = LOWER($kind)";
                cmd.Parameters.AddWithValue("$kind", kindFilter.Trim());
            }
        }

        var trimmedQuery = query.Trim();
        var normalizedDotQuery = trimmedQuery.Replace('+', '.');
        cmd.Parameters.AddWithValue("$fts", ftsQuery);
        cmd.Parameters.AddWithValue("$exact", normalizedDotQuery);
        cmd.Parameters.AddWithValue("$dotSuffix", "%." + EscapeLike(normalizedDotQuery));
        cmd.Parameters.AddWithValue("$prefix", EscapeLike(trimmedQuery) + "%");
        cmd.Parameters.AddWithValue("$limit", limit);

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

        var rawHits = new List<(string EntityType, long EntityId, string Namespace, double Score)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rawHits.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetDouble(3)));
        }

        if (rawHits.Count == 0) return Array.Empty<SearchResult>();

        // Batch-hydrate types and members in at most 2 queries instead of N+1.
        var typeMap = BatchLoadTypes(rawHits.Where(h => h.EntityType == EntityTypes.Type).Select(h => h.EntityId).Distinct().ToList());
        var memberMap = BatchLoadMembers(rawHits.Where(h => h.EntityType == EntityTypes.Member).Select(h => h.EntityId).Distinct().ToList());

        var results = new List<SearchResult>(rawHits.Count);
        foreach (var h in rawHits)
        {
            if (h.EntityType == EntityTypes.Type)
            {
                if (typeMap.TryGetValue(h.EntityId, out var tr))
                    results.Add(new SearchResult(EntityTypes.Type, h.EntityId, h.Score, h.Namespace, tr, null));
            }
            else if (memberMap.TryGetValue(h.EntityId, out var mr))
            {
                results.Add(new SearchResult(EntityTypes.Member, h.EntityId, h.Score, h.Namespace, null, mr));
            }
        }

        return results;
    }

    public IReadOnlyList<TypeRow> ResolveType(string query, string? namespacePrefix = null, string? assemblyFilter = null)
    {
        var q = query.Trim();
        var qBare = TypeNameHelper.StripGenerics(q);
        var qDot = qBare.Replace('+', '.');

        // 1. Indexed exact match on full_name, bare_full_name, or clr_name (COLLATE NOCASE)
        var exact = QueryTypes(
            TypeRowReader.Select + @"
WHERE (t.full_name = $q OR t.bare_full_name = $qDot OR t.clr_name = $q)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$q", q);
                cmd.Parameters.AddWithValue("$qDot", qDot);
            },
            namespacePrefix,
            assemblyFilter);
        if (exact.Count > 0) return exact;

        // 2. Indexed simple_name / name match when query is unqualified
        if (!q.Contains('.') && !q.Contains('+'))
        {
            var bySimple = QueryTypes(
                TypeRowReader.Select + @"
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

        // 3. Suffix match (e.g. "PnP3dObjects.Equipment", "Outer.Inner", "Outer+Inner", or multi-level "Middle+Inner")
        var suffixDot = "%." + EscapeLike(qDot);
        var suffixClrDot = "%." + EscapeLike(qBare);
        var suffixClrPlus = "%+" + EscapeLike(qBare);
        return QueryTypes(
            TypeRowReader.Select + @"
WHERE (t.bare_full_name LIKE $suffixDot ESCAPE '\'
    OR t.clr_name LIKE $suffixClrDot ESCAPE '\'
    OR t.clr_name LIKE $suffixClrPlus ESCAPE '\')",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$suffixDot", suffixDot);
                cmd.Parameters.AddWithValue("$suffixClrDot", suffixClrDot);
                cmd.Parameters.AddWithValue("$suffixClrPlus", suffixClrPlus);
            },
            namespacePrefix,
            assemblyFilter);
    }

    /// <summary>Exact lookup by CLR name (e.g. <c>Ns.Base`1</c>), which never confuses <c>Foo</c> with <c>Foo&lt;T&gt;</c>.</summary>
    private List<TypeRow> ResolveTypeByClr(string clrName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = TypeRowReader.Select + " WHERE t.clr_name = $clr ORDER BY t.full_name ASC, a.name ASC LIMIT 50";
        cmd.Parameters.AddWithValue("$clr", clrName);
        return TypeRowReader.ReadAll(cmd);
    }

    /// <summary>
    /// Resolves a declared base class or interface. When the CLR name is known it is authoritative (no fuzzy fallback
    /// that could pick a different generic arity); name-based resolution is only used for rows without it.
    /// </summary>
    private IReadOnlyList<TypeRow> ResolveReferencedType(string? clrName, string fallbackName)
        => !string.IsNullOrEmpty(clrName) ? ResolveTypeByClr(clrName) : ResolveType(fallbackName);

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
        return TypeRowReader.ReadAll(cmd);
    }

    public IReadOnlyList<MemberRow> GetMembers(long typeId, string? memberFilter = null)
    {
        var sql = MemberRowReader.Select + @"
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
        return MemberRowReader.ReadAll(cmd);
    }

    /// <summary>
    /// Resolved base classes (nearest first) for classes/structs, or all transitively inherited interfaces for
    /// interfaces. Implicit CLR bases (System.Object, System.ValueType, ...) are excluded.
    /// </summary>
    public List<TypeRow> GetBaseTypeChain(TypeRow target, int maxDepth = MaxBaseDepth)
    {
        if (TypeKinds.IsInterface(target.Kind))
            return GetParentInterfaceChain(target, maxDepth);

        return WalkBaseClasses(target, maxDepth, stopAtImplicitBase: true)
            .Where(step => step.Row != null)
            .Select(step => step.Row!)
            .ToList();
    }

    /// <summary>
    /// Walks the base-class chain one declared base at a time, preferring definitions from the same assembly.
    /// Each step carries the declared base name and its resolved row; the walk ends after the first step that
    /// cannot be resolved (missing from the index, or a cycle), which is yielded with <c>Row == null</c>.
    /// </summary>
    private List<(string DeclaredName, TypeRow? Row)> WalkBaseClasses(TypeRow target, int maxDepth, bool stopAtImplicitBase)
    {
        var steps = new List<(string, TypeRow?)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.BareFullName };
        var currentBase = target.BaseType;
        var currentBaseClr = target.BaseClrName;
        var currentAsmPath = target.AssemblyPath;

        for (var i = 0; i < maxDepth && !string.IsNullOrEmpty(currentBase); i++)
        {
            var bareBase = TypeNameHelper.StripGenerics(currentBase);
            if (stopAtImplicitBase && WellKnownTypes.IsImplicitBase(bareBase))
                break;

            if (!visited.Add(bareBase))
            {
                steps.Add((currentBase, null));
                break;
            }

            var matches = ResolveReferencedType(currentBaseClr, bareBase);
            if (matches.Count == 0)
            {
                steps.Add((currentBase, null));
                break;
            }

            var baseRow = PickPreferredAssemblyMatch(matches, currentAsmPath);
            steps.Add((currentBase, baseRow));
            currentBase = baseRow.BaseType;
            currentBaseClr = baseRow.BaseClrName;
            currentAsmPath = baseRow.AssemblyPath;
        }
        return steps;
    }

    private List<TypeRow> GetParentInterfaceChain(TypeRow target, int maxDepth)
    {
        var chain = new List<TypeRow>();
        var visitedIds = new HashSet<long> { target.Id };
        var queue = new Queue<(TypeRow Row, int Depth)>();
        queue.Enqueue((target, 0));

        while (queue.Count > 0)
        {
            var (cur, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (var (ifaceFull, ifaceSimple, ifaceClr) in LoadInterfacesForTypes(new List<long> { cur.Id }))
            {
                var query = !string.IsNullOrEmpty(ifaceFull) ? ifaceFull : ifaceSimple;
                if (string.IsNullOrEmpty(query) && string.IsNullOrEmpty(ifaceClr)) continue;

                var matches = ResolveReferencedType(ifaceClr, query);
                if (matches.Count == 0) continue;

                var parentRow = PickPreferredAssemblyMatch(matches, cur.AssemblyPath);
                if (visitedIds.Add(parentRow.Id))
                {
                    chain.Add(parentRow);
                    queue.Enqueue((parentRow, depth + 1));
                }
            }
        }

        return chain;
    }

    private static TypeRow PickPreferredAssemblyMatch(IReadOnlyList<TypeRow> matches, string preferredAssemblyPath)
        => matches.FirstOrDefault(m => string.Equals(m.AssemblyPath, preferredAssemblyPath, StringComparison.OrdinalIgnoreCase))
           ?? matches[0];

    public IReadOnlyList<MemberRow> GetExtensionMethods(TypeRow target, IReadOnlyList<TypeRow> baseChain, string? memberFilter = null)
    {
        var queryFulls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.BareFullName };
        var querySimples = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.SimpleName };
        var satisfiedFulls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.BareFullName };
        var satisfiedSimples = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.SimpleName };

        void AddSatisfied(string full)
        {
            satisfiedFulls.Add(full);
            satisfiedSimples.Add(TypeNameHelper.ExtractSimpleTypeName(full));
        }

        void AddBaseTypeReference(string? baseType)
        {
            if (string.IsNullOrEmpty(baseType)) return;
            var bareBase = TypeNameHelper.StripGenerics(baseType);
            switch (bareBase)
            {
                case WellKnownTypes.ValueType:
                    AddSatisfied(WellKnownTypes.ValueType);
                    return;
                case WellKnownTypes.Enum:
                    AddSatisfied(WellKnownTypes.Enum);
                    AddSatisfied(WellKnownTypes.ValueType);
                    return;
                case WellKnownTypes.Object or WellKnownTypes.MulticastDelegate or WellKnownTypes.Delegate:
                    return;
            }

            var simpleBase = TypeNameHelper.ExtractSimpleTypeName(bareBase);
            queryFulls.Add(bareBase);
            satisfiedFulls.Add(bareBase);
            if (!string.IsNullOrEmpty(simpleBase))
            {
                querySimples.Add(simpleBase);
                satisfiedSimples.Add(simpleBase);
            }
        }

        AddBaseTypeReference(target.BaseType);
        foreach (var b in baseChain)
        {
            queryFulls.Add(b.BareFullName);
            querySimples.Add(b.SimpleName);
            satisfiedFulls.Add(b.BareFullName);
            satisfiedSimples.Add(b.SimpleName);
            AddBaseTypeReference(b.BaseType);
        }

        // Load exact normalized interfaces from type_interfaces table (handles comma-containing generic interfaces)
        var typeIds = new List<long>(1 + baseChain.Count) { target.Id };
        foreach (var b in baseChain) typeIds.Add(b.Id);

        foreach (var (ifaceFull, ifaceSimple, _) in LoadInterfacesForTypes(typeIds))
        {
            satisfiedFulls.Add(ifaceFull);
            if (!string.IsNullOrEmpty(ifaceSimple))
                satisfiedSimples.Add(ifaceSimple);

            if (NoisyExtensionInterfaces.Contains(ifaceFull) || NoisyExtensionInterfaces.Contains(ifaceSimple))
                continue;

            queryFulls.Add(ifaceFull);
            if (!string.IsNullOrEmpty(ifaceSimple))
                querySimples.Add(ifaceSimple);
        }

        if (querySimples.Count == 0) return Array.Empty<MemberRow>();

        // Step 1: Collect candidate member IDs matching at least one non-noisy target.
        var candidateIds = new List<long>();
        var seenCandidateIds = new HashSet<long>();
        var hasMemberFilter = !string.IsNullOrWhiteSpace(memberFilter);
        var memberFilterPattern = hasMemberFilter ? "%" + EscapeLike(memberFilter!.Trim()) + "%" : null;

        foreach (var chunk in querySimples.Chunk(500))
        {
            using var cmd = _conn.CreateCommand();
            var simpleParams = AddInParameters(cmd, "$s", chunk);

            var sql = @"
SELECT et.member_id, et.target_full, et.target_simple
FROM extension_targets et";
            if (hasMemberFilter)
                sql += @"
JOIN members m ON m.id = et.member_id";

            sql += $@"
WHERE et.target_simple IN ({simpleParams})";
            if (hasMemberFilter)
            {
                sql += @"
  AND m.name LIKE $mf ESCAPE '\'";
                cmd.Parameters.AddWithValue("$mf", memberFilterPattern!);
            }

            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var memberId = r.GetInt64(0);
                if (MatchesExtensionTarget(r.GetString(1), r.GetString(2), queryFulls, querySimples) &&
                    seenCandidateIds.Add(memberId))
                {
                    candidateIds.Add(memberId);
                }
            }
        }

        if (candidateIds.Count == 0) return Array.Empty<MemberRow>();

        // Step 2: Verify every required receiver constraint on each candidate member is satisfied.
        var allTargetConstraints = LoadExtensionTargetsForMembers(candidateIds);
        var matchedIds = candidateIds
            .Where(id => allTargetConstraints.TryGetValue(id, out var reqs) && reqs.Count > 0 &&
                         reqs.All(req => MatchesExtensionTarget(req.TargetFull, req.TargetSimple, satisfiedFulls, satisfiedSimples)))
            .ToList();

        if (matchedIds.Count == 0) return Array.Empty<MemberRow>();

        // Step 3: Batch-load verified extension members and return in deterministic order.
        return BatchLoadMembers(matchedIds).Values
            .OrderBy(m => m.DeclaringFullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id)
            .Take(200)
            .ToList();
    }

    private static bool MatchesExtensionTarget(
        string targetFull,
        string targetSimple,
        HashSet<string> targetFulls,
        HashSet<string> targetSimples)
    {
        if (targetFull.Contains('.'))
            return targetFulls.Contains(TypeNameHelper.StripGenerics(targetFull));
        return targetSimples.Contains(targetSimple);
    }

    public HierarchyResult GetHierarchy(TypeRow target, int limit = 100)
    {
        var ancestors = TypeKinds.IsInterface(target.Kind)
            ? GetParentInterfaceChain(target, MaxBaseDepth).Select(i => i.FullName).ToList()
            : WalkBaseClasses(target, MaxBaseDepth, stopAtImplicitBase: false).Select(step => step.DeclaredName).ToList();

        var derived = new List<TypeRow>();
        var seenDerived = new HashSet<long> { target.Id };
        var queue = new Queue<(string ClrName, string Kind)>();
        queue.Enqueue((target.ClrName, target.Kind));

        while (queue.Count > 0 && derived.Count < limit)
        {
            var (curClr, curKind) = queue.Dequeue();
            using var cmd = _conn.CreateCommand();

            // Exact CLR-name joins: implementers/subclasses of Foo<T> are never mixed up with those of Foo.
            cmd.CommandText = TypeKinds.IsInterface(curKind)
                ? TypeRowReader.Select + @"
JOIN type_interfaces ti ON ti.type_id = t.id
WHERE ti.iface_clr = $clr
ORDER BY t.full_name ASC LIMIT $rem"
                : TypeRowReader.Select + @"
WHERE t.base_clr = $clr
ORDER BY t.full_name ASC LIMIT $rem";
            cmd.Parameters.AddWithValue("$clr", curClr);
            cmd.Parameters.AddWithValue("$rem", limit - derived.Count);

            foreach (var row in TypeRowReader.ReadAll(cmd))
            {
                if (derived.Count >= limit) break;
                if (seenDerived.Add(row.Id))
                {
                    derived.Add(row);
                    queue.Enqueue((row.ClrName, row.Kind));
                }
            }
        }

        return new HierarchyResult(target, ancestors, derived);
    }

    public IReadOnlyList<MemberRow> FindByType(
        string typeName,
        string role = TypeRefRoles.Both,
        string? namespacePrefix = null,
        string? assemblyFilter = null,
        int limit = 40)
    {
        var decorated = TypeNameHelper.StripTypeDecorations(typeName);
        if (string.IsNullOrEmpty(decorated)) return Array.Empty<MemberRow>();

        var isQualified = decorated.Contains('.') || decorated.Contains('+');
        var normalizedFull = decorated.Replace('+', '.');
        var simple = TypeNameHelper.ExtractSimpleTypeName(decorated);
        if (string.IsNullOrEmpty(simple)) return Array.Empty<MemberRow>();

        var normalizedRole = role.ToLowerInvariant();
        using var cmd = _conn.CreateCommand();

        var sql = MemberRowReader.Columns + @"
FROM member_type_refs r
JOIN members m ON m.id = r.member_id
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id";

        if (isQualified)
        {
            sql += @"
WHERE (r.type_full = $fullExact OR (r.type_simple = $simple AND r.type_full LIKE $fullSuffix ESCAPE '\'))";
            cmd.Parameters.AddWithValue("$fullExact", normalizedFull);
            cmd.Parameters.AddWithValue("$simple", simple);
            cmd.Parameters.AddWithValue("$fullSuffix", "%." + EscapeLike(normalizedFull));
        }
        else
        {
            sql += @"
WHERE r.type_simple = $simple";
            cmd.Parameters.AddWithValue("$simple", simple);
        }

        if (normalizedRole is TypeRefRoles.Return or TypeRefRoles.Param)
        {
            sql += " AND r.role = $role";
            cmd.Parameters.AddWithValue("$role", normalizedRole);
        }

        AppendTypeFilters(ref sql, cmd, namespacePrefix, assemblyFilter);

        sql += normalizedRole == TypeRefRoles.Both
            ? @"
GROUP BY m.id
ORDER BY MIN(CASE WHEN r.role = 'return' THEN 0 ELSE 1 END), t.full_name ASC, m.name ASC
LIMIT $limit"
            : @"
ORDER BY t.full_name ASC, m.name ASC
LIMIT $limit";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$limit", limit);
        return MemberRowReader.ReadAll(cmd);
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
                r.GetNullableString(3),
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
            cmd.CommandText = $"{TypeRowReader.Select} WHERE t.id IN ({AddInParameters(cmd, "$id", chunk)})";
            foreach (var row in TypeRowReader.ReadAll(cmd))
                map[row.Id] = row;
        }
        return map;
    }

    private Dictionary<long, MemberRow> BatchLoadMembers(List<long> ids)
    {
        var map = new Dictionary<long, MemberRow>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"{MemberRowReader.Select} WHERE m.id IN ({AddInParameters(cmd, "$id", chunk)})";
            foreach (var row in MemberRowReader.ReadAll(cmd))
                map[row.Id] = row;
        }
        return map;
    }

    private Dictionary<long, List<(string TargetFull, string TargetSimple)>> LoadExtensionTargetsForMembers(List<long> memberIds)
    {
        var map = new Dictionary<long, List<(string, string)>>(memberIds.Count);
        foreach (var chunk in memberIds.Chunk(500))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT member_id, target_full, target_simple FROM extension_targets WHERE member_id IN ({AddInParameters(cmd, "$mid", chunk)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var mid = r.GetInt64(0);
                if (!map.TryGetValue(mid, out var list))
                {
                    list = new List<(string, string)>(2);
                    map[mid] = list;
                }
                list.Add((r.GetString(1), r.GetString(2)));
            }
        }
        return map;
    }

    private List<(string FullName, string SimpleName, string ClrName)> LoadInterfacesForTypes(List<long> typeIds)
    {
        var list = new List<(string, string, string)>();
        foreach (var chunk in typeIds.Chunk(500))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT iface_full, iface_simple, iface_clr FROM type_interfaces WHERE type_id IN ({AddInParameters(cmd, "$tid", chunk)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        return list;
    }

    /// <summary>Binds <paramref name="values"/> as <c>{prefix}0..N</c> and returns the comma-separated placeholder list.</summary>
    private static string AddInParameters<T>(SqliteCommand cmd, string prefix, IReadOnlyList<T> values)
    {
        var placeholders = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            placeholders[i] = prefix + i;
            cmd.Parameters.AddWithValue(placeholders[i], values[i]!);
        }
        return string.Join(", ", placeholders);
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
