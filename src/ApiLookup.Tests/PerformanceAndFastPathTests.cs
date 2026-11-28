using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using Xunit;

namespace ApiLookup.Tests;

/// <summary>
/// Phase 4 tests: Discover/Probe split, stat-fingerprint fast path, directory XML caching,
/// single-pass <see cref="TypeSigProjections"/>, and --verbose timing diagnostics.
/// </summary>
[Collection(EnvironmentSensitiveCollection.Name)]
public class PerformanceAndFastPathTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDll;
    private readonly string? _fixtureXml;

    public PerformanceAndFastPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-phase4-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;
        var xml = Path.ChangeExtension(_fixtureDll, ".xml");
        _fixtureXml = File.Exists(xml) ? xml : null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private string CopyFixtureTo(string targetDir, string fileName = "SampleApi.dll", bool includeXml = true)
    {
        Directory.CreateDirectory(targetDir);
        var dllDest = Path.Combine(targetDir, fileName);
        File.Copy(_fixtureDll, dllDest, overwrite: true);
        if (includeXml && _fixtureXml != null)
        {
            var xmlDest = Path.ChangeExtension(dllDest, ".xml");
            File.Copy(_fixtureXml, xmlDest, overwrite: true);
        }
        return dllDest;
    }

    [Fact]
    public void DiscoverAndTryOpenFresh_ReusesIndexWithoutProbing()
    {
        var sdkDir = Path.Combine(_tempDir, "sdk");
        CopyFixtureTo(sdkDir);
        var dbPath = Path.Combine(_tempDir, "fastpath.db");

        var disc1 = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.Single(disc1.Candidates);
        Assert.False(IndexBuilder.TryOpenFresh(dbPath, null, disc1, includeNonPublic: false, out _));

        var resolved = SourceResolver.Probe(disc1);
        var (builtStore, builtStats) = IndexBuilder.EnsureBuilt(dbPath, null, resolved, "dir:" + sdkDir, includeNonPublic: false, forceRebuild: false);
        Assert.True(builtStats.Rebuilt);
        builtStore.Dispose();

        var disc2 = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.True(IndexBuilder.TryOpenFresh(dbPath, null, disc2, includeNonPublic: false, out var fresh));
        using (fresh.Store)
        {
            Assert.False(fresh.Stats.Rebuilt);
            Assert.Equal(builtStats.TypeCount, fresh.Stats.TypeCount);
            Assert.Contains(fresh.Store.ResolveType("SamplePipeSegment"), t => t.SimpleName == "SamplePipeSegment");
        }
    }

    [Fact]
    public void StatFingerprint_InvalidatesWhenDllOrXmlChanges()
    {
        var sdkDir = Path.Combine(_tempDir, "invalidate-sdk");
        var dllPath = CopyFixtureTo(sdkDir, "SampleApi.dll", includeXml: false);
        var dbPath = Path.Combine(_tempDir, "invalidate.db");

        var discInitial = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        var (store, _) = IndexBuilder.EnsureBuilt(dbPath, null, SourceResolver.Probe(discInitial), "dir", false, false);
        store.Dispose();

        // 1. Touching DLL mtime invalidates stat_fingerprint
        File.SetLastWriteTimeUtc(dllPath, File.GetLastWriteTimeUtc(dllPath).AddSeconds(5));
        var discAfterDllTouch = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.NotEqual(discInitial.StatFingerprint, discAfterDllTouch.StatFingerprint);
        Assert.False(IndexBuilder.TryOpenFresh(dbPath, null, discAfterDllTouch, false, out _));

        // 2. Adding a companion XML invalidates stat_fingerprint
        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        File.WriteAllText(xmlPath, "<doc><assembly><name>SampleApi</name></assembly><members /></doc>");
        var discWithXml = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.NotEqual(discAfterDllTouch.StatFingerprint, discWithXml.StatFingerprint);
        Assert.Equal(xmlPath, discWithXml.Candidates.Single().XmlPath);

        var (rebuilt, _) = IndexBuilder.EnsureBuilt(dbPath, null, SourceResolver.Probe(discWithXml), "dir", false, false);
        rebuilt.Dispose();

        // 3. Touching companion XML mtime invalidates stat_fingerprint
        File.SetLastWriteTimeUtc(xmlPath, File.GetLastWriteTimeUtc(xmlPath).AddSeconds(5));
        var discAfterXmlTouch = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.NotEqual(discWithXml.StatFingerprint, discAfterXmlTouch.StatFingerprint);
        Assert.False(IndexBuilder.TryOpenFresh(dbPath, null, discAfterXmlTouch, false, out _));
    }

    [Fact]
    public void NonManagedBinaryChange_FallsBackToContentFingerprint_AndRefreshesStat()
    {
        var sdkDir = Path.Combine(_tempDir, "mixed-sdk");
        CopyFixtureTo(sdkDir, "Managed.dll");
        var native1 = Path.Combine(sdkDir, "native.dll");
        File.WriteAllText(native1, "not a managed PE binary");
        var dbPath = Path.Combine(_tempDir, "mixed.db");

        var disc1 = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.Equal(2, disc1.Candidates.Count);
        var (store1, stats1) = IndexBuilder.EnsureBuilt(dbPath, null, SourceResolver.Probe(disc1), "dir", false, false);
        Assert.True(stats1.Rebuilt);
        Assert.Equal(1, stats1.AssemblyCount);
        store1.Dispose();

        // Modify native.dll and add native2.dll -> stat fingerprint misses, Probe keeps same managed fingerprint,
        // EnsureBuilt reuses DB (Rebuilt == false) AND updates meta.stat_fingerprint for subsequent calls.
        File.WriteAllText(native1, "modified native payload with different length");
        File.SetLastWriteTimeUtc(native1, DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(Path.Combine(sdkDir, "native2.dll"), "second native binary");

        var disc2 = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.Equal(3, disc2.Candidates.Count);
        Assert.NotEqual(disc1.StatFingerprint, disc2.StatFingerprint);
        Assert.False(IndexBuilder.TryOpenFresh(dbPath, null, disc2, false, out _));

        var (store2, stats2) = IndexBuilder.EnsureBuilt(dbPath, null, SourceResolver.Probe(disc2), "dir", false, false);
        Assert.False(stats2.Rebuilt);
        store2.Dispose();

        // Now TryOpenFresh succeeds immediately on the updated directory state.
        var disc3 = SourceResolver.Discover(Array.Empty<string>(), new[] { sdkDir }, Array.Empty<string>());
        Assert.True(IndexBuilder.TryOpenFresh(dbPath, null, disc3, false, out var fresh));
        using (fresh.Store)
        {
            Assert.False(fresh.Stats.Rebuilt);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectoryXmlCache_MatchesFindXmlDocPathPriority(bool recursive)
    {
        var dir = Path.Combine(_tempDir, recursive ? "xml-prio-rec" : "xml-prio-top");
        Directory.CreateDirectory(dir);
        foreach (var sub in new[] { "en", "en-US", "zh-Hans", "zh-CN" })
            Directory.CreateDirectory(Path.Combine(dir, sub));

        static void WriteXml(string path) => File.WriteAllText(path, "<doc><members /></doc>");

        // (a) root + en -> root wins
        var a = CopyFixtureTo(dir, "CaseA.dll", includeXml: false);
        WriteXml(Path.Combine(dir, "CaseA.xml"));
        WriteXml(Path.Combine(dir, "en", "CaseA.xml"));

        // (b) en + en-US -> en wins
        var b = CopyFixtureTo(dir, "CaseB.dll", includeXml: false);
        WriteXml(Path.Combine(dir, "en", "CaseB.xml"));
        WriteXml(Path.Combine(dir, "en-US", "CaseB.xml"));

        // (c) en-US + zh-Hans -> en-US wins
        var c = CopyFixtureTo(dir, "CaseC.dll", includeXml: false);
        WriteXml(Path.Combine(dir, "en-US", "CaseC.xml"));
        WriteXml(Path.Combine(dir, "zh-Hans", "CaseC.xml"));

        // (d) zh-Hans + zh-CN -> zh-Hans wins
        var d = CopyFixtureTo(dir, "CaseD.dll", includeXml: false);
        WriteXml(Path.Combine(dir, "zh-Hans", "CaseD.xml"));
        WriteXml(Path.Combine(dir, "zh-CN", "CaseD.xml"));

        // (e) zh-CN only -> zh-CN wins
        var e = CopyFixtureTo(dir, "CaseE.dll", includeXml: false);
        WriteXml(Path.Combine(dir, "zh-CN", "CaseE.xml"));

        var discovered = SourceResolver.Discover(Array.Empty<string>(), new[] { dir }, Array.Empty<string>(), recursive: recursive);
        Assert.Equal(5, discovered.Candidates.Count);

        foreach (var dll in new[] { a, b, c, d, e })
        {
            var expected = XmlDocReader.FindXmlDocPath(dll);
            var actual = discovered.Candidates.Single(cand => cand.FullPath.Equals(dll, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(expected, actual.XmlPath);
            Assert.True(actual.XmlFileSize >= 0);
        }
    }

    [Fact]
    public void TypeSigProjections_XmlDocAndTypeRefsCoverComplexSignatures()
    {
        var voidSig = new PrimitiveTypeSig(PrimitiveTypeCode.Void);
        Assert.Equal("System.Void", TypeSigProjections.ToXmlDocName(voidSig));
        Assert.Empty(TypeSigProjections.CollectTypeRefs(voidSig));

        var intSig = new PrimitiveTypeSig(PrimitiveTypeCode.Int32);
        Assert.Equal("System.Int32", TypeSigProjections.ToXmlDocName(intSig));
        Assert.Equal(new[] { ("System.Int32", "int") }, TypeSigProjections.CollectTypeRefs(intSig));

        var strSig = new PrimitiveTypeSig(PrimitiveTypeCode.String);
        var listDef = new NamedTypeSig("List", "System.Collections.Generic.List", false, "System.Collections.Generic.List`1");
        var dictDef = new NamedTypeSig("Dictionary", "System.Collections.Generic.Dictionary", false, "System.Collections.Generic.Dictionary`2");
        var listInt = new GenericInstanceTypeSig(listDef, ImmutableArray.Create<TypeSig>(intSig));
        var dict = new GenericInstanceTypeSig(dictDef, ImmutableArray.Create<TypeSig>(strSig, listInt));

        Assert.Equal(
            "System.Collections.Generic.Dictionary{System.String,System.Collections.Generic.List{System.Int32}}",
            TypeSigProjections.ToXmlDocName(dict));

        var dictRefs = TypeSigProjections.CollectTypeRefs(dict);
        Assert.Equal(
            new[]
            {
                ("System.Collections.Generic.Dictionary", "Dictionary"),
                ("System.String", "string"),
                ("System.Collections.Generic.List", "List"),
                ("System.Int32", "int"),
            },
            dictRefs);

        // Nullable<int> unwraps to int without emitting System.Nullable as a type reference
        var nullableDef = new NamedTypeSig("Nullable", "System.Nullable", true, "System.Nullable`1");
        var nullableInt = new GenericInstanceTypeSig(nullableDef, ImmutableArray.Create<TypeSig>(intSig));
        Assert.Equal("System.Nullable{System.Int32}", TypeSigProjections.ToXmlDocName(nullableInt));
        Assert.Equal(new[] { ("System.Int32", "int") }, TypeSigProjections.CollectTypeRefs(nullableInt));

        // Arrays, byref, pointer
        Assert.Equal("System.Int32[]", TypeSigProjections.ToXmlDocName(new ArrayTypeSig(intSig, 1)));
        Assert.Equal("System.Int32[0:,0:]", TypeSigProjections.ToXmlDocName(new ArrayTypeSig(intSig, 2)));
        Assert.Equal("System.Int32@", TypeSigProjections.ToXmlDocName(new ByRefTypeSig(intSig)));
        Assert.Equal("System.Int32*", TypeSigProjections.ToXmlDocName(new PointerTypeSig(intSig)));

        // Generic parameters
        var tParam = new GenericParameterTypeSig("T", false, 0, IsMethodParameter: false);
        var mParam = new GenericParameterTypeSig("TResult", false, 1, IsMethodParameter: true);
        Assert.Equal("`0", TypeSigProjections.ToXmlDocName(tParam));
        Assert.Equal("``1", TypeSigProjections.ToXmlDocName(mParam));
        Assert.Empty(TypeSigProjections.CollectTypeRefs(tParam));
        Assert.Empty(TypeSigProjections.CollectTypeRefs(mParam));

        // Function pointer
        var fnMethod = new MethodSignature<TypeSig>(
            new SignatureHeader(0x09), // Method header, default convention
            returnType: intSig,
            requiredParameterCount: 1,
            genericParameterCount: 0,
            parameterTypes: ImmutableArray.Create<TypeSig>(strSig));
        var fnPtr = new FunctionPointerTypeSig(fnMethod);
        Assert.Equal("System.IntPtr", TypeSigProjections.ToXmlDocName(fnPtr));
        Assert.Equal(
            new[] { ("System.String", "string"), ("System.Int32", "int") },
            TypeSigProjections.CollectTypeRefs(fnPtr));
    }

    [Fact]
    public void Cli_VerbosePrintsTimingDiagnostics()
    {
        var sdkDir = Path.Combine(_tempDir, "timing-sdk");
        CopyFixtureTo(sdkDir);
        var dbPath = Path.Combine(_tempDir, "timing.db");

        var err1 = new StringWriter();
        var rc1 = Program.Run(new[] { "index", "--db", dbPath, "--dir", sdkDir, "--verbose" }, new StringWriter(), err1);
        Assert.Equal(0, rc1);
        var stderr1 = err1.ToString();
        Assert.Contains("[timing] discover", stderr1);
        Assert.Contains("[timing] extract+insert", stderr1);

        var err2 = new StringWriter();
        var rc2 = Program.Run(new[] { "index", "--db", dbPath, "--dir", sdkDir, "--verbose" }, new StringWriter(), err2);
        Assert.Equal(0, rc2);
        var stderr2 = err2.ToString();
        Assert.Contains("[timing] discover", stderr2);
        Assert.Contains("[timing] stat fingerprint matched", stderr2);
    }
}
