using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("baseType")] string BaseType,
    [property: JsonPropertyName("members")] IReadOnlyList<MemberRow> Members);

public sealed record TypeDetailJson(
    [property: JsonPropertyName("type")] TypeRow Type,
    [property: JsonPropertyName("members")] IReadOnlyList<MemberRow> Members,
    [property: JsonPropertyName("inherited")] IReadOnlyList<InheritedGroupJson> Inherited,
    [property: JsonPropertyName("extensionMethods")] IReadOnlyList<MemberRow> ExtensionMethods);

public sealed record SearchJsonResult(
    [property: JsonPropertyName("assemblies")] IReadOnlyList<AssemblyRow> Assemblies,
    [property: JsonPropertyName("hits")] IReadOnlyList<SearchHit> Hits);

public sealed record AmbiguousTypesResult(
    [property: JsonPropertyName("ambiguous")] bool Ambiguous,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("candidates")] IReadOnlyList<TypeRow> Candidates);

public sealed record DropIndexResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dropped")] bool Dropped);

public sealed record PruneResult(
    [property: JsonPropertyName("removedCount")] int RemovedCount,
    [property: JsonPropertyName("freedBytes")] long FreedBytes,
    [property: JsonPropertyName("removedFiles")] IReadOnlyList<string> RemovedFiles);

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
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("count")] int Count);

public sealed record LocateJsonResult(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("scannedAssemblies")] int ScannedAssemblies,
    [property: JsonPropertyName("references")] IReadOnlyList<ResolvedRefGroup>? References,
    [property: JsonPropertyName("matches")] IReadOnlyList<LocateHit> Matches);
