using System.Text.Json.Serialization;

namespace ApiLookup;

[JsonSourceGenerationOptions(
    WriteIndented = true,
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
public partial class ApiLookupJsonContext : JsonSerializerContext
{
}
