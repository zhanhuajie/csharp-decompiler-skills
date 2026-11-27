using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ApiLookup;

public sealed record IndexStats(
    string DbPath,
    string? IndexName,
    bool Rebuilt,
    int AssemblyCount,
    int XmlDocCount,
    int TypeCount,
    int MemberCount,
    long ElapsedMs,
    long DbSizeBytes);

public sealed record IndexInfo(
    string Name,
    string DbPath,
    string BuiltAtUtc,
    int AssemblyCount,
    int TypeCount,
    int MemberCount,
    long DbSizeBytes,
    string Sources);

public sealed record SearchHit(
    string EntityType,     // "type" | "member"
    long EntityId,
    string Kind,           // class | struct | interface | enum | delegate | method | property | ctor | field | event
    string AssemblyName,
    string AssemblyPath,
    string Namespace,
    string FullName,       // Full type name, or DeclaringType.MemberName
    string Name,
    string Signature,
    string? Summary,
    double Score);

public sealed record TypeRow(
    long Id,
    string AssemblyName,
    string AssemblyPath,
    string? XmlPath,
    string FullName,
    string ClrName,
    string Namespace,
    string Name,
    string SimpleName,
    string Kind,
    string Accessibility,
    string Modifiers,
    string? BaseType,
    string? BaseSimple,
    string Interfaces,
    string GenericParams,
    string? Summary,
    string? Remarks);

public sealed record MemberRow(
    long Id,
    long TypeId,
    string DeclaringFullName,
    string DeclaringSimpleName,
    string AssemblyName,
    string Kind,
    string Name,
    string Accessibility,
    string Modifiers,
    string ReturnType,
    string Signature,
    string? ExtTargetType,
    string? EnumValue,
    string? Summary,
    string? ParamsDoc,
    string? ReturnsDoc);

public sealed record AssemblyRow(
    string Name,
    string Path,
    string Version,
    string? XmlPath,
    int TypeCount);

public sealed record HierarchyResult(
    TypeRow Target,
    IReadOnlyList<string> AncestorChain,
    IReadOnlyList<TypeRow> DerivedTypes);

public sealed class IndexStore : IDisposable
{
    public const string SchemaVersion = "1";

    private readonly SqliteConnection _conn;
    public string DbPath { get; }

    private IndexStore(string dbPath, SqliteConnection conn)
    {
        DbPath = dbPath;
        _conn = conn;
    }

