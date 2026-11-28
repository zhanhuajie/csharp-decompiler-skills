using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ApiLookup.Tests;

/// <summary>
/// Accuracy improvements: full nullable metadata decoding, arity-exact base/interface resolution,
/// cross-assembly &lt;inheritdoc/&gt; resolution, and NuGet packageFolders support.
/// </summary>
public class AccuracyTests : IDisposable
{
    private const string Ns = "ApiLookup.Tests.Fixtures";
    private readonly string _tempDir;
    private readonly string _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;

    public AccuracyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-accuracy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private IndexStore BuildFixtureStore()
    {
        var resolved = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll });
        var (store, _) = IndexBuilder.EnsureBuilt(Path.Combine(_tempDir, "fixture.db"), "test", resolved, "fixture", false, false);
        return store;
    }

    // ---- Nullable metadata ------------------------------------------------------------------------------

    [Theory]
    [InlineData("GetNames", "public static List<string?>? GetNames(Dictionary<string, object?> map, string?[] items, string[]? maybeItems)")]
    [InlineData("Build", "public static StringBuilder? Build(StringBuilder? a, StringBuilder? b)")] // context-only, cross-assembly type
    [InlineData("Pair", "public static KeyValuePair<string?, int> Pair(ValueTuple<string?, int> tuple)")] // generic value types
    [InlineData("FirstOrNull", "public static T? FirstOrNull<T>(IEnumerable<T?> items) where T : class")]
    [InlineData("Pick", "public static T Pick<T>(T value, T? fallback) where T : struct")]
    public void Signatures_ApplyNullableAnnotationsAtEveryPosition(string member, string expected)
    {
        var asm = MetadataExtractor.Extract(_fixtureDll)!;
        var type = asm.Types.Single(t => t.SimpleName == "SampleNullability");
        Assert.Equal(expected, type.Members.Single(m => m.Name == member).Signature);
    }

    [Fact]
    public void TypeSigRenderer_FollowsRoslynFlagOrder()
    {
        // Dictionary<string, List<object>>: [dict, string, List, object]
        var obj = new PrimitiveTypeSig(System.Reflection.Metadata.PrimitiveTypeCode.Object);
        var str = new PrimitiveTypeSig(System.Reflection.Metadata.PrimitiveTypeCode.String);
        var list = new GenericInstanceTypeSig(new NamedTypeSig("List", "System.Collections.Generic.List", false), [obj]);
        var dict = new GenericInstanceTypeSig(new NamedTypeSig("Dictionary", "System.Collections.Generic.Dictionary", false), [str, list]);

        Assert.Equal("Dictionary<string, List<object>>", TypeSigRenderer.Render(dict));
        Assert.Equal("Dictionary<string?, List<object?>>?", TypeSigRenderer.Render(dict, NullableFlags.PerPosition(new byte[] { 2, 2, 1, 2 })));
        Assert.Equal("Dictionary<string?, List<object?>?>?", TypeSigRenderer.Render(dict, NullableFlags.Uniform(2)));

        // Generic value types consume a placeholder byte: KeyValuePair<string, object> -> [0, string, object]
        var kvp = new GenericInstanceTypeSig(new NamedTypeSig("KeyValuePair", "System.Collections.Generic.KeyValuePair", true), [str, obj]);
        Assert.Equal("KeyValuePair<string, object?>", TypeSigRenderer.Render(kvp, NullableFlags.PerPosition(new byte[] { 0, 1, 2 })));

        // Non-generic value types consume nothing; arrays consume their own byte before the element's.
        var intSig = new PrimitiveTypeSig(System.Reflection.Metadata.PrimitiveTypeCode.Int32);
        Assert.Equal("int[]?", TypeSigRenderer.Render(new ArrayTypeSig(intSig, 1), NullableFlags.PerPosition(new byte[] { 2 })));
        Assert.Equal("string?[]", TypeSigRenderer.Render(new ArrayTypeSig(str, 1), NullableFlags.PerPosition(new byte[] { 1, 2 })));
    }

    // ---- Arity-exact inheritance ------------------------------------------------------------------------

    [Fact]
    public void BaseAndDerivedResolution_DistinguishesGenericArity()
    {
        using var store = BuildFixtureStore();

        var derivedGeneric = store.ResolveType($"{Ns}.DerivedFromGenericArity").Single();
        Assert.Equal($"{Ns}.SampleArity`1", derivedGeneric.BaseClrName);
        Assert.Equal($"{Ns}.SampleArity`1", store.GetBaseTypeChain(derivedGeneric).Single().ClrName);

        var derivedPlain = store.ResolveType($"{Ns}.DerivedFromArity").Single();
        Assert.Equal($"{Ns}.SampleArity", store.GetBaseTypeChain(derivedPlain).Single().ClrName);

        var genericTwin = store.ResolveType($"{Ns}.SampleArity`1").Single();
        Assert.Equal(new[] { "DerivedFromGenericArity" }, store.GetHierarchy(genericTwin).DerivedTypes.Select(t => t.SimpleName));

        var plainTwin = store.ResolveType($"{Ns}.SampleArity").Single(t => t.ClrName == $"{Ns}.SampleArity");
        Assert.Equal(new[] { "DerivedFromArity" }, store.GetHierarchy(plainTwin).DerivedTypes.Select(t => t.SimpleName));
    }

    // ---- <inheritdoc/> ----------------------------------------------------------------------------------

    [Fact]
    public void Extraction_RecordsInheritDocMarkers()
    {
        var gate = MetadataExtractor.Extract(_fixtureDll)!.Types.Single(t => t.SimpleName == "SampleGateValve");
        Assert.Null(gate.Summary);
        Assert.Equal(string.Empty, gate.InheritDoc);
        Assert.Equal(string.Empty, gate.Members.Single(m => m.Name == "ComputeFlow").InheritDoc);
        Assert.Equal($"M:{Ns}.SampleBaseEntity.Translate(System.Double,System.Double)", gate.Members.Single(m => m.Name == "Shift").InheritDoc);
    }

    [Fact]
    public void InheritDoc_ResolvesFromBaseClassInterfaceAndCref()
    {
        using var store = BuildFixtureStore();
        var gate = store.ResolveType($"{Ns}.SampleGateValve").Single();
        Assert.Equal("Base valve with documented flow computation.", gate.Summary);

        var members = store.GetMembers(gate.Id);
        var flow = members.Single(m => m.Name == "ComputeFlow");
        Assert.Equal("Computes the volumetric flow rate through the valve.", flow.Summary);
        Assert.Equal("Flow in cubic meters per hour.", flow.ReturnsDoc);

        var open = members.Single(m => m.Name == "Open");
        Assert.Equal("Opens the valve to the requested percentage.", open.Summary);
        Assert.Contains("percent: Opening ratio from 0 to 100.", open.ParamsDoc);

        var shift = members.Single(m => m.Name == "Shift");
        Assert.Equal("Moves the entity by an offset.", shift.Summary);
        Assert.Contains("dx: Delta X in millimeters.", shift.ParamsDoc);

        // Inherited text is searchable through the in-place FTS update.
        var hits = OutputFormatter.ToSearchHits(store.Search("volumetric flow"));
        Assert.Contains(hits, h => h.FullName == $"{Ns}.SampleGateValve.ComputeFlow");
    }

    // ---- NuGet packageFolders ---------------------------------------------------------------------------

    [Fact]
    public void ProjectResolution_UsesPackageFoldersFromAssetsFile()
    {
        var customRoot = Path.Combine(_tempDir, "custom-packages");
        var libDir = Path.Combine(customRoot, "custom.pkg", "2.0.0", "lib", "net8.0");
        Directory.CreateDirectory(libDir);
        var pkgDll = Path.Combine(libDir, "Custom.Pkg.dll");
        File.Copy(_fixtureDll, pkgDll);

        var projDir = Path.Combine(_tempDir, "App");
        Directory.CreateDirectory(Path.Combine(projDir, "obj"));
        var csproj = Path.Combine(projDir, "App.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var assets = new
        {
            version = 3,
            targets = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Custom.Pkg/2.0.0"] = new { type = "package", runtime = new Dictionary<string, object> { ["lib/net8.0/Custom.Pkg.dll"] = new { } } },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Custom.Pkg/2.0.0"] = new { type = "package", path = "custom.pkg/2.0.0" },
            },
            packageFolders = new Dictionary<string, object> { [customRoot + Path.DirectorySeparatorChar] = new { } },
        };
        File.WriteAllText(Path.Combine(projDir, "obj", "project.assets.json"), JsonSerializer.Serialize(assets));

        var resolved = SourceResolver.Resolve(new[] { csproj }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Contains(resolved.Assemblies, a => a.Path.Equals(pkgDll, StringComparison.OrdinalIgnoreCase));
    }
}
