using System.Text.Json.Serialization;

namespace ApiLookup;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IndexStats))]
[JsonSerializable(typeof(IReadOnlyList<IndexInfo>))]
[JsonSerializable(typeof(IReadOnlyList<SearchHit>))]
[JsonSerializable(typeof(SearchJsonResult))]
[JsonSerializable(typeof(AmbiguousTypesResult))]
[JsonSerializable(typeof(TypeDetailJson))]
[JsonSerializable(typeof(HierarchyResult))]
[JsonSerializable(typeof(IReadOnlyList<MemberRow>))]
[JsonSerializable(typeof(IReadOnlyList<AssemblyRow>))]
[JsonSerializable(typeof(DropIndexResult))]
[JsonSerializable(typeof(PruneResult))]
[JsonSerializable(typeof(LocateJsonResult))]
[JsonSerializable(typeof(ErrorJsonResult))]
public partial class ApiLookupJsonContext : JsonSerializerContext
{
}
