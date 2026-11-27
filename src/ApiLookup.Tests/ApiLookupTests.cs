using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ApiLookup;
using Xunit;

namespace ApiLookup.Tests.Fixtures
{
    /// <summary>Base CAD entity for hierarchy and inheritance tests.</summary>
    public abstract class SampleBaseEntity
    {
        /// <summary>Unique hexadecimal handle.</summary>
        public string Handle { get; set; } = string.Empty;

        /// <summary>Moves the entity by an offset.</summary>
        /// <param name="dx">Delta X in millimeters.</param>
        /// <param name="dy">Delta Y in millimeters.</param>
        public virtual void Translate(double dx, double dy = 0.0) { }
    }

    /// <summary>Represents a 3D pipe segment with <see cref="SampleBaseEntity"/> identity.</summary>
    /// <remarks>Used to verify XML doc extraction, generics, and extension lookup.</remarks>
    public sealed class SamplePipeSegment : SampleBaseEntity, IDisposable
    {
        /// <summary>Creates a new pipe segment.</summary>
        public SamplePipeSegment(double outerDiameter, SamplePipeKind kind = SamplePipeKind.Straight)
        {
            OuterDiameter = outerDiameter;
            Kind = kind;
        }

        /// <summary>Outer diameter in mm.</summary>
        public double OuterDiameter { get; }

        /// <summary>Pipe classification.</summary>
        public SamplePipeKind Kind { get; }

        /// <summary>Tries to resolve connected port coordinates.</summary>
        /// <param name="portName">Port name such as S1.</param>
        /// <param name="coords">Output coordinates array.</param>
        /// <param name="tags">Optional filter tags.</param>
        /// <returns>True when the port exists.</returns>
        public bool TryGetPort(string portName, out double[] coords, params string[] tags)
        {
            coords = Array.Empty<double>();
            return false;
        }

        /// <summary>Nested diagnostic info.</summary>
        public struct PortSnapshot
        {
            public string Name;
        }

        public void Dispose() { }
    }

    /// <summary>Classification of sample pipe segments.</summary>
    public enum SamplePipeKind
    {
        /// <summary>Straight run.</summary>
        Straight = 0,
        /// <summary>Bent elbow run.</summary>
        Elbow = 2,
    }

    /// <summary>Extension helpers for <see cref="SampleBaseEntity"/>.</summary>
    public static class SampleEntityExtensions
    {
        /// <summary>Creates an off-screen preview device snapshot for an entity.</summary>
        public static string CreateAutoCADOffScreenDeviceSnapshot(this SampleBaseEntity entity, int width = 800)
            => $"{entity.Handle}:{width}";
    }

    /// <summary>Factory producing pipe segments.</summary>
    public static class SamplePipeFactory
    {
        /// <summary>Builds a default straight pipe segment.</summary>
        public static SamplePipeSegment CreateDefaultPipe(double od) => new(od);
    }
}

namespace ApiLookup.Tests
{
    public class ApiLookupTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _fixtureDll;

        public ApiLookupTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Tokenizer_SplitsCamelCaseAndBuildsSafeFtsQuery()
        {
            var tokens = Tokenizer.BuildSearchTokens("CreateAutoCADOffScreenDevice");
            Assert.Contains("CreateAutoCADOffScreenDevice", tokens);
            Assert.Contains("Off", tokens);
            Assert.Contains("Screen", tokens);
            Assert.Contains("OffScreen", tokens);
            Assert.Contains("AutoCAD", tokens);

            var fts = Tokenizer.BuildFtsQuery("C++ List<T> offscreen device");
            Assert.NotNull(fts);
            Assert.DoesNotContain("+", fts);
            Assert.DoesNotContain("<", fts);
            Assert.Contains("\"offscreen\"*", fts);
            Assert.Contains("\"device\"*", fts);
        }

