using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ApiLookup.Tests;

public class TypeLocatorTests
{
    private readonly string _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;

    [Fact]
    public void Locate_FindsSimpleNestedGenericAndQualifiedTypesWithoutCache()
    {
        var resolved = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll });

        var simple = TypeLocator.Locate(resolved.Assemblies, "SamplePipeSegment");
        Assert.Single(simple);
        Assert.Equal("ApiLookup.Tests.Fixtures.SamplePipeSegment", simple[0].FullName);

        var nestedByLeaf = TypeLocator.Locate(resolved.Assemblies, "PortSnapshot");
        Assert.Single(nestedByLeaf);
        Assert.Equal("ApiLookup.Tests.Fixtures.SamplePipeSegment+PortSnapshot", nestedByLeaf[0].ClrName);

        var nestedByPlus = TypeLocator.Locate(resolved.Assemblies, "SamplePipeSegment+PortSnapshot");
        Assert.Single(nestedByPlus);

        var nestedByDot = TypeLocator.Locate(resolved.Assemblies, "SamplePipeSegment.PortSnapshot");
        Assert.Single(nestedByDot);

        var generic = TypeLocator.Locate(resolved.Assemblies, "SampleContainer");
        Assert.Single(generic);
        Assert.Equal("ApiLookup.Tests.Fixtures.SampleContainer<T>", generic[0].FullName);
    }

    [Fact]
    public void Locate_RespectsNamespaceBoundaryVisibilityAndTypeForwarders()
    {
        var resolved = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { _fixtureDll });

        // Namespace boundary: Fixtures must exclude FixturesExtra
        Assert.Empty(TypeLocator.Locate(resolved.Assemblies, "ExtraFixtureType", namespacePrefix: "ApiLookup.Tests.Fixtures"));
        Assert.Single(TypeLocator.Locate(resolved.Assemblies, "ExtraFixtureType", namespacePrefix: "ApiLookup.Tests.FixturesExtra"));

        // Visibility: internal type hidden by default, revealed when includeNonPublic == true
        Assert.Empty(TypeLocator.Locate(resolved.Assemblies, "InternalFixtureHelper", includeNonPublic: false));
        var internalHits = TypeLocator.Locate(resolved.Assemblies, "InternalFixtureHelper", includeNonPublic: true);
        Assert.Single(internalHits);
        Assert.Equal("internal", internalHits[0].Accessibility);

        // Facade [TypeForwardedTo] detection on System.Runtime.dll -> System.Private.CoreLib
        var coreLibPath = typeof(object).Assembly.Location;
        var runtimeDir = Path.GetDirectoryName(coreLibPath)!;
        var sysRuntimeFacade = Path.Combine(runtimeDir, "System.Runtime.dll");
        if (File.Exists(sysRuntimeFacade))
        {
            var facadeOnly = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { sysRuntimeFacade });
            var forwardedHits = TypeLocator.Locate(facadeOnly.Assemblies, "System.String");
            Assert.Single(forwardedHits);
            Assert.StartsWith("forwarded ->", forwardedHits[0].Accessibility);

            // When both facade and implementation DLL are scanned, concrete TypeDefinition wins over forwarder stub
            var both = SourceResolver.Resolve(Array.Empty<string>(), Array.Empty<string>(), new[] { sysRuntimeFacade, coreLibPath });
            var concreteHits = TypeLocator.Locate(both.Assemblies, "System.String");
            Assert.Single(concreteHits);
            Assert.Equal("public", concreteHits[0].Accessibility);
        }
    }
}
