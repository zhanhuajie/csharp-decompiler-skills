using System;
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
    public void Locate_RespectsNamespaceBoundaryAndVisibility()
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
    }
}
