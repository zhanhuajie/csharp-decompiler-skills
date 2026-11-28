using Microsoft.Data.Sqlite;

namespace ApiLookup;

/// <summary>
/// Post-build pass that resolves <c>&lt;inheritdoc/&gt;</c> across every indexed assembly. Extraction only records the
/// marker (<c>inheritdoc</c> column: empty for implicit, otherwise the <c>cref</c> doc ID) because the documented
/// base member usually lives in a different assembly. Here, members without their own summary copy summary /
/// param / returns docs from:
/// <list type="number">
/// <item>the <c>cref</c> target (matched on <c>doc_id</c>), or</item>
/// <item>the same member (kind + name + parameter types) on the nearest base class, then on implemented interfaces.</item>
/// </list>
/// Chains (inheritdoc pointing at another inheritdoc) are handled by repeating the pass a few times.
/// The FTS entries are updated in place (rowids follow <see cref="IndexSchema.FtsRowIdForType"/> /
/// <see cref="IndexSchema.FtsRowIdForMember"/>) so inherited text is searchable.
/// </summary>
internal static class InheritDocResolver
{
    private const int MaxPasses = 4;
    private const int MaxBaseDepth = 16;

    public static void Resolve(SqliteConnection conn)
    {
        const string pendingSql = @"
SELECT (SELECT COUNT(*) FROM types WHERE inheritdoc IS NOT NULL AND summary IS NULL)
     + (SELECT COUNT(*) FROM members WHERE inheritdoc IS NOT NULL AND summary IS NULL)";
        if (Scalar(conn, null, pendingSql) == 0)
            return;

        using var tx = conn.BeginTransaction();
        var hasCref = Scalar(conn, tx, @"
SELECT EXISTS(SELECT 1 FROM types WHERE inheritdoc <> '' AND summary IS NULL)
    OR EXISTS(SELECT 1 FROM members WHERE inheritdoc <> '' AND summary IS NULL)") != 0;

        // doc_id lookups are only needed for explicit crefs; keep the index temporary so the DB stays small.
        if (hasCref)
            Execute(conn, tx, "CREATE INDEX tmp_types_doc_id ON types(doc_id); CREATE INDEX tmp_members_doc_id ON members(doc_id);");

        var ancestors = new AncestorCache(conn, tx);
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var changed = ResolveTypes(conn, tx, ancestors) + ResolveMembers(conn, tx, ancestors);
            if (changed == 0) break;
        }

        if (hasCref)
            Execute(conn, tx, "DROP INDEX tmp_types_doc_id; DROP INDEX tmp_members_doc_id;");
        tx.Commit();
    }