    public static string DefaultCacheDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(baseDir, "csharp-api-lookup", "indexes");
    }

    public static string GetNamedIndexPath(string name)
    {
        var safe = SanitizeName(name);
        return Path.Combine(DefaultCacheDirectory(), $"named-{safe}.db");
    }

    public static string GetAutoCachePath(string sourceKey)
        => Path.Combine(DefaultCacheDirectory(), $"auto-{sourceKey}.db");

    public static string ResolveDbPath(string? indexName, string? explicitDbPath, string? sourceKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitDbPath))
            return Path.GetFullPath(explicitDbPath);
        if (!string.IsNullOrWhiteSpace(indexName))
            return GetNamedIndexPath(indexName);
        if (!string.IsNullOrWhiteSpace(sourceKey))
            return GetAutoCachePath(sourceKey);
        throw new InvalidOperationException("No index name, db path, or source provided.");
    }

    public static IndexStore OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"Index database not found: {dbPath}", dbPath);
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        return new IndexStore(dbPath, conn);
    }

    public static (IndexStore Store, IndexStats Stats) EnsureBuilt(
        string dbPath,
        string? indexName,
        ResolvedSources sources,
        string sourceDescription,
        bool includeNonPublic,
        bool forceRebuild)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var effectiveFingerprint = $"{SchemaVersion}|{(includeNonPublic ? "all" : "pub")}|{sources.Fingerprint}";

        if (!forceRebuild && File.Exists(dbPath))
        {
            try
            {
                var existing = OpenReadOnly(dbPath);
                if (existing.GetMeta("fingerprint") == effectiveFingerprint)
                {
                    var stats = existing.ReadStats(indexName, rebuilt: false, sw.ElapsedMilliseconds);
                    return (existing, stats);
                }
                existing.Dispose();
            }
            catch
            {
                // Corrupt or older schema -> rebuild below
            }
        }

        // Build into a temp file first, then atomically replace target DB
        var tempPath = dbPath + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            // Extract metadata in parallel across assemblies (CPU-bound PE + XML parsing)
            var extracted = new ExtractedAssembly?[sources.Assemblies.Count];
            Parallel.For(0, sources.Assemblies.Count, i =>
            {
                extracted[i] = MetadataExtractor.Extract(sources.Assemblies[i].Path, includeNonPublic);
            });

            using (var conn = new SqliteConnection($"Data Source={tempPath};Mode=ReadWriteCreate"))
            {
                conn.Open();
                ExecuteNonQuery(conn, @"
PRAGMA journal_mode = MEMORY;
PRAGMA synchronous = OFF;
PRAGMA temp_store = MEMORY;
PRAGMA cache_size = -64000;
");
                CreateSchema(conn);

                using var tx = conn.BeginTransaction();
                InsertAll(conn, tx, extracted, indexName, effectiveFingerprint, sourceDescription);
                tx.Commit();

                ExecuteNonQuery(conn, "INSERT INTO fts_entries(fts_entries) VALUES('optimize');");
            }
            SqliteConnection.ClearAllPools();

            if (File.Exists(dbPath)) File.Delete(dbPath);
            File.Move(tempPath, dbPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        var store = OpenReadOnly(dbPath);
        var builtStats = store.ReadStats(indexName, rebuilt: true, sw.ElapsedMilliseconds);
        return (store, builtStats);
    }

    public static IReadOnlyList<IndexInfo> ListIndexes()
    {
        var dir = DefaultCacheDirectory();
        if (!Directory.Exists(dir)) return Array.Empty<IndexInfo>();

        var list = new List<IndexInfo>();
        foreach (var file in Directory.GetFiles(dir, "*.db").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var store = OpenReadOnly(file);
                var name = store.GetMeta("index_name") ?? Path.GetFileNameWithoutExtension(file);
                var builtAt = store.GetMeta("built_at_utc") ?? "";
                var sources = store.GetMeta("sources") ?? "";
                var asmCount = (int)store.ScalarLong("SELECT COUNT(*) FROM assemblies");
                var typeCount = (int)store.ScalarLong("SELECT COUNT(*) FROM types");
                var memCount = (int)store.ScalarLong("SELECT COUNT(*) FROM members");
                var size = new FileInfo(file).Length;
                list.Add(new IndexInfo(name, file, builtAt, asmCount, typeCount, memCount, size, sources));
            }
            catch
            {
                // Ignore unreadable files
            }
        }
        return list;
    }

    public static bool DropNamedIndex(string name)
    {
        var path = GetNamedIndexPath(name);
        if (!File.Exists(path)) return false;
        SqliteConnection.ClearAllPools();
        File.Delete(path);
        return true;
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        ExecuteNonQuery(conn, @"
CREATE TABLE meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE assemblies (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    path TEXT NOT NULL,
    version TEXT NOT NULL,
    xml_path TEXT,
    file_size INTEGER NOT NULL,
    mtime_utc INTEGER NOT NULL,
    type_count INTEGER NOT NULL
);

CREATE TABLE types (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    assembly_id INTEGER NOT NULL,
    full_name TEXT NOT NULL,
    clr_name TEXT NOT NULL,
    namespace TEXT NOT NULL,
    name TEXT NOT NULL,
    simple_name TEXT NOT NULL,
    kind TEXT NOT NULL,
    accessibility TEXT NOT NULL,
    modifiers TEXT NOT NULL,
    base_type TEXT,
    base_simple TEXT,
    interfaces TEXT NOT NULL,
    generic_params TEXT NOT NULL,
    is_extension_container INTEGER NOT NULL,
    summary TEXT,
    remarks TEXT,
    doc_id TEXT NOT NULL
);

CREATE TABLE members (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type_id INTEGER NOT NULL,
    assembly_id INTEGER NOT NULL,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    accessibility TEXT NOT NULL,
    modifiers TEXT NOT NULL,
    return_type TEXT NOT NULL,
    return_simple TEXT NOT NULL,
    signature TEXT NOT NULL,
    param_types TEXT NOT NULL,
    ext_target_type TEXT,
    ext_target_simple TEXT,
    enum_value TEXT,
    summary TEXT,
    params_doc TEXT,
    returns_doc TEXT,
    doc_id TEXT NOT NULL
);

CREATE VIRTUAL TABLE fts_entries USING fts5(
    entity_type UNINDEXED,
    entity_id UNINDEXED,
    kind UNINDEXED,
    assembly_name UNINDEXED,
    namespace UNINDEXED,
    full_name,
    name,
    tokens,
    summary,
    tokenize = 'unicode61'
);

CREATE INDEX idx_types_full_name ON types(full_name COLLATE NOCASE);
CREATE INDEX idx_types_clr_name ON types(clr_name COLLATE NOCASE);
CREATE INDEX idx_types_simple_name ON types(simple_name COLLATE NOCASE);
CREATE INDEX idx_types_base_type ON types(base_type COLLATE NOCASE);
CREATE INDEX idx_types_base_simple ON types(base_simple COLLATE NOCASE);
CREATE INDEX idx_types_namespace ON types(namespace COLLATE NOCASE);

CREATE INDEX idx_members_type_id ON members(type_id);
CREATE INDEX idx_members_name ON members(name COLLATE NOCASE);
CREATE INDEX idx_members_return_simple ON members(return_simple COLLATE NOCASE);
CREATE INDEX idx_members_ext_target ON members(ext_target_type COLLATE NOCASE);
CREATE INDEX idx_members_ext_simple ON members(ext_target_simple COLLATE NOCASE);
");
    }

    private static void InsertAll(
        SqliteConnection conn,
        SqliteTransaction tx,
        ExtractedAssembly?[] assemblies,
        string? indexName,
        string fingerprint,
        string sourceDescription)
    {
        using var metaCmd = conn.CreateCommand();
        metaCmd.Transaction = tx;
        metaCmd.CommandText = "INSERT INTO meta(key, value) VALUES($k, $v)";
        var pMetaK = metaCmd.Parameters.Add("$k", SqliteType.Text);
        var pMetaV = metaCmd.Parameters.Add("$v", SqliteType.Text);

        void PutMeta(string k, string v)
        {
            pMetaK.Value = k;
            pMetaV.Value = v;
            metaCmd.ExecuteNonQuery();
        }

        PutMeta("schema_version", SchemaVersion);
        PutMeta("fingerprint", fingerprint);
        PutMeta("built_at_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        PutMeta("sources", sourceDescription);
        if (!string.IsNullOrWhiteSpace(indexName))
            PutMeta("index_name", indexName);

        using var asmCmd = conn.CreateCommand();
        asmCmd.Transaction = tx;
        asmCmd.CommandText = @"
INSERT INTO assemblies(name, path, version, xml_path, file_size, mtime_utc, type_count)
VALUES($name, $path, $version, $xml, $size, $mtime, $tcount);
SELECT last_insert_rowid();";
        var pAsmName = asmCmd.Parameters.Add("$name", SqliteType.Text);
        var pAsmPath = asmCmd.Parameters.Add("$path", SqliteType.Text);
        var pAsmVer = asmCmd.Parameters.Add("$version", SqliteType.Text);
        var pAsmXml = asmCmd.Parameters.Add("$xml", SqliteType.Text);
        var pAsmSize = asmCmd.Parameters.Add("$size", SqliteType.Integer);
        var pAsmMtime = asmCmd.Parameters.Add("$mtime", SqliteType.Integer);
        var pAsmTCount = asmCmd.Parameters.Add("$tcount", SqliteType.Integer);

        using var typeCmd = conn.CreateCommand();
        typeCmd.Transaction = tx;
        typeCmd.CommandText = @"
INSERT INTO types(assembly_id, full_name, clr_name, namespace, name, simple_name, kind, accessibility, modifiers,
                  base_type, base_simple, interfaces, generic_params, is_extension_container, summary, remarks, doc_id)
VALUES($aid, $full, $clr, $ns, $name, $simple, $kind, $acc, $mod,
       $base, $baseSimple, $ifaces, $gp, $ext, $sum, $rem, $docId);
SELECT last_insert_rowid();";
        var pTAid = typeCmd.Parameters.Add("$aid", SqliteType.Integer);
        var pTFull = typeCmd.Parameters.Add("$full", SqliteType.Text);
        var pTClr = typeCmd.Parameters.Add("$clr", SqliteType.Text);
        var pTNs = typeCmd.Parameters.Add("$ns", SqliteType.Text);
        var pTName = typeCmd.Parameters.Add("$name", SqliteType.Text);
        var pTSimple = typeCmd.Parameters.Add("$simple", SqliteType.Text);
        var pTKind = typeCmd.Parameters.Add("$kind", SqliteType.Text);
        var pTAcc = typeCmd.Parameters.Add("$acc", SqliteType.Text);
        var pTMod = typeCmd.Parameters.Add("$mod", SqliteType.Text);
        var pTBase = typeCmd.Parameters.Add("$base", SqliteType.Text);
        var pTBaseS = typeCmd.Parameters.Add("$baseSimple", SqliteType.Text);
        var pTIfaces = typeCmd.Parameters.Add("$ifaces", SqliteType.Text);
        var pTGp = typeCmd.Parameters.Add("$gp", SqliteType.Text);
        var pTExt = typeCmd.Parameters.Add("$ext", SqliteType.Integer);
        var pTSum = typeCmd.Parameters.Add("$sum", SqliteType.Text);
        var pTRem = typeCmd.Parameters.Add("$rem", SqliteType.Text);
        var pTDocId = typeCmd.Parameters.Add("$docId", SqliteType.Text);

        using var memCmd = conn.CreateCommand();
        memCmd.Transaction = tx;
        memCmd.CommandText = @"
INSERT INTO members(type_id, assembly_id, kind, name, accessibility, modifiers, return_type, return_simple,
                    signature, param_types, ext_target_type, ext_target_simple, enum_value, summary, params_doc, returns_doc, doc_id)
VALUES($tid, $aid, $kind, $name, $acc, $mod, $ret, $retS,
       $sig, $ptypes, $extT, $extS, $eval, $sum, $pdoc, $rdoc, $docId);
SELECT last_insert_rowid();";
        var pMTid = memCmd.Parameters.Add("$tid", SqliteType.Integer);
        var pMAid = memCmd.Parameters.Add("$aid", SqliteType.Integer);
        var pMKind = memCmd.Parameters.Add("$kind", SqliteType.Text);
        var pMName = memCmd.Parameters.Add("$name", SqliteType.Text);
        var pMAcc = memCmd.Parameters.Add("$acc", SqliteType.Text);
        var pMMod = memCmd.Parameters.Add("$mod", SqliteType.Text);
        var pMRet = memCmd.Parameters.Add("$ret", SqliteType.Text);
        var pMRetS = memCmd.Parameters.Add("$retS", SqliteType.Text);
        var pMSig = memCmd.Parameters.Add("$sig", SqliteType.Text);
        var pMPtypes = memCmd.Parameters.Add("$ptypes", SqliteType.Text);
        var pMExtT = memCmd.Parameters.Add("$extT", SqliteType.Text);
        var pMExtS = memCmd.Parameters.Add("$extS", SqliteType.Text);
        var pMEval = memCmd.Parameters.Add("$eval", SqliteType.Text);
        var pMSum = memCmd.Parameters.Add("$sum", SqliteType.Text);
        var pMPdoc = memCmd.Parameters.Add("$pdoc", SqliteType.Text);
        var pMRdoc = memCmd.Parameters.Add("$rdoc", SqliteType.Text);
        var pMDocId = memCmd.Parameters.Add("$docId", SqliteType.Text);

        using var ftsCmd = conn.CreateCommand();
        ftsCmd.Transaction = tx;
        ftsCmd.CommandText = @"
INSERT INTO fts_entries(entity_type, entity_id, kind, assembly_name, namespace, full_name, name, tokens, summary)
VALUES($et, $eid, $kind, $aname, $ns, $full, $name, $tokens, $sum);";
        var pFEt = ftsCmd.Parameters.Add("$et", SqliteType.Text);
        var pFEid = ftsCmd.Parameters.Add("$eid", SqliteType.Integer);
        var pFKind = ftsCmd.Parameters.Add("$kind", SqliteType.Text);
        var pFAname = ftsCmd.Parameters.Add("$aname", SqliteType.Text);
        var pFNs = ftsCmd.Parameters.Add("$ns", SqliteType.Text);
        var pFFull = ftsCmd.Parameters.Add("$full", SqliteType.Text);
        var pFName = ftsCmd.Parameters.Add("$name", SqliteType.Text);
        var pFTokens = ftsCmd.Parameters.Add("$tokens", SqliteType.Text);
        var pFSum = ftsCmd.Parameters.Add("$sum", SqliteType.Text);

        foreach (var asm in assemblies)
        {
            if (asm == null) continue;

            pAsmName.Value = asm.Name;
            pAsmPath.Value = asm.Path;
            pAsmVer.Value = asm.Version;
            pAsmXml.Value = (object?)asm.XmlPath ?? DBNull.Value;
            pAsmSize.Value = asm.FileSize;
            pAsmMtime.Value = asm.MtimeUtc;
            pAsmTCount.Value = asm.Types.Count;
            var asmId = (long)asmCmd.ExecuteScalar()!;

            foreach (var t in asm.Types)
            {
                pTAid.Value = asmId;
                pTFull.Value = t.FullName;
                pTClr.Value = t.ClrName;
                pTNs.Value = t.Namespace;
                pTName.Value = t.Name;
                pTSimple.Value = t.SimpleName;
                pTKind.Value = t.Kind;
                pTAcc.Value = t.Accessibility;
                pTMod.Value = t.Modifiers;
                pTBase.Value = (object?)t.BaseType ?? DBNull.Value;
                pTBaseS.Value = (object?)t.BaseSimple ?? DBNull.Value;
                pTIfaces.Value = t.Interfaces;
                pTGp.Value = t.GenericParams;
                pTExt.Value = t.IsExtensionContainer ? 1 : 0;
                pTSum.Value = (object?)t.Summary ?? DBNull.Value;
                pTRem.Value = (object?)t.Remarks ?? DBNull.Value;
                pTDocId.Value = t.DocId;
                var typeId = (long)typeCmd.ExecuteScalar()!;

                pFEt.Value = "type";
                pFEid.Value = typeId;
                pFKind.Value = t.Kind;
                pFAname.Value = asm.Name;
                pFNs.Value = t.Namespace;
                pFFull.Value = t.FullName;
                pFName.Value = t.SimpleName;
                pFTokens.Value = Tokenizer.BuildSearchTokens(t.SimpleName, t.FullName, t.BaseSimple, t.Interfaces);
                pFSum.Value = t.Summary ?? string.Empty;
                ftsCmd.ExecuteNonQuery();

                foreach (var m in t.Members)
                {
                    pMTid.Value = typeId;
                    pMAid.Value = asmId;
                    pMKind.Value = m.Kind;
                    pMName.Value = m.Name;
                    pMAcc.Value = m.Accessibility;
                    pMMod.Value = m.Modifiers;
                    pMRet.Value = m.ReturnType;
                    pMRetS.Value = m.ReturnSimple;
                    pMSig.Value = m.Signature;
                    pMPtypes.Value = m.ParamTypes;
                    pMExtT.Value = (object?)m.ExtTargetType ?? DBNull.Value;
                    pMExtS.Value = (object?)m.ExtTargetSimple ?? DBNull.Value;
                    pMEval.Value = (object?)m.EnumValue ?? DBNull.Value;
                    pMSum.Value = (object?)m.Summary ?? DBNull.Value;
                    pMPdoc.Value = (object?)m.ParamsDoc ?? DBNull.Value;
                    pMRdoc.Value = (object?)m.ReturnsDoc ?? DBNull.Value;
                    pMDocId.Value = m.DocId;
                    var memId = (long)memCmd.ExecuteScalar()!;

                    // Skip indexing trivial parameterless constructors in FTS5 (already covered by the type entry)
                    if (m.Kind == "ctor" && string.IsNullOrEmpty(m.ParamTypes))
                        continue;

                    pFEt.Value = "member";
                    pFEid.Value = memId;
                    pFKind.Value = m.Kind;
                    pFAname.Value = asm.Name;
                    pFNs.Value = t.Namespace;
                    pFFull.Value = $"{t.FullName}.{m.Name}";
                    pFName.Value = m.Name;
                    pFTokens.Value = Tokenizer.BuildSearchTokens(m.Name, t.SimpleName, m.ReturnSimple, m.ParamTypes);
                    pFSum.Value = m.Summary ?? string.Empty;
                    ftsCmd.ExecuteNonQuery();
                }
            }
        }
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
    bm25(fts_entries, 2.0, 6.0, 1.5, 0.8) AS rank_score,
    CASE
        WHEN LOWER(f.name) = LOWER($exact) AND f.entity_type = 'type' THEN 0
        WHEN LOWER(f.full_name) = LOWER($exact) THEN 1
        WHEN LOWER(f.name) = LOWER($exact) THEN 2
        WHEN LOWER(f.name) LIKE LOWER($prefix) AND f.entity_type = 'type' THEN 3
        WHEN LOWER(f.name) LIKE LOWER($prefix) THEN 4
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

        if (!string.IsNullOrWhiteSpace(namespacePrefix))
            sql += " AND LOWER(f.namespace) LIKE LOWER($nsPrefix)";

        if (!string.IsNullOrWhiteSpace(assemblyFilter))
            sql += " AND LOWER(f.assembly_name) LIKE LOWER($asmFilter)";

        sql += " ORDER BY priority ASC, rank_score ASC LIMIT $limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$fts", ftsQuery);
        cmd.Parameters.AddWithValue("$exact", query.Trim());
        cmd.Parameters.AddWithValue("$prefix", query.Trim() + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        if (!string.IsNullOrWhiteSpace(kindFilter))
            cmd.Parameters.AddWithValue("$kind", kindFilter.Trim());
        if (!string.IsNullOrWhiteSpace(namespacePrefix))
            cmd.Parameters.AddWithValue("$nsPrefix", namespacePrefix.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(assemblyFilter))
            cmd.Parameters.AddWithValue("$asmFilter", "%" + assemblyFilter.Trim() + "%");

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

        // Hydrate assembly paths and full signatures for returned hits
        var hits = new List<SearchHit>(rawHits.Count);
        foreach (var h in rawHits)
        {
            if (h.et == "type")
            {
                var tr = GetTypeById(h.eid);
                if (tr == null) continue;
                var decl = FormatTypeHeader(tr);
                hits.Add(new SearchHit("type", h.eid, tr.Kind, tr.AssemblyName, tr.AssemblyPath,
                    tr.Namespace, tr.FullName, tr.Name, decl, tr.Summary, h.score));
            }
            else
            {
                var mr = GetMemberById(h.eid);
                if (mr == null) continue;
                var asmPath = GetAssemblyPathForType(mr.TypeId);
                hits.Add(new SearchHit("member", h.eid, mr.Kind, mr.AssemblyName, asmPath,
                    h.ns, $"{mr.DeclaringFullName}.{mr.Name}", mr.Name, mr.Signature, mr.Summary, h.score));
            }
        }

        return hits;
    }

    public IReadOnlyList<TypeRow> ResolveType(string query, string? namespacePrefix = null, string? assemblyFilter = null)
    {
        var q = query.Trim();
        var qNoGeneric = MetadataExtractor.ExtractSimpleTypeName(q);

        // 1. Exact full_name or clr_name match
        var exact = QueryTypes(@"
SELECT t.id, a.name, a.path, a.xml_path, t.full_name, t.clr_name, t.namespace, t.name, t.simple_name,
       t.kind, t.accessibility, t.modifiers, t.base_type, t.base_simple, t.interfaces, t.generic_params,
       t.summary, t.remarks
FROM types t
JOIN assemblies a ON a.id = t.assembly_id
WHERE (LOWER(t.full_name) = LOWER($q) OR LOWER(t.clr_name) = LOWER($q)
       OR LOWER(REPLACE(t.clr_name, '+', '.')) = LOWER($q))",
            q, namespacePrefix, assemblyFilter);
        if (exact.Count > 0) return exact;

        // 2. Exact simple_name match
        var bySimple = QueryTypes(@"
SELECT t.id, a.name, a.path, a.xml_path, t.full_name, t.clr_name, t.namespace, t.name, t.simple_name,
       t.kind, t.accessibility, t.modifiers, t.base_type, t.base_simple, t.interfaces, t.generic_params,
       t.summary, t.remarks
FROM types t
JOIN assemblies a ON a.id = t.assembly_id
WHERE (LOWER(t.simple_name) = LOWER($q) OR LOWER(t.name) = LOWER($q))",
            qNoGeneric, namespacePrefix, assemblyFilter);
        if (bySimple.Count > 0) return bySimple;

        // 3. Suffix match (e.g. "PnP3dObjects.Equipment" or "Outer.Inner")
        var suffix = QueryTypes(@"
SELECT t.id, a.name, a.path, a.xml_path, t.full_name, t.clr_name, t.namespace, t.name, t.simple_name,
       t.kind, t.accessibility, t.modifiers, t.base_type, t.base_simple, t.interfaces, t.generic_params,
       t.summary, t.remarks
FROM types t
JOIN assemblies a ON a.id = t.assembly_id
WHERE LOWER(t.full_name) LIKE LOWER($suffix)",
            "%." + q, namespacePrefix, assemblyFilter, paramName: "$suffix");
        return suffix;
    }

    private List<TypeRow> QueryTypes(string baseSql, string val, string? nsPrefix, string? asmFilter, string paramName = "$q")
    {
        var sql = baseSql;
        if (!string.IsNullOrWhiteSpace(nsPrefix))
            sql += " AND LOWER(t.namespace) LIKE LOWER($nsPrefix)";
        if (!string.IsNullOrWhiteSpace(asmFilter))
            sql += " AND LOWER(a.name) LIKE LOWER($asmFilter)";
        sql += " ORDER BY t.full_name ASC, a.name ASC LIMIT 50";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(paramName, val);
        if (!string.IsNullOrWhiteSpace(nsPrefix))
            cmd.Parameters.AddWithValue("$nsPrefix", nsPrefix.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(asmFilter))
            cmd.Parameters.AddWithValue("$asmFilter", "%" + asmFilter.Trim() + "%");

        var list = new List<TypeRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadTypeRow(r));
        return list;
    }

    public IReadOnlyList<MemberRow> GetMembers(long typeId, string? memberFilter = null)
    {
        var sql = @"
SELECT m.id, m.type_id, t.full_name, t.simple_name, a.name, m.kind, m.name, m.accessibility,
       m.modifiers, m.return_type, m.signature, m.ext_target_type, m.enum_value, m.summary, m.params_doc, m.returns_doc
FROM members m
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id
WHERE m.type_id = $tid";

        if (!string.IsNullOrWhiteSpace(memberFilter))
            sql += " AND LOWER(m.name) LIKE LOWER($mf)";

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
            cmd.Parameters.AddWithValue("$mf", "%" + memberFilter.Trim() + "%");

        var list = new List<MemberRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMemberRow(r));
        return list;
    }

    public List<TypeRow> GetBaseTypeChain(TypeRow target, int maxDepth = 16)
    {
        var chain = new List<TypeRow>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.FullName };
        var currentBase = target.BaseType;

        for (var i = 0; i < maxDepth && !string.IsNullOrEmpty(currentBase); i++)
        {
            if (currentBase is "System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate")
                break;

            var bareBase = StripGenerics(currentBase);
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
        var targetFulls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StripGenerics(target.FullName) };
        var targetSimples = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.SimpleName };

        void CollectIfaces(string ifacesCsv)
        {
            if (string.IsNullOrWhiteSpace(ifacesCsv)) return;
            foreach (var raw in ifacesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var simple = MetadataExtractor.ExtractSimpleTypeName(raw);
                if (!string.IsNullOrEmpty(simple) && simple is not ("IDisposable" or "IComparable" or "IEquatable" or "ICloneable"))
                    targetSimples.Add(simple);
            }
        }

        CollectIfaces(target.Interfaces);
        foreach (var b in baseChain)
        {
            targetFulls.Add(StripGenerics(b.FullName));
            targetSimples.Add(b.SimpleName);
            CollectIfaces(b.Interfaces);
        }

        var simpleParams = targetSimples.Select((_, i) => $"$s{i}").ToList();
        var sql = $@"