        [Fact]
        public void Extractor_ReadsTypesMembersEnumsExtensionsAndXmlDocs()
        {
            var asm = MetadataExtractor.Extract(_fixtureDll);
            Assert.NotNull(asm);
            Assert.NotNull(asm!.XmlPath);

            var pipe = asm.Types.Single(t => t.SimpleName == "SamplePipeSegment");
            Assert.Equal("class", pipe.Kind);
            Assert.Equal("sealed", pipe.Modifiers);
            Assert.Equal("ApiLookup.Tests.Fixtures.SampleBaseEntity", pipe.BaseType);
            Assert.Contains("IDisposable", pipe.Interfaces);
            Assert.Contains("3D pipe segment", pipe.Summary);
            Assert.Contains("XML doc extraction", pipe.Remarks);

            var tryGet = pipe.Members.Single(m => m.Name == "TryGetPort");
            Assert.Contains("out double[] coords", tryGet.Signature);
            Assert.Contains("params string[] tags", tryGet.Signature);
            Assert.Contains("connected port coordinates", tryGet.Summary);
            Assert.Contains("portName", tryGet.ParamsDoc);
            Assert.Contains("True when the port exists", tryGet.ReturnsDoc);

            var nested = asm.Types.Single(t => t.SimpleName == "PortSnapshot");
            Assert.Equal("struct", nested.Kind);
            Assert.EndsWith("SamplePipeSegment+PortSnapshot", nested.ClrName);

            var enumType = asm.Types.Single(t => t.SimpleName == "SamplePipeKind");
            Assert.Equal("enum", enumType.Kind);
            var elbow = enumType.Members.Single(m => m.Name == "Elbow");
            Assert.Equal("2", elbow.EnumValue);
            Assert.Equal("Elbow = 2", elbow.Signature);

            var extType = asm.Types.Single(t => t.SimpleName == "SampleEntityExtensions");
            Assert.True(extType.IsExtensionContainer);
            var extMethod = extType.Members.Single(m => m.Name == "CreateAutoCADOffScreenDeviceSnapshot");
            Assert.Contains("this SampleBaseEntity entity", extMethod.Signature);
            Assert.Equal("SampleBaseEntity", extMethod.ExtTargetSimple);
        }

        [Fact]
        public void IndexStore_CachesFingerprintAndSupportsSearchShowHierarchyByType()
        {
            var dbPath = Path.Combine(_tempDir, "fixture.db");
            var resolved = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll });

            // 1. Initial build
            var (store1, stats1) = IndexStore.EnsureBuilt(dbPath, "test", resolved, "fixture", false, forceRebuild: false);
            using (store1)
            {
                Assert.True(stats1.Rebuilt);
                Assert.Equal(1, stats1.AssemblyCount);
                Assert.Equal(1, stats1.XmlDocCount);

                // Search via lowercase merged pair "offscreen" -> matches CreateAutoCADOffScreenDeviceSnapshot!
                var hits = store1.Search("offscreen device");
                Assert.Contains(hits, h => h.Name == "CreateAutoCADOffScreenDeviceSnapshot");

                // Search via XML summary phrase
                var docHits = store1.Search("hexadecimal handle");
                Assert.Contains(docHits, h => h.Name == "Handle");

                // Show + inherited + extensions
                var pipe = store1.ResolveType("SamplePipeSegment").Single();
                var chain = store1.GetBaseTypeChain(pipe);
                Assert.Single(chain);
                Assert.Equal("SampleBaseEntity", chain[0].SimpleName);

                var exts = store1.GetExtensionMethods(pipe, chain);
                Assert.Contains(exts, e => e.Name == "CreateAutoCADOffScreenDeviceSnapshot");

                // Hierarchy: base entity -> derived SamplePipeSegment
                var baseEntity = store1.ResolveType("SampleBaseEntity").Single();
                var hierarchy = store1.GetHierarchy(baseEntity);
                Assert.Contains(hierarchy.DerivedTypes, d => d.SimpleName == "SamplePipeSegment");

                // By-type reverse lookup: return SamplePipeSegment -> finds SamplePipeFactory.CreateDefaultPipe
                var producers = store1.FindByType("SamplePipeSegment", role: "return");
                Assert.Contains(producers, m => m.Name == "CreateDefaultPipe");
            }

            // 2. Second call hits fingerprint cache (Rebuilt == false)
            var (store2, stats2) = IndexStore.EnsureBuilt(dbPath, "test", resolved, "fixture", false, forceRebuild: false);
            using (store2)
            {
                Assert.False(stats2.Rebuilt);
            }
        }

        [Fact]
        public void SourceResolver_GlobIncludeAndExcludeFiltersWork()
        {
            Assert.True(SourceResolver.MatchesGlob("PnP3dObjectsMgd.dll", "PnP*.dll"));
            Assert.True(SourceResolver.MatchesGlob("acdbmgd.dll", "ac*mgd.dll"));
            Assert.False(SourceResolver.MatchesGlob("DevExpress.Data.v22.2.dll", "PnP*.dll"));
            Assert.True(SourceResolver.MatchesGlob("DevExpress.Data.v22.2.dll", "DevExpress.*"));

            var dir = Path.GetDirectoryName(_fixtureDll)!;
            var included = SourceResolver.Resolve(
                Array.Empty<string>(), new[] { dir }, Array.Empty<string>(),
                recursive: false,
                includePatterns: new[] { "ApiLookup.Tests.dll" });
            Assert.Single(included.Assemblies);

            var excluded = SourceResolver.Resolve(
                Array.Empty<string>(), new[] { dir }, Array.Empty<string>(),
                recursive: false,
                includePatterns: new[] { "ApiLookup*.dll" },
                excludePatterns: new[] { "*.Tests.dll" });
            Assert.DoesNotContain(excluded.Assemblies, a => a.Path.EndsWith("ApiLookup.Tests.dll", StringComparison.OrdinalIgnoreCase));
        }
    }
}