    private static int ResolveTypes(SqliteConnection conn, SqliteTransaction tx, AncestorCache ancestors)
    {
        var pending = new List<(long Id, string Cref, string SimpleName, string FullName, string? BaseSimple, string Interfaces)>();
        using (var cmd = Command(conn, tx, @"
SELECT id, inheritdoc, simple_name, full_name, base_simple, interfaces
FROM types WHERE inheritdoc IS NOT NULL AND summary IS NULL"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                pending.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetNullableString(4), r.GetString(5)));
        }

        var changed = 0;
        foreach (var t in pending)
        {
            (string Summary, string? Remarks)? source = null;
            if (t.Cref.Length > 0)
            {
                source = FindTypeDoc(conn, tx, "doc_id = $key", t.Cref);
            }
            else
            {
                foreach (var ancestorId in ancestors.Get(t.Id))
                {
                    source = FindTypeDoc(conn, tx, "id = $key", ancestorId);
                    if (source != null) break;
                }
            }
            if (source is not { } doc) continue;

            using (var upd = Command(conn, tx, "UPDATE types SET summary = $s, remarks = COALESCE(remarks, $r) WHERE id = $id"))
            {
                upd.Parameters.AddWithValue("$s", doc.Summary);
                upd.Parameters.AddWithValue("$r", (object?)doc.Remarks ?? DBNull.Value);
                upd.Parameters.AddWithValue("$id", t.Id);
                upd.ExecuteNonQuery();
            }
            UpdateFts(conn, tx, IndexSchema.FtsRowIdForType(t.Id), doc.Summary,
                IndexBuilder.BuildTypeTokens(t.SimpleName, t.FullName, t.BaseSimple, t.Interfaces, doc.Summary));
            changed++;
        }
        return changed;
    }

    private static int ResolveMembers(SqliteConnection conn, SqliteTransaction tx, AncestorCache ancestors)
    {
        var pending = new List<(long Id, long TypeId, string Kind, string Name, string ParamTypes, string ReturnSimple, string Cref, string TypeSimple)>();
        using (var cmd = Command(conn, tx, @"
SELECT m.id, m.type_id, m.kind, m.name, m.param_types, m.return_simple, m.inheritdoc, t.simple_name
FROM members m JOIN types t ON t.id = m.type_id
WHERE m.inheritdoc IS NOT NULL AND m.summary IS NULL"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                pending.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetString(6), r.GetString(7)));
            }
        }

        var changed = 0;
        foreach (var m in pending)
        {
            MemberDoc? source = null;
            if (m.Cref.Length > 0)
            {
                source = FindMemberDocByDocId(conn, tx, m.Cref);
            }
            else
            {
                // Explicit interface implementations are named "Ns.IFoo.Bar"; match on the trailing member name.
                var leafName = m.Name.Contains('.') ? m.Name[(m.Name.LastIndexOf('.') + 1)..] : m.Name;
                foreach (var ancestorId in ancestors.Get(m.TypeId))
                {
                    source = FindMatchingMemberDoc(conn, tx, ancestorId, m.Kind, leafName, m.ParamTypes);
                    if (source != null) break;
                }
            }
            if (source is not { } doc) continue;

            using (var upd = Command(conn, tx, @"
UPDATE members SET summary = $s, params_doc = COALESCE(params_doc, $p), returns_doc = COALESCE(returns_doc, $r)
WHERE id = $id"))
            {
                upd.Parameters.AddWithValue("$s", doc.Summary);
                upd.Parameters.AddWithValue("$p", (object?)doc.ParamsDoc ?? DBNull.Value);
                upd.Parameters.AddWithValue("$r", (object?)doc.ReturnsDoc ?? DBNull.Value);
                upd.Parameters.AddWithValue("$id", m.Id);
                upd.ExecuteNonQuery();
            }
            UpdateFts(conn, tx, IndexSchema.FtsRowIdForMember(m.Id), doc.Summary,
                IndexBuilder.BuildMemberTokens(m.Name, m.TypeSimple, m.ReturnSimple, m.ParamTypes, doc.Summary));
            changed++;
        }
        return changed;
    }

    private sealed record MemberDoc(string Summary, string? ParamsDoc, string? ReturnsDoc);

    private static (string Summary, string? Remarks)? FindTypeDoc(SqliteConnection conn, SqliteTransaction tx, string predicate, object key)
    {
        using var cmd = Command(conn, tx, $"SELECT summary, remarks FROM types WHERE {predicate} AND summary IS NOT NULL LIMIT 1");
        cmd.Parameters.AddWithValue("$key", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetNullableString(1)) : null;
    }

    private static MemberDoc? FindMemberDocByDocId(SqliteConnection conn, SqliteTransaction tx, string docId)
    {
        if (docId.StartsWith("T:", StringComparison.Ordinal))
        {
            var typeDoc = FindTypeDoc(conn, tx, "doc_id = $key", docId);
            return typeDoc is { } t ? new MemberDoc(t.Summary, null, null) : null;
        }

        using var cmd = Command(conn, tx, "SELECT summary, params_doc, returns_doc FROM members WHERE doc_id = $key AND summary IS NOT NULL LIMIT 1");
        cmd.Parameters.AddWithValue("$key", docId);
        return ReadMemberDoc(cmd);
    }

    private static MemberDoc? FindMatchingMemberDoc(SqliteConnection conn, SqliteTransaction tx, long typeId, string kind, string name, string paramTypes)
    {
        // Constructors are named after their declaring type, so match base constructors on signature only.
        var nameClause = kind == MemberKinds.Constructor ? string.Empty : " AND name = $name";
        using var cmd = Command(conn, tx, $@"
SELECT summary, params_doc, returns_doc FROM members
WHERE type_id = $tid AND kind = $kind{nameClause} AND param_types = $ptypes AND summary IS NOT NULL
LIMIT 1");
        cmd.Parameters.AddWithValue("$tid", typeId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$ptypes", paramTypes);
        if (nameClause.Length > 0)
            cmd.Parameters.AddWithValue("$name", name);
        return ReadMemberDoc(cmd);
    }

    private static MemberDoc? ReadMemberDoc(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        return r.Read() ? new MemberDoc(r.GetString(0), r.GetNullableString(1), r.GetNullableString(2)) : null;
    }

    private static void UpdateFts(SqliteConnection conn, SqliteTransaction tx, long rowId, string summary, string tokens)
    {
        using var cmd = Command(conn, tx, "UPDATE fts_entries SET summary = $s, tokens = $t WHERE rowid = $rowid");
        cmd.Parameters.AddWithValue("$s", summary);
        cmd.Parameters.AddWithValue("$t", tokens);
        cmd.Parameters.AddWithValue("$rowid", rowId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Ordered ancestors of a type: base classes nearest-first (resolved by exact CLR name, preferring the same
    /// assembly), followed by all implemented interfaces breadth-first (including interfaces' parent interfaces).
    /// </summary>
    private sealed class AncestorCache
    {
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;
        private readonly Dictionary<long, IReadOnlyList<long>> _cache = new();

        public AncestorCache(SqliteConnection conn, SqliteTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public IReadOnlyList<long> Get(long typeId)
        {
            if (_cache.TryGetValue(typeId, out var cached)) return cached;

            var result = new List<long>();
            var visited = new HashSet<long> { typeId };

            var current = typeId;
            for (var depth = 0; depth < MaxBaseDepth; depth++)
            {
                var (baseClr, asmId) = ReadBase(current);
                if (baseClr == null) break;
                var baseId = ResolveClr(baseClr, asmId);
                if (baseId == null || !visited.Add(baseId.Value)) break;
                result.Add(baseId.Value);
                current = baseId.Value;
            }

            var queue = new Queue<long>();
            queue.Enqueue(typeId);
            foreach (var b in result) queue.Enqueue(b);
            var interfaces = new List<long>();
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                var asmId = ReadBase(t).AssemblyId;
                foreach (var ifaceClr in ReadInterfaces(t))
                {
                    var ifaceId = ResolveClr(ifaceClr, asmId);
                    if (ifaceId != null && visited.Add(ifaceId.Value))
                    {
                        interfaces.Add(ifaceId.Value);
                        queue.Enqueue(ifaceId.Value);
                    }
                }
            }
            result.AddRange(interfaces);

            _cache[typeId] = result;
            return result;
        }

        private (string? BaseClr, long AssemblyId) ReadBase(long typeId)
        {
            using var cmd = Command(_conn, _tx, "SELECT base_clr, assembly_id FROM types WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", typeId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetNullableString(0), r.GetInt64(1)) : (null, 0);
        }

        private List<string> ReadInterfaces(long typeId)
        {
            using var cmd = Command(_conn, _tx, "SELECT iface_clr FROM type_interfaces WHERE type_id = $id");
            cmd.Parameters.AddWithValue("$id", typeId);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }

        private long? ResolveClr(string clrName, long preferredAssemblyId)
        {
            using var cmd = Command(_conn, _tx, @"
SELECT id FROM types WHERE clr_name = $clr
ORDER BY CASE WHEN assembly_id = $asm THEN 0 ELSE 1 END, id
LIMIT 1");
            cmd.Parameters.AddWithValue("$clr", clrName);
            cmd.Parameters.AddWithValue("$asm", preferredAssemblyId);
            return cmd.ExecuteScalar() is long id ? id : null;
        }
    }

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = Command(conn, tx, sql);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = Command(conn, tx, sql);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
