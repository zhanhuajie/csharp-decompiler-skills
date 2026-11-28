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
    string Sources,
    bool IsNamed);

public sealed record SearchHit(
    string EntityType,
    long EntityId,
    string Kind,
    string AssemblyName,
    string AssemblyPath,
    string Namespace,
    string FullName,
    string Name,
    string Signature,
    string? Summary,
    double Score);

/// <summary>
/// Raw full-text match returned by <see cref="IndexStore.Search"/>, in rank order. Exactly one of
/// <see cref="Type"/> / <see cref="Member"/> is set; <see cref="OutputFormatter.ToSearchHits"/> turns it into a <see cref="SearchHit"/>.
/// </summary>
public sealed record SearchResult(
    string EntityType,
    long EntityId,
    double Score,
    string Namespace,
    TypeRow? Type,
    MemberRow? Member)
{
    public string Name => Type?.Name ?? Member?.Name ?? string.Empty;
}

public sealed record TypeRow(
    long Id,
    string AssemblyName,
    string AssemblyPath,
    string? XmlPath,
    string FullName,
    string BareFullName,
    string ClrName,
    string Namespace,
    string Name,
    string SimpleName,
    string Kind,
    string Accessibility,
    string Modifiers,
    string? BaseType,
    string? BaseSimple,
    string? BaseClrName,
    string Interfaces,
    string GenericParams,
    string GenericConstraints,
    string? DelegateSignature,
    string? Summary,
    string? Remarks);

public sealed record MemberRow(
    long Id,
    long TypeId,
    string DeclaringFullName,
    string DeclaringSimpleName,
    string AssemblyName,
    string AssemblyPath,
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

public sealed record InheritedGroupJson(
    string BaseType,
    IReadOnlyList<MemberRow> Members);

public sealed record TypeDetailJson(
    TypeRow Type,
    IReadOnlyList<MemberRow> Members,
    IReadOnlyList<InheritedGroupJson> Inherited,
    IReadOnlyList<MemberRow> ExtensionMethods);

public sealed record SearchJsonResult(
    IReadOnlyList<AssemblyRow> Assemblies,
    IReadOnlyList<SearchHit> Hits);

public sealed record AmbiguousTypesResult(
    bool Ambiguous,
    string Query,
    IReadOnlyList<TypeRow> Candidates);

public sealed record DropIndexResult(
    string Name,
    bool Dropped);

public sealed record PruneResult(
    int RemovedCount,
    long FreedBytes,
    IReadOnlyList<string> RemovedFiles,
    int RemovedTempCount = 0);

public sealed record LocateHit(
    string FullName,
    string ClrName,
    string Namespace,
    string SimpleName,
    string Accessibility,
    string AssemblyName,
    string AssemblyPath,
    string Group);

public sealed record ResolvedRefGroup(
    string Group,
    int Count);

public sealed record LocateJsonResult(
    string Query,
    int ScannedAssemblies,
    IReadOnlyList<ResolvedRefGroup>? References,
    IReadOnlyList<LocateHit> Matches);

public sealed record ErrorJsonResult(
    string Error,
    int ExitCode);
