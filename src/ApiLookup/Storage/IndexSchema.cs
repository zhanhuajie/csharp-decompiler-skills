namespace ApiLookup;

/// <summary>
/// Single source of truth for the SQLite index layout. Bump <see cref="Version"/> whenever tables, columns,
/// or stored value formats change; existing databases with a different version are rebuilt automatically.
/// </summary>
internal static class IndexSchema
{
    public const string Version = "5";

    /// <summary>FTS rowid of a type entry (even numbers), so it can be updated in place after the bulk insert.</summary>
    public static long FtsRowIdForType(long typeId) => typeId * 2;

    /// <summary>FTS rowid of a member entry (odd numbers).</summary>
    public static long FtsRowIdForMember(long memberId) => memberId * 2 + 1;

    /// <summary>Tables created before bulk insert (secondary indexes are added afterwards for speed).</summary>
    public const string CreateTablesSql = @"
CREATE TABLE meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE assemblies (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL COLLATE NOCASE,
    path TEXT NOT NULL,
    version TEXT NOT NULL,
    xml_path TEXT,
    file_size INTEGER NOT NULL,
    mtime_utc INTEGER NOT NULL,
    type_count INTEGER NOT NULL
);

CREATE TABLE types (
    id INTEGER PRIMARY KEY,
    assembly_id INTEGER NOT NULL,
    full_name TEXT NOT NULL COLLATE NOCASE,
    bare_full_name TEXT NOT NULL COLLATE NOCASE,
    clr_name TEXT NOT NULL COLLATE NOCASE,
    namespace TEXT NOT NULL COLLATE NOCASE,
    name TEXT NOT NULL COLLATE NOCASE,
    simple_name TEXT NOT NULL COLLATE NOCASE,
    kind TEXT NOT NULL COLLATE NOCASE,
    accessibility TEXT NOT NULL,
    modifiers TEXT NOT NULL,
    base_type TEXT COLLATE NOCASE,
    base_bare TEXT COLLATE NOCASE,
    base_simple TEXT COLLATE NOCASE,
    base_clr TEXT COLLATE NOCASE,
    interfaces TEXT NOT NULL,
    generic_params TEXT NOT NULL,
    generic_constraints TEXT NOT NULL,
    delegate_signature TEXT,
    is_extension_container INTEGER NOT NULL,
    summary TEXT,
    remarks TEXT,
    doc_id TEXT NOT NULL,
    inheritdoc TEXT
);

CREATE TABLE type_interfaces (
    type_id INTEGER NOT NULL,
    iface_full TEXT NOT NULL COLLATE NOCASE,
    iface_simple TEXT NOT NULL COLLATE NOCASE,
    iface_clr TEXT NOT NULL COLLATE NOCASE
);

CREATE TABLE members (
    id INTEGER PRIMARY KEY,
    type_id INTEGER NOT NULL,
    assembly_id INTEGER NOT NULL,
    kind TEXT NOT NULL COLLATE NOCASE,
    name TEXT NOT NULL COLLATE NOCASE,
    accessibility TEXT NOT NULL,
    modifiers TEXT NOT NULL,
    return_type TEXT NOT NULL,
    return_simple TEXT NOT NULL COLLATE NOCASE,
    signature TEXT NOT NULL,
    param_types TEXT NOT NULL,
    ext_target_type TEXT COLLATE NOCASE,
    ext_target_simple TEXT COLLATE NOCASE,
    enum_value TEXT,
    summary TEXT,
    params_doc TEXT,
    returns_doc TEXT,
    doc_id TEXT NOT NULL,
    inheritdoc TEXT
);

CREATE TABLE extension_targets (
    member_id INTEGER NOT NULL,
    target_full TEXT NOT NULL COLLATE NOCASE,
    target_simple TEXT NOT NULL COLLATE NOCASE
);

CREATE TABLE member_type_refs (
    member_id INTEGER NOT NULL,
    type_full TEXT NOT NULL COLLATE NOCASE,
    type_simple TEXT NOT NULL COLLATE NOCASE,
    role TEXT NOT NULL
);

-- 5 UNINDEXED filter columns followed by 4 searchable columns; bm25() weights in IndexStore.Search follow this order.
-- rowid encodes the entity: types use id*2, members use id*2+1 (see IndexSchema.FtsRowId).
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
";

    /// <summary>Secondary indexes created after bulk insert.</summary>
    public const string CreateIndexesSql = @"
CREATE INDEX idx_types_full_name ON types(full_name);
CREATE INDEX idx_types_bare_full_name ON types(bare_full_name);
CREATE INDEX idx_types_clr_name ON types(clr_name);
CREATE INDEX idx_types_simple_name ON types(simple_name);
CREATE INDEX idx_types_name ON types(name);
CREATE INDEX idx_types_base_type ON types(base_type);
CREATE INDEX idx_types_base_bare ON types(base_bare);
CREATE INDEX idx_types_base_simple ON types(base_simple);
CREATE INDEX idx_types_base_clr ON types(base_clr);
CREATE INDEX idx_types_namespace ON types(namespace);
CREATE INDEX idx_types_assembly_id ON types(assembly_id);

CREATE INDEX idx_type_interfaces_type_id ON type_interfaces(type_id);
CREATE INDEX idx_type_interfaces_full ON type_interfaces(iface_full);
CREATE INDEX idx_type_interfaces_simple ON type_interfaces(iface_simple);
CREATE INDEX idx_type_interfaces_clr ON type_interfaces(iface_clr);

CREATE INDEX idx_members_type_id ON members(type_id);
CREATE INDEX idx_members_name ON members(name);
CREATE INDEX idx_members_ext_target ON members(ext_target_type);
CREATE INDEX idx_members_ext_simple ON members(ext_target_simple);

CREATE INDEX idx_extension_targets_lookup ON extension_targets(target_simple, target_full, member_id);
CREATE INDEX idx_extension_targets_member ON extension_targets(member_id);

CREATE INDEX idx_member_type_refs_lookup ON member_type_refs(type_simple, role, member_id);
CREATE INDEX idx_member_type_refs_full ON member_type_refs(type_full, role, member_id);
";
}
