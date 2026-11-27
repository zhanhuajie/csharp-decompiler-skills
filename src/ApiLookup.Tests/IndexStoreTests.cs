using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ApiLookup.Tests;

public class IndexStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDll;

    public IndexStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private IndexStore BuildFixtureStore(out IndexStats stats, string? dbName = null, bool forceRebuild = false)
    {
        var dbPath = Path.Combine(_tempDir, dbName ?? "fixture.db");
        var resolved = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll });
        var (store, s) = IndexBuilder.EnsureBuilt(dbPath, "test", resolved, "fixture", false, forceRebuild);
        stats = s;
        return store;
    }

    [Fact]
    public void Search_FingerprintCache_ChineseDocs_DotMemberPriority_And_Bm25RankingWork()
    {
        using (var store1 = BuildFixtureStore(out var stats1))
        {
            Assert.True(stats1.Rebuilt);
            Assert.Equal(1, stats1.AssemblyCount);
            Assert.Equal(1, stats1.XmlDocCount);

            var hits = store1.Search("offscreen device");
            Assert.Contains(hits, h => h.Name == "CreateAutoCADOffScreenDeviceSnapshot");

            var docHits = store1.Search("hexadecimal handle");
            Assert.Contains(docHits, h => h.Name == "Handle");

            // Chinese XML doc search (multi-char & trailing single-char)
            var cnHits = store1.Search("管道");
            Assert.Contains(cnHits, h => h.Name is "ChinesePipeProcessor" or "ComputePipeLength");

            var cnMultiHits = store1.Search("三维管道");
            Assert.Contains(cnMultiHits, h => h.Name == "ChinesePipeProcessor");

            var cnTrailingCharHits = store1.Search("器");
            Assert.Contains(cnTrailingCharHits, h => h.Name == "ChinesePipeProcessor");

            // Type.Member qualified search ranks exact member first
            var dotHits = store1.Search("SamplePipeSegment.TryGetPort");
            Assert.NotEmpty(dotHits);
            Assert.Equal("TryGetPort", dotHits[0].Name);

            // bm25 weighting: exact/name match must outrank summary-only match
            var rankHits = store1.Search("rankingtest");
            var nameIdx = rankHits.ToList().FindIndex(h => h.Name == "Rankingtest");
            var summaryIdx = rankHits.ToList().FindIndex(h => h.Name == "SummaryKeywordHolder");
            Assert.True(nameIdx >= 0 && summaryIdx >= 0 && nameIdx < summaryIdx,
                $"Expected Rankingtest ({nameIdx}) to rank ahead of SummaryKeywordHolder ({summaryIdx})");
        }

        // Second build hits fingerprint cache
        using (var store2 = BuildFixtureStore(out var stats2))
        {
            Assert.False(stats2.Rebuilt);
        }

        // Force rebuild overwrites existing DB cleanly once prior reader closes
        using (var store3 = BuildFixtureStore(out var stats3, forceRebuild: true))
        {
            Assert.True(stats3.Rebuilt);
        }
    }

    [Fact]
    public void NamespaceBoundary_NestedPlusResolution_And_LikeUnderscoreEscapingWork()
    {
        using var store = BuildFixtureStore(out _);

        // --ns ApiLookup.Tests.Fixtures must NOT match ApiLookup.Tests.FixturesExtra
        var nsHits = store.Search("ExtraFixtureType", namespacePrefix: "ApiLookup.Tests.Fixtures");
        Assert.Empty(nsHits);

        var extraHits = store.Search("ExtraFixtureType", kindFilter: "type", namespacePrefix: "ApiLookup.Tests.FixturesExtra");
        Assert.Single(extraHits);

        // Multi-level nested `+` resolution
        var deepByPlus = store.ResolveType("MiddleBox+DeepLeaf");
        Assert.Single(deepByPlus);
        Assert.Equal("ApiLookup.Tests.Fixtures.OuterBox.MiddleBox.DeepLeaf", deepByPlus[0].FullName);

        // --member '_' must match literal underscore only (not every member!)
        var factory = store.ResolveType("SamplePipeFactory").Single();
        var underscoreMembers = store.GetMembers(factory.Id, "_");
        Assert.Single(underscoreMembers);
        Assert.Equal("op_Custom_Add", underscoreMembers[0].Name);
    }

    [Fact]
    public void Hierarchy_InterfaceInheritance_Extensions_And_QualifiedByTypeWork()
    {
        using var store = BuildFixtureStore(out _);

        // Class + base + extension methods (including generic constrained extension, excluding array extension!)
        var pipe = store.ResolveType("SamplePipeSegment").Single();
        var chain = store.GetBaseTypeChain(pipe);
        Assert.Single(chain);
        Assert.Equal("SampleBaseEntity", chain[0].SimpleName);

        var exts = store.GetExtensionMethods(pipe, chain);
        Assert.Contains(exts, e => e.Name == "CreateAutoCADOffScreenDeviceSnapshot");
        Assert.Contains(exts, e => e.Name == "UpgradeEntityOpen");
        Assert.Contains(exts, e => e.Name == "UpgradeDisposableEntity");
        Assert.DoesNotContain(exts, e => e.Name == "UpgradeTaggedEntity");
        Assert.DoesNotContain(exts, e => e.Name == "CountSegmentArray");

        // Multi-constraint extension conjunction checks across base, interface, tagged class, struct, and nested generic host
        var baseEntity = store.ResolveType("SampleBaseEntity").Single();
        var baseExts = store.GetExtensionMethods(baseEntity, store.GetBaseTypeChain(baseEntity));
        Assert.Contains(baseExts, e => e.Name == "UpgradeEntityOpen");
        Assert.DoesNotContain(baseExts, e => e.Name == "UpgradeTaggedEntity");
        Assert.DoesNotContain(baseExts, e => e.Name == "UpgradeDisposableEntity");

        var taggedPipe = store.ResolveType("SampleTaggedPipe").Single();
        var taggedPipeExts = store.GetExtensionMethods(taggedPipe, store.GetBaseTypeChain(taggedPipe));
        Assert.Contains(taggedPipeExts, e => e.Name == "UpgradeEntityOpen");
        Assert.Contains(taggedPipeExts, e => e.Name == "UpgradeTaggedEntity");
        Assert.DoesNotContain(taggedPipeExts, e => e.Name == "UpgradeDisposableEntity");
        Assert.DoesNotContain(taggedPipeExts, e => e.Name == "FormatTaggedStruct");

        var taggedStruct = store.ResolveType("SampleTaggedStruct").Single();
        var taggedStructExts = store.GetExtensionMethods(taggedStruct, store.GetBaseTypeChain(taggedStruct));
        Assert.Contains(taggedStructExts, e => e.Name == "FormatTaggedStruct");
        Assert.Contains(taggedStructExts, e => e.Name == "GetFixtureTag");
        Assert.DoesNotContain(taggedStructExts, e => e.Name == "UpgradeTaggedEntity");

        var innerToken = store.ResolveType("InnerToken").Single();
        var innerTokenExts = store.GetExtensionMethods(innerToken, store.GetBaseTypeChain(innerToken));
        Assert.Contains(innerTokenExts, e => e.Name == "DescribeInnerToken");

        // Generic base-class formatting preserves generic arguments in FormatTypeHeader & Search
        var pipeContainer = store.ResolveType("SamplePipeContainer").Single();
        Assert.Equal("public class SamplePipeContainer : SampleContainer<SamplePipeSegment>", OutputFormatter.FormatTypeHeader(pipeContainer));
        var pipeContainerHit = store.Search("SamplePipeContainer", kindFilter: "type").Single(h => h.Name == "SamplePipeContainer");
        Assert.Contains(": SampleContainer<SamplePipeSegment>", pipeContainerHit.Signature);

        var stringList = store.ResolveType("SampleStringList").Single();
        Assert.Equal("public class SampleStringList : List<string>", OutputFormatter.FormatTypeHeader(stringList));
        var stringListHit = store.Search("SampleStringList", kindFilter: "type").Single(h => h.Name == "SampleStringList");
        Assert.Contains(": List<string>", stringListHit.Signature);

        // Interface inheritance chain: IDerivedSampleItem -> ISampleItem
        var derivedIface = store.ResolveType("IDerivedSampleItem").Single();
        var ifaceBaseChain = store.GetBaseTypeChain(derivedIface);
        Assert.Single(ifaceBaseChain);
        Assert.Equal("ApiLookup.Tests.Fixtures.ISampleItem", ifaceBaseChain[0].FullName);

        var derivedIfaceHierarchy = store.GetHierarchy(derivedIface);
        Assert.Contains("ApiLookup.Tests.Fixtures.ISampleItem", derivedIfaceHierarchy.AncestorChain);
        Assert.Contains(derivedIfaceHierarchy.DerivedTypes, d => d.SimpleName == "SampleItemImpl");
        Assert.Contains(derivedIfaceHierarchy.DerivedTypes, d => d.SimpleName == "DerivedSampleItemImpl");

        // Interface hierarchy: ISampleItem -> IDerivedSampleItem -> SampleItemImpl -> DerivedSampleItemImpl
        var iSampleItem = store.ResolveType("ISampleItem", namespacePrefix: "ApiLookup.Tests.Fixtures").Single();
        var ifaceHierarchy = store.GetHierarchy(iSampleItem);
        Assert.Contains(ifaceHierarchy.DerivedTypes, d => d.SimpleName == "IDerivedSampleItem");
        Assert.Contains(ifaceHierarchy.DerivedTypes, d => d.SimpleName == "SampleItemImpl");
        Assert.Contains(ifaceHierarchy.DerivedTypes, d => d.SimpleName == "DerivedSampleItemImpl");
        Assert.DoesNotContain(ifaceHierarchy.DerivedTypes, d => d.SimpleName == "ISampleItemSource");

        // Extension methods on SampleItemImpl include transitive parent interface Fixtures.ISampleItem and exclude OtherNs.ISampleItem
        var impl = store.ResolveType("SampleItemImpl").Single();
        var implChain = store.GetBaseTypeChain(impl);
        var implExts = store.GetExtensionMethods(impl, implChain);
        Assert.Contains(implExts, e => e.Name == "CountPipeEntries");
        Assert.DoesNotContain(implExts, e => e.Name == "GetOtherTag");
        Assert.DoesNotContain(implExts, e => e.Name == "UpgradeTaggedEntity");
        Assert.DoesNotContain(implExts, e => e.Name == "FormatTaggedStruct");

        // Reverse lookup by-type finds generic containers, multi-dim arrays, primitive aliases, AND disambiguates qualified namespaces
        var producers = store.FindByType("SamplePipeSegment", role: "return");
        Assert.Contains(producers, m => m.Name == "CreateDefaultPipe");
        Assert.Contains(producers, m => m.Name == "CreatePipeBatch");
        Assert.Contains(producers, m => m.Name == "CreatePipeAsync");
        Assert.Contains(producers, m => m.Name == "CreatePipeGrid");

        var qualifiedIfaceRefs = store.FindByType("ApiLookup.Tests.Fixtures.ISampleItem");
        Assert.Contains(qualifiedIfaceRefs, m => m.Name == "GetItems");
        Assert.Contains(qualifiedIfaceRefs, m => m.Name == "GetFixtureTag");
        Assert.DoesNotContain(qualifiedIfaceRefs, m => m.Name == "GetOtherTag");

        var stringProducers = store.FindByType("String", role: "return");
        Assert.Contains(stringProducers, m => m.Name == "Describe");

        var intConsumers = store.FindByType("Int32", role: "param");
        Assert.Contains(intConsumers, m => m.Name == "CreatePipeBatch");
    }

    [Fact]
    public void QueryPlans_UseIndexesInsteadOfFullTableScans()
    {
        using var store = BuildFixtureStore(out _);

        var planResolve = store.ExplainQueryPlan(
            "SELECT id FROM types WHERE (full_name = $q OR bare_full_name = $qBare OR clr_name = $q)",
            ("$q", "SamplePipeSegment"), ("$qBare", "SamplePipeSegment"));
        Assert.DoesNotContain(planResolve, p => p.StartsWith("SCAN types", StringComparison.OrdinalIgnoreCase));

        var planByTypeSimple = store.ExplainQueryPlan(
            "SELECT member_id FROM member_type_refs WHERE type_simple = $s AND role = 'return'",
            ("$s", "SamplePipeSegment"));
        Assert.Contains(planByTypeSimple, p => p.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase) ||
                                               p.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase));

        var planByTypeFull = store.ExplainQueryPlan(
            "SELECT member_id FROM member_type_refs WHERE type_full = $f AND role = 'return'",
            ("$f", "ApiLookup.Tests.Fixtures.SamplePipeSegment"));
        Assert.Contains(planByTypeFull, p => p.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase) ||
                                             p.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase));

        var planIface = store.ExplainQueryPlan(
            "SELECT type_id FROM type_interfaces WHERE iface_full = $f",
            ("$f", "ApiLookup.Tests.Fixtures.ISampleItem"));
        Assert.DoesNotContain(planIface, p => p.StartsWith("SCAN type_interfaces", StringComparison.OrdinalIgnoreCase));

        var planExt = store.ExplainQueryPlan(
            "SELECT member_id FROM extension_targets WHERE target_simple IN ($s0)",
            ("$s0", "SampleBaseEntity"));
        Assert.DoesNotContain(planExt, p => p.StartsWith("SCAN extension_targets", StringComparison.OrdinalIgnoreCase));

        var planExtByMember = store.ExplainQueryPlan(
            "SELECT member_id, target_full, target_simple FROM extension_targets WHERE member_id IN ($m0)",
            ("$m0", 1L));
        Assert.DoesNotContain(planExtByMember, p => p.StartsWith("SCAN extension_targets", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IndexCatalog_IsolatedCacheSanitizeListDropAndPruneWork()
    {
        var cacheDir = Path.Combine(_tempDir, "cache");
        Directory.CreateDirectory(cacheDir);
        var resolved = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll });

        // Distinct special-character names must not collide on disk
        Assert.NotEqual(
            IndexCatalog.GetNamedIndexPath("sdk/2025", cacheDir),
            IndexCatalog.GetNamedIndexPath("sdk:2025", cacheDir));

        var namedPath = IndexCatalog.GetNamedIndexPath("MySdk", cacheDir);
        var (s1, _) = IndexBuilder.EnsureBuilt(namedPath, "MySdk", resolved, "fixture", false, false);
        s1.Dispose();

        var autoPath = IndexCatalog.GetAutoCachePath(resolved.SourceKey, cacheDir);
        var (s2, _) = IndexBuilder.EnsureBuilt(autoPath, null, resolved, "fixture", false, false);
        s2.Dispose();

        var listed = IndexCatalog.ListIndexes(cacheDir);
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, x => x.IsNamed && x.Name == "MySdk");
        Assert.Contains(listed, x => !x.IsNamed);

        var pruned = IndexCatalog.PruneAutoIndexes(cacheDir);
        Assert.Equal(1, pruned.RemovedCount);
        Assert.True(File.Exists(namedPath));
        Assert.False(File.Exists(autoPath));

        Assert.True(IndexCatalog.DropNamedIndex("MySdk", cacheDir));
        Assert.False(File.Exists(namedPath));
    }
}
