using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ApiLookup.Tests;

public class SourceResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDll;

    public SourceResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-resolver-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void GlobIncludeAndExcludeFiltersWork()
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

    [Fact]
    public void Resolve_IncludeNonPublicProducesDistinctSourceKey()
    {
        var pub = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll }, includeNonPublic: false);
        var all = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll }, includeNonPublic: true);
        Assert.NotEqual(pub.SourceKey, all.SourceKey);
    }

    [Fact]
    public void ResolveFromAssets_DeduplicatesCompileRuntimeExpandsBuildPropsAndCustomAssemblyName()
    {
        var nugetRoot = Path.Combine(_tempDir, "nuget");
        var pkgRoot = Path.Combine(nugetRoot, "sample.pkg", "1.0.0");
        var refDir = Path.Combine(pkgRoot, "ref", "net8.0");
        var libDir = Path.Combine(pkgRoot, "lib", "net8.0");
        var net48Dir = Path.Combine(pkgRoot, "lib", "net48");
        Directory.CreateDirectory(refDir);
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(net48Dir);

        File.Copy(_fixtureDll, Path.Combine(refDir, "Sample.Pkg.dll"));
        File.Copy(_fixtureDll, Path.Combine(libDir, "Sample.Pkg.dll"));
        File.Copy(_fixtureDll, Path.Combine(net48Dir, "Sample.Pkg.Legacy.dll"));

        // Put companion XML next to lib/ copy so lib/ is preferred over ref/
        var fixtureXml = Path.ChangeExtension(_fixtureDll, ".xml");
        if (File.Exists(fixtureXml))
            File.Copy(fixtureXml, Path.Combine(libDir, "Sample.Pkg.xml"));

        // Create ancestor Directory.Build.props defining $(CustomSdkRoot)
        var sdkDir = Path.Combine(_tempDir, "VendorSdk");
        Directory.CreateDirectory(sdkDir);
        var vendorDll = Path.Combine(sdkDir, "VendorApi.dll");
        File.Copy(_fixtureDll, vendorDll);

        File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), $@"<Project>
  <PropertyGroup>
    <CustomSdkRoot>{sdkDir}</CustomSdkRoot>
  </PropertyGroup>
</Project>");

        // Create ProjectReference with custom <AssemblyName>
        var refProjDir = Path.Combine(_tempDir, "SubLib");
        var refBinNet8 = Path.Combine(refProjDir, "bin", "Release", "net8.0");
        Directory.CreateDirectory(refBinNet8);
        var customProjDll = Path.Combine(refBinNet8, "custom-sublib.dll");
        File.Copy(_fixtureDll, customProjDll);
        File.WriteAllText(Path.Combine(refProjDir, "SubLib.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>custom-sublib</AssemblyName>
  </PropertyGroup>
</Project>");

        var projDir = Path.Combine(_tempDir, "MyProj");
        var objDir = Path.Combine(projDir, "obj");
        var hintDir = Path.Combine(projDir, "libs");
        Directory.CreateDirectory(objDir);
        Directory.CreateDirectory(hintDir);

        var hintDll = Path.Combine(hintDir, "LocalHint.dll");
        File.Copy(_fixtureDll, hintDll);

        var csprojPath = Path.Combine(projDir, "MyProj.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <SdkDllName>VendorApi.dll</SdkDllName>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""LocalHint"">
      <HintPath>$(ProjectDir)libs\LocalHint.dll</HintPath>
    </Reference>
    <Reference Include=""VendorApi"">
      <HintPath>$(CustomSdkRoot)\$(SdkDllName)</HintPath>
    </Reference>
  </ItemGroup>
</Project>");

        var assetsPath = Path.Combine(objDir, "project.assets.json");
        File.WriteAllText(assetsPath, @"{
  ""version"": 3,
  ""targets"": {
    ""net8.0"": {
      ""Sample.Pkg/1.0.0"": {
        ""type"": ""package"",
        ""compile"": { ""ref/net8.0/Sample.Pkg.dll"": {} },
        ""runtime"": { ""lib/net8.0/Sample.Pkg.dll"": {} }
      },
      ""SubLib/1.0.0"": {
        ""type"": ""project""
      }
    },
    ""net48"": {
      ""Sample.Pkg/1.0.0"": {
        ""type"": ""package"",
        ""runtime"": { ""lib/net48/Sample.Pkg.Legacy.dll"": {} }
      }
    }
  },
  ""libraries"": {
    ""Sample.Pkg/1.0.0"": {
      ""type"": ""package"",
      ""path"": ""sample.pkg/1.0.0""
    },
    ""SubLib/1.0.0"": {
      ""type"": ""project"",
      ""msbuildProject"": ""../SubLib/SubLib.csproj""
    }
  }
}");

        var prevEnv = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", nugetRoot);

            // 1. Default TFM picks net8.0, deduplicates ref/ vs lib/ to lib/, expands Directory.Build.props + csproj macros, and resolves custom AssemblyName
            var auto = SourceResolver.Resolve(new[] { csprojPath }, Array.Empty<string>(), Array.Empty<string>());
            Assert.Contains(auto.Warnings, w => w.Contains("Multiple target frameworks"));
            Assert.Single(auto.Assemblies, a => a.Path.EndsWith("Sample.Pkg.dll"));
            Assert.Contains(auto.Assemblies, a => a.Path.Equals(Path.Combine(libDir, "Sample.Pkg.dll"), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(auto.Assemblies, a => a.Path.EndsWith("Sample.Pkg.Legacy.dll"));
            Assert.Contains(auto.Assemblies, a => a.Path.Equals(hintDll, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(auto.Assemblies, a => a.Path.Equals(vendorDll, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(auto.Assemblies, a => a.Path.Equals(customProjDll, StringComparison.OrdinalIgnoreCase));

            // 2. Explicit --tfm net48 selects net48
            var net48 = SourceResolver.Resolve(new[] { csprojPath }, Array.Empty<string>(), Array.Empty<string>(), targetFramework: "net48");
            Assert.Contains(net48.Assemblies, a => a.Path.EndsWith("Sample.Pkg.Legacy.dll"));
            Assert.DoesNotContain(net48.Assemblies, a => a.Path.EndsWith("Sample.Pkg.dll"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", prevEnv);
        }
    }
}