SELECT m.id, m.type_id, t.full_name, t.simple_name, a.name, m.kind, m.name, m.accessibility,
       m.modifiers, m.return_type, m.signature, m.ext_target_type, m.enum_value, m.summary, m.params_doc, m.returns_doc
FROM members m
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id
WHERE m.ext_target_simple IN ({string.Join(", ", simpleParams)})";

        if (!string.IsNullOrWhiteSpace(memberFilter))
            sql += " AND LOWER(m.name) LIKE LOWER($mf)";

        sql += " ORDER BY t.full_name ASC, m.name ASC LIMIT 200";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var idx = 0;
        foreach (var s in targetSimples)
            cmd.Parameters.AddWithValue($"$s{idx++}", s);
        if (!string.IsNullOrWhiteSpace(memberFilter))
            cmd.Parameters.AddWithValue("$mf", "%" + memberFilter.Trim() + "%");

        var list = new List<MemberRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = ReadMemberRow(r);
            // Verify namespace matches when ext_target_type has a namespace and matches one of our known full names,
            // or allow interface simple-name matches.
            if (row.ExtTargetType != null && row.ExtTargetType.Contains('.'))
            {
                var extBare = StripGenerics(row.ExtTargetType);
                var extSimple = MetadataExtractor.ExtractSimpleTypeName(extBare);
                if (targetFulls.Contains(extBare) ||
                    (!targetFulls.Any(f => MetadataExtractor.ExtractSimpleTypeName(f).Equals(extSimple, StringComparison.OrdinalIgnoreCase))))
                {
                    list.Add(row);
                }
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
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.FullName };

        for (var i = 0; i < 16 && !string.IsNullOrEmpty(currentBase); i++)
        {
            ancestors.Add(currentBase);
            var bare = StripGenerics(currentBase);
            if (!visited.Add(bare)) break;
            var matches = ResolveType(bare);
            if (matches.Count == 0) break;
            currentBase = matches[0].BaseType;
        }

        // Find direct & indirect derived types (BFS)
        var derived = new List<TypeRow>();
        var seenDerived = new HashSet<long> { target.Id };
        var queue = new Queue<(string full, string simple)>();
        queue.Enqueue((StripGenerics(target.FullName), target.SimpleName));

        while (queue.Count > 0 && derived.Count < limit)
        {
            var (curFull, curSimple) = queue.Dequeue();
            var sql = target.Kind == "interface"
                ? @"
SELECT t.id, a.name, a.path, a.xml_path, t.full_name, t.clr_name, t.namespace, t.name, t.simple_name,
       t.kind, t.accessibility, t.modifiers, t.base_type, t.base_simple, t.interfaces, t.generic_params,
       t.summary, t.remarks
FROM types t
JOIN assemblies a ON a.id = t.assembly_id
WHERE (t.interfaces LIKE $iLike OR LOWER(t.base_type) = LOWER($full))
ORDER BY t.full_name ASC LIMIT $rem"
                : @"
SELECT t.id, a.name, a.path, a.xml_path, t.full_name, t.clr_name, t.namespace, t.name, t.simple_name,
       t.kind, t.accessibility, t.modifiers, t.base_type, t.base_simple, t.interfaces, t.generic_params,
       t.summary, t.remarks
FROM types t
JOIN assemblies a ON a.id = t.assembly_id
WHERE (LOWER(t.base_type) = LOWER($full) OR (LOWER(t.base_simple) = LOWER($simple) AND t.base_type LIKE $fullPrefix))
ORDER BY t.full_name ASC LIMIT $rem";

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$full", curFull);
            cmd.Parameters.AddWithValue("$simple", curSimple);
            cmd.Parameters.AddWithValue("$fullPrefix", curFull + "%");
            cmd.Parameters.AddWithValue("$iLike", "%" + curSimple + "%");
            cmd.Parameters.AddWithValue("$rem", limit - derived.Count);

            using var r = cmd.ExecuteReader();
            while (r.Read() && derived.Count < limit)
            {
                var row = ReadTypeRow(r);
                if (seenDerived.Add(row.Id))
                {
                    derived.Add(row);
                    queue.Enqueue((StripGenerics(row.FullName), row.SimpleName));
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
        var roleClause = role.ToLowerInvariant() switch
        {
            "return" => "LOWER(m.return_simple) = LOWER($simple)",
            "param" => "('|' || LOWER(m.param_types) || '|') LIKE ('%|' || LOWER($simple) || '|%')",
            _ => "(LOWER(m.return_simple) = LOWER($simple) OR ('|' || LOWER(m.param_types) || '|') LIKE ('%|' || LOWER($simple) || '|%'))",
        };

        var sql = $@"
SELECT m.id, m.type_id, t.full_name, t.simple_name, a.name, m.kind, m.name, m.accessibility,
       m.modifiers, m.return_type, m.signature, m.ext_target_type, m.enum_value, m.summary, m.params_doc, m.returns_doc
FROM members m
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id
WHERE {roleClause}";

        if (!string.IsNullOrWhiteSpace(namespacePrefix))
            sql += " AND LOWER(t.namespace) LIKE LOWER($nsPrefix)";
        if (!string.IsNullOrWhiteSpace(assemblyFilter))
            sql += " AND LOWER(a.name) LIKE LOWER($asmFilter)";

        sql += @"
ORDER BY CASE WHEN LOWER(m.return_simple) = LOWER($simple) THEN 0 ELSE 1 END,
         t.full_name ASC, m.name ASC
LIMIT $limit";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$simple", simple);
        cmd.Parameters.AddWithValue("$limit", limit);
        if (!string.IsNullOrWhiteSpace(namespacePrefix))
            cmd.Parameters.AddWithValue("$nsPrefix", namespacePrefix.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(assemblyFilter))
            cmd.Parameters.AddWithValue("$asmFilter", "%" + assemblyFilter.Trim() + "%");

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

    public static string FormatTypeHeader(TypeRow t)
    {
        var parts = new List<string> { t.Accessibility };
        if (!string.IsNullOrEmpty(t.Modifiers)) parts.Add(t.Modifiers);
        parts.Add(t.Kind);
        parts.Add(t.Name);

        var bases = new List<string>();
        if (!string.IsNullOrEmpty(t.BaseSimple) &&
            t.BaseType is not ("System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate"))
            bases.Add(t.BaseSimple);
        if (!string.IsNullOrEmpty(t.Interfaces))
            bases.Add(t.Interfaces);

        return bases.Count > 0
            ? $"{string.Join(" ", parts)} : {string.Join(", ", bases)}"
            : string.Join(" ", parts);
    }

    private TypeRow? GetTypeById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT t.id, a.name, a.path, a.xml_path, t.full_name, t.clr_name, t.namespace, t.name, t.simple_name,
       t.kind, t.accessibility, t.modifiers, t.base_type, t.base_simple, t.interfaces, t.generic_params,
       t.summary, t.remarks
FROM types t
JOIN assemblies a ON a.id = t.assembly_id
WHERE t.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadTypeRow(r) : null;
    }

    private MemberRow? GetMemberById(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT m.id, m.type_id, t.full_name, t.simple_name, a.name, m.kind, m.name, m.accessibility,
       m.modifiers, m.return_type, m.signature, m.ext_target_type, m.enum_value, m.summary, m.params_doc, m.returns_doc
FROM members m
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id
WHERE m.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMemberRow(r) : null;
    }

    private string GetAssemblyPathForType(long typeId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT a.path FROM types t JOIN assemblies a ON a.id = t.assembly_id WHERE t.id = $id";
        cmd.Parameters.AddWithValue("$id", typeId);
        return cmd.ExecuteScalar() as string ?? string.Empty;
    }

    private static TypeRow ReadTypeRow(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        AssemblyName: r.GetString(1),
        AssemblyPath: r.GetString(2),
        XmlPath: r.IsDBNull(3) ? null : r.GetString(3),
        FullName: r.GetString(4),
        ClrName: r.GetString(5),
        Namespace: r.GetString(6),
        Name: r.GetString(7),
        SimpleName: r.GetString(8),
        Kind: r.GetString(9),
        Accessibility: r.GetString(10),
        Modifiers: r.GetString(11),
        BaseType: r.IsDBNull(12) ? null : r.GetString(12),
        BaseSimple: r.IsDBNull(13) ? null : r.GetString(13),
        Interfaces: r.GetString(14),
        GenericParams: r.GetString(15),
        Summary: r.IsDBNull(16) ? null : r.GetString(16),
        Remarks: r.IsDBNull(17) ? null : r.GetString(17));

    private static MemberRow ReadMemberRow(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        TypeId: r.GetInt64(1),
        DeclaringFullName: r.GetString(2),
        DeclaringSimpleName: r.GetString(3),
        AssemblyName: r.GetString(4),
        Kind: r.GetString(5),
        Name: r.GetString(6),
        Accessibility: r.GetString(7),
        Modifiers: r.GetString(8),
        ReturnType: r.GetString(9),
        Signature: r.GetString(10),
        ExtTargetType: r.IsDBNull(11) ? null : r.GetString(11),
        EnumValue: r.IsDBNull(12) ? null : r.GetString(12),
        Summary: r.IsDBNull(13) ? null : r.GetString(13),
        ParamsDoc: r.IsDBNull(14) ? null : r.GetString(14),
        ReturnsDoc: r.IsDBNull(15) ? null : r.GetString(15));

    private IndexStats ReadStats(string? indexName, bool rebuilt, long elapsedMs)
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

    private long ScalarLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string StripGenerics(string fullName)
    {
        var lt = fullName.IndexOf('<');
        return lt >= 0 ? fullName[..lt] : fullName;
    }

    private static string SanitizeName(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray();
        return new string(chars);
    }

    public void Dispose() => _conn.Dispose();
}
