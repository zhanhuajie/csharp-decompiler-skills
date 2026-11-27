using System.IO;
using System.Linq;
using Xunit;

namespace ApiLookup.Tests;

public class MetadataExtractorTests
{
    private readonly string _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;

    [Fact]
    public void Extract_ReadsTypesMembersEnumsExtensionsAndXmlDocs()
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

        // Interface implementation Dispose() must NOT be marked virtual
        var dispose = pipe.Members.Single(m => m.Name == "Dispose");
        Assert.Equal(string.Empty, dispose.Modifiers);
        Assert.Equal("public void Dispose()", dispose.Signature);

        // Conversion operator matches XML doc via ~ReturnType suffix
        var opImp = pipe.Members.Single(m => m.Name == "op_Implicit");
        Assert.Contains("handle string", opImp.Summary);

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

        // Generic constrained extension method resolves constraint target
        var genericExt = extType.Members.Single(m => m.Name == "UpgradeEntityOpen");
        Assert.Equal("SampleBaseEntity", genericExt.ExtTargetSimple);
        Assert.Contains(genericExt.ExtensionTargets, et => et.TargetFull == "ApiLookup.Tests.Fixtures.SampleBaseEntity");

        // Array extension method preserves [] in target so it does NOT collide with single SamplePipeSegment
        var arrayExt = extType.Members.Single(m => m.Name == "CountSegmentArray");
        Assert.Equal("SamplePipeSegment[]", arrayExt.ExtTargetSimple);
    }

    [Fact]
    public void Extract_HandlesGenericsInitOnlySealedOverrideDelegatesConstantsAndValueDocs()
    {
        var asm = MetadataExtractor.Extract(_fixtureDll)!;

        // Generic container: constraints, nullable T?, init setter, indexer, nullable event, escaped const
        var container = asm.Types.Single(t => t.SimpleName == "SampleContainer");
        Assert.Equal("where T : SampleBaseEntity, IDisposable", container.GenericConstraints);

        var factoryBox = asm.Types.Single(t => t.SimpleName == "SampleFactoryBox");
        Assert.Equal("where T : SampleBaseEntity, new()", factoryBox.GenericConstraints);

        var currentProp = container.Members.Single(m => m.Name == "Current");
        Assert.Contains("T? Current", currentProp.Signature);
        Assert.Contains("init;", currentProp.Signature);
        Assert.DoesNotContain("set;", currentProp.Signature);

        var indexer = container.Members.Single(m => m.Name == "Item");
        Assert.Contains("T? this[int index]", indexer.Signature);

        var ev = container.Members.Single(m => m.Name == "Changed");
        Assert.Equal("event", ev.Kind);
        Assert.Contains("event EventHandler? Changed", ev.Signature);

        var constField = container.Members.Single(m => m.Name == "EscapedText");
        Assert.Contains("\\n", constField.Signature);
        Assert.Contains("\\\"quoted\\\"", constField.Signature);

        // Derived generic class: BaseType preserves full type arg without `0, sealed override, <value> doc, NRT ?, enum default
        var pipeContainer = asm.Types.Single(t => t.SimpleName == "SamplePipeContainer");
        Assert.Equal("ApiLookup.Tests.Fixtures.SampleContainer<ApiLookup.Tests.Fixtures.SamplePipeSegment>", pipeContainer.BaseType);
        Assert.Equal("ApiLookup.Tests.Fixtures.SampleContainer", pipeContainer.BaseBare);
        Assert.DoesNotContain("`0", pipeContainer.BaseType);

        var describe = pipeContainer.Members.Single(m => m.Name == "Describe");
        Assert.Equal("sealed override", describe.Modifiers);
        Assert.StartsWith("public sealed override string Describe()", describe.Signature);

        var processItem = pipeContainer.Members.Single(m => m.Name == "ProcessItem");
        Assert.Contains("where U : struct", processItem.Signature);

        var active = pipeContainer.Members.Single(m => m.Name == "Active");
        Assert.Contains("SamplePipeSegment? Active", active.Signature);
        Assert.Contains("Active pipe segment instance", active.ReturnsDoc);

        var normKind = pipeContainer.Members.Single(m => m.Name == "NormalizeKind");
        Assert.Contains("SamplePipeKind.Elbow", normKind.Signature);

        // Interface members should not have 'abstract' modifier
        var iface = asm.Types.Single(t => t.FullName == "ApiLookup.Tests.Fixtures.ISampleItem");
        var ifaceProp = iface.Members.Single(m => m.Name == "ItemCode");
        Assert.Equal(string.Empty, ifaceProp.Modifiers);

        // Delegate signature
        var del = asm.Types.Single(t => t.SimpleName == "SampleFilterHandler");
        Assert.Equal("delegate", del.Kind);
        Assert.Equal("public delegate bool SampleFilterHandler(SampleBaseEntity entity, int depth)", del.DelegateSignature);
    }

    [Fact]
    public void Extract_HandlesStructModifiersRefReturnsDecimalConstantsAndFlagsDefaults()
    {
        var asm = MetadataExtractor.Extract(_fixtureDll)!;

        var roPoint = asm.Types.Single(t => t.SimpleName == "SampleReadOnlyPoint");
        Assert.Equal("readonly", roPoint.Modifiers);
        var ctor = roPoint.Members.Single(m => m.Kind == "ctor");
        Assert.Contains("in double x", ctor.Signature);
        var xProp = roPoint.Members.Single(m => m.Name == "X");
        Assert.StartsWith("public ref readonly double X", xProp.Signature);
        Assert.DoesNotContain("&", xProp.Signature);

        var refSpan = asm.Types.Single(t => t.SimpleName == "SampleRefSpanReader");
        Assert.Equal("ref", refSpan.Modifiers);
        var posProp = refSpan.Members.Single(m => m.Name == "Position");
        Assert.StartsWith("public ref int Position", posProp.Signature);

        var roRefView = asm.Types.Single(t => t.SimpleName == "SampleReadOnlyRefView");
        Assert.Equal("readonly ref", roRefView.Modifiers);

        var factory = asm.Types.Single(t => t.SimpleName == "SamplePipeFactory");
        var decConst = factory.Members.Single(m => m.Name == "DefaultWallThickness");
        Assert.Equal("const", decConst.Modifiers);
        Assert.Contains("decimal DefaultWallThickness = 12.75m", decConst.Signature);

        var createDefault = factory.Members.Single(m => m.Name == "CreateDefaultPipe");
        Assert.Contains("decimal tolerance = 0.05m", createDefault.Signature);

        var openFlags = factory.Members.Single(m => m.Name == "OpenWithFlags");
        Assert.Contains("SampleAccessFlags.Read | SampleAccessFlags.Write", openFlags.Signature);
        Assert.Contains("(SamplePipeKind)99", openFlags.Signature);
    }

    [Fact]
    public void ExtractReferencedSimpleTypeNames_HandlesGenericsMultiDimArraysAndPrimitiveAliases()
    {
        var refs1 = MetadataExtractor.ExtractReferencedSimpleTypeNames("IDictionary<string, IList<SamplePipeSegment[,,]>>");
        Assert.Contains("IDictionary", refs1);
        Assert.Contains("string", refs1);
        Assert.Contains("IList", refs1);
        Assert.Contains("SamplePipeSegment", refs1);

        Assert.Equal("string", MetadataExtractor.ExtractSimpleTypeName("System.String"));
        Assert.Equal("int", MetadataExtractor.ExtractSimpleTypeName("System.Int32[]"));
        Assert.Equal("Inner", MetadataExtractor.ExtractSimpleTypeName("Ns.Outer<int>.Inner<string>"));
    }

    [Fact]
    public void XmlDocReader_DisablesAmbiguousFallbackAcrossSameArityOverloads()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"xmldoc-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, @"<?xml version=""1.0""?>
<doc>
  <members>
    <member name=""M:Ns.Foo.Connect(System.Int32,System.Int32)""><summary>Int overload</summary></member>
    <member name=""M:Ns.Foo.Connect(System.String,System.String)""><summary>String overload</summary></member>
    <member name=""M:Ns.Foo.UniqueMethod(System.Double)""><summary>Unique arity doc</summary></member>
  </members>
</doc>");
            var book = XmlDocReader.Load(tmp);
            Assert.Equal("Int overload", book.Find("M:Ns.Foo.Connect(System.Int32,System.Int32)")?.Summary);
            Assert.Null(book.Find("M:Ns.Foo.Connect(System.Double,System.Double)", "M:Ns.Foo.Connect#2"));
            Assert.Equal("Unique arity doc", book.Find("M:Ns.Foo.UniqueMethod(System.Double modopt(IsConst))", "M:Ns.Foo.UniqueMethod#1")?.Summary);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
