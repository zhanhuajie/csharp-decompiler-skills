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
        var (store, s) = IndexStore.EnsureBuilt(dbPath, "test", resolved, "fixture", false, forceRebuild);
        stats = s;
        return store;
    }

    [Fact]
    public void Search_FingerprintCache_ChineseDocs_And_Bm25RankingWork()
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

            // Chinese XML doc search
            var cnHits = store1.Search("管道");
            Assert.Contains(cnHits, h => h.Name is "ChinesePipeProcessor" or "ComputePipeLength");

            var cnMultiHits = store1.Search("三维管道");
            Assert.Contains(cnMultiHits, h => h.Name == "ChinesePipeProcessor");

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
        using var store3 = BuildFixtureStore(out var stats3, forceRebuild: true);
        Assert.True(stats3.Rebuilt);
    }

    [Fact]
    public void NamespaceBoundaryAndLikeUnderscoreEscapingWork()
    {
        using var store = BuildFixtureStore(out _);

        // --ns ApiLookup.Tests.Fixtures must NOT match ApiLookup.Tests.FixturesExtra
        var nsHits = store.Search("ExtraFixtureType", namespacePrefix: "ApiLookup.Tests.Fixtures");
        Assert.Empty(nsHits);

        var extraHits = store.Search("ExtraFixtureType", kindFilter: "type", namespacePrefix: "ApiLookup.Tests.FixturesExtra");
        Assert.Single(extraHits);

        // --member '_' must match literal underscore only (not every member!)
        var factory = store.ResolveType("SamplePipeFactory").Single();
        var underscoreMembers = store.GetMembers(factory.Id, "_");
        Assert.Single(underscoreMembers);
        Assert.Equal("op_Custom_Add", underscoreMembers[0].Name);
    }

    [Fact]
    public void Hierarchy_ExtensionMethods_And_ByType_HandleGenericsInterfacesAndAliases()
    {
        using var store = BuildFixtureStore(out _);

        // Class + base + extension methods
        var pipe = store.ResolveType("SamplePipeSegment").Single();
        var chain = store.GetBaseTypeChain(pipe);
        Assert.Single(chain);
        Assert.Equal("SampleBaseEntity", chain[0].SimpleName);

        var exts = store.GetExtensionMethods(pipe, chain);
        Assert.Contains(exts, e => e.Name == "CreateAutoCADOffScreenDeviceSnapshot");

        // Generic base class chain
        var pipeContainer = store.ResolveType("SamplePipeContainer").Single();
        var containerChain = store.GetBaseTypeChain(pipeContainer);
        Assert.Single(containerChain);
        Assert.Equal("SampleContainer", containerChain[0].SimpleName);

        // Interface hierarchy: ISampleItem -> SampleItemImpl -> DerivedSampleItemImpl, excluding ISampleItemSource
        var iSampleItem = store.ResolveType("ISampleItem", namespacePrefix: "ApiLookup.Tests.Fixtures").Single();
        var ifaceHierarchy = store.GetHierarchy(iSampleItem);
        Assert.Contains(ifaceHierarchy.DerivedTypes, d => d.SimpleName == "SampleItemImpl");
        Assert.Contains(ifaceHierarchy.DerivedTypes, d => d.SimpleName == "DerivedSampleItemImpl");
        Assert.DoesNotContain(ifaceHierarchy.DerivedTypes, d => d.SimpleName == "ISampleItemSource");

        // Extension methods on SampleItemImpl include comma-containing IDictionary<string, SamplePipeSegment> and Fixtures.ISampleItem,
        // and EXCLUDE OtherNs.ISampleItem.GetOtherTag!
        var impl = store.ResolveType("SampleItemImpl").Single();
        var implChain = store.GetBaseTypeChain(impl);
        var implExts = store.GetExtensionMethods(impl, implChain);
        Assert.Contains(implExts, e => e.Name == "GetFixtureTag");
        Assert.Contains(implExts, e => e.Name == "CountPipeEntries");
        Assert.DoesNotContain(implExts, e => e.Name == "GetOtherTag");

        // Reverse lookup by-type finds generic containers, multi-dim arrays, and primitive aliases
        var producers = store.FindByType("SamplePipeSegment", role: "return");
        Assert.Contains(producers, m => m.Name == "CreateDefaultPipe");
        Assert.Contains(producers, m => m.Name == "CreatePipeBatch");
        Assert.Contains(producers, m => m.Name == "CreatePipeAsync");
        Assert.Contains(producers, m => m.Name == "CreatePipeGrid");

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

        var planByType = store.ExplainQueryPlan(
            "SELECT member_id FROM member_type_refs WHERE type_simple = $s AND role = 'return'",
            ("$s", "SamplePipeSegment"));
        Assert.Contains(planByType, p => p.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase) ||
                                         p.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase));

        var planIface = store.ExplainQueryPlan(
            "SELECT type_id FROM type_interfaces WHERE iface_full = $f",
            ("$f", "ApiLookup.Tests.Fixtures.ISampleItem"));
        Assert.DoesNotContain(planIface, p => p.StartsWith("SCAN type_interfaces", StringComparison.OrdinalIgnoreCase));

        var planExt = store.ExplainQueryPlan(
            "SELECT id FROM members WHERE ext_target_simple IN ($s0)",
            ("$s0", "SampleBaseEntity"));
        Assert.DoesNotContain(planExt, p => p.StartsWith("SCAN members", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IndexCatalog_IsolatedCacheListDropAndPruneWork()
    {
        var cacheDir = Path.Combine(_tempDir, "cache");
        Directory.CreateDirectory(cacheDir);
        var resolved = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll });

        var namedPath = IndexCatalog.GetNamedIndexPath("MySdk", cacheDir);
        var (s1, _) = IndexStore.EnsureBuilt(namedPath, "MySdk", resolved, "fixture", false, false);
        s1.Dispose();

        var autoPath = IndexCatalog.GetAutoCachePath(resolved.SourceKey, cacheDir);
        var (s2, _) = IndexStore.EnsureBuilt(autoPath, null, resolved, "fixture", false, false);
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
