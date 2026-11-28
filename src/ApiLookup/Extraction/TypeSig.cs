using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;

namespace ApiLookup;

/// <summary>
/// Structured form of a decoded ECMA-335 type signature. Unlike the flat display strings produced by
/// <see cref="DisplaySignatureProvider"/>, it keeps the class/value-type distinction carried by the signature
/// itself, which is what C# nullable metadata (<c>NullableAttribute</c>) is defined over.
/// </summary>
internal abstract record TypeSig;

internal sealed record PrimitiveTypeSig(PrimitiveTypeCode Code) : TypeSig
{
    public bool IsReferenceType => Code is PrimitiveTypeCode.String or PrimitiveTypeCode.Object;
}

/// <param name="DisplayName">Short C# display name, e.g. <c>List</c> or <c>Outer.Inner</c> (generic arity stripped).</param>
/// <param name="FullName">Namespace-qualified name with arity stripped, e.g. <c>System.Nullable</c>.</param>
/// <param name="XmlDocName">XML doc ID form with raw arity, e.g. <c>System.Collections.Generic.List`1</c>.</param>
internal sealed record NamedTypeSig(string DisplayName, string FullName, bool IsValueType, string XmlDocName) : TypeSig;

internal sealed record GenericInstanceTypeSig(NamedTypeSig Definition, ImmutableArray<TypeSig> Arguments) : TypeSig
{
    public bool IsNullableValueType => Definition.FullName == "System.Nullable" && Arguments.Length == 1;
}

/// <param name="Rank">1 for single-dimensional (<c>T[]</c>), otherwise the multi-dimensional rank.</param>
internal sealed record ArrayTypeSig(TypeSig Element, int Rank) : TypeSig;

internal sealed record ByRefTypeSig(TypeSig Element) : TypeSig;

internal sealed record PointerTypeSig(TypeSig Element) : TypeSig;

internal sealed record GenericParameterTypeSig(string Name, bool IsValueTypeConstrained, int Index, bool IsMethodParameter) : TypeSig;

internal sealed record FunctionPointerTypeSig(MethodSignature<TypeSig> Signature) : TypeSig;

/// <summary>Decodes signatures into <see cref="TypeSig"/> trees.</summary>
internal sealed class TypeSigProvider : ISignatureTypeProvider<TypeSig, GenericContext>
{
    public static readonly TypeSigProvider Instance = new();

    private const byte ValueTypeKind = (byte)SignatureTypeKind.ValueType;

    public TypeSig GetPrimitiveType(PrimitiveTypeCode typeCode) => new PrimitiveTypeSig(typeCode);

    public TypeSig GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var isValueType = rawTypeKind == ValueTypeKind ||
                          (rawTypeKind == 0 && IsValueTypeDefinition(reader, reader.GetTypeDefinition(handle)));
        return new NamedTypeSig(
            DisplaySignatureProvider.Instance.GetTypeFromDefinition(reader, handle, rawTypeKind),
            FullNameDisplaySignatureProvider.Instance.GetTypeFromDefinition(reader, handle, rawTypeKind),
            isValueType,
            XmlDocSignatureProvider.Instance.GetTypeFromDefinition(reader, handle, rawTypeKind));
    }

    /// <remarks>
    /// Outside of signature blobs (e.g. an event's declared type) the raw kind is unknown (0); such references
    /// are treated as reference types, which holds for every place this is used (delegate-typed events).
    /// </remarks>
    public TypeSig GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => new NamedTypeSig(
            DisplaySignatureProvider.Instance.GetTypeFromReference(reader, handle, rawTypeKind),
            FullNameDisplaySignatureProvider.Instance.GetTypeFromReference(reader, handle, rawTypeKind),
            rawTypeKind == ValueTypeKind,
            XmlDocSignatureProvider.Instance.GetTypeFromReference(reader, handle, rawTypeKind));

    public TypeSig GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public TypeSig GetSZArrayType(TypeSig elementType) => new ArrayTypeSig(elementType, 1);

    public TypeSig GetArrayType(TypeSig elementType, ArrayShape shape) => new ArrayTypeSig(elementType, Math.Max(1, shape.Rank));

    public TypeSig GetByReferenceType(TypeSig elementType) => new ByRefTypeSig(elementType);

    public TypeSig GetPointerType(TypeSig elementType) => new PointerTypeSig(elementType);

    public TypeSig GetPinnedType(TypeSig elementType) => elementType;

    public TypeSig GetModifiedType(TypeSig modifier, TypeSig unmodifiedType, bool isRequired) => unmodifiedType;

    public TypeSig GetGenericInstantiation(TypeSig genericType, ImmutableArray<TypeSig> typeArguments)
        => genericType is NamedTypeSig named ? new GenericInstanceTypeSig(named, typeArguments) : genericType;

    public TypeSig GetGenericTypeParameter(GenericContext genericContext, int index)
    {
        var name = index >= 0 && index < genericContext.TypeParams.Count ? genericContext.TypeParams[index] : $"T{index}";
        return new GenericParameterTypeSig(name, genericContext.ValueTypeGenericParams?.Contains(name) == true, index, IsMethodParameter: false);
    }

    public TypeSig GetGenericMethodParameter(GenericContext genericContext, int index)
    {
        var name = index >= 0 && index < genericContext.MethodParams.Count ? genericContext.MethodParams[index] : $"TMethod{index}";
        return new GenericParameterTypeSig(name, genericContext.ValueTypeGenericParams?.Contains(name) == true, index, IsMethodParameter: true);
    }

    public TypeSig GetFunctionPointerType(MethodSignature<TypeSig> signature) => new FunctionPointerTypeSig(signature);

    /// <summary>Decodes a TypeDef/TypeRef/TypeSpec handle that appears outside a signature blob.</summary>
    public static TypeSig? FromEntity(MetadataReader md, EntityHandle handle, GenericContext ctx) => handle.Kind switch
    {
        HandleKind.TypeDefinition => Instance.GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => Instance.GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => Instance.GetTypeFromSpecification(md, ctx, (TypeSpecificationHandle)handle, 0),
        _ => null,
    };

    private static bool IsValueTypeDefinition(MetadataReader md, TypeDefinition td)
    {
        if (td.BaseType.IsNil) return false;
        var baseName = TypeNameHelper.ResolveEntityFullName(md, td.BaseType, GenericContext.Empty);
        return baseName is WellKnownTypes.ValueType or WellKnownTypes.Enum;
    }
}

/// <summary>
/// Nullability flags for one type reference, as stored in <c>NullableAttribute</c> (single byte or byte[]) or inherited
/// from <c>NullableContextAttribute</c>. Values: 0 = oblivious, 1 = not annotated, 2 = annotated (<c>?</c>).
/// </summary>
internal readonly struct NullableFlags
{
    private readonly byte[]? _perPosition;
    private readonly byte _uniform;

    private NullableFlags(byte[]? perPosition, byte uniform)
    {
        _perPosition = perPosition;
        _uniform = uniform;
    }

    public static readonly NullableFlags Oblivious = new(null, 0);

    public static NullableFlags Uniform(byte value) => new(null, value);

    public static NullableFlags PerPosition(byte[] flags) => new(flags, 0);

    public Cursor CreateCursor() => new(_perPosition, _uniform);

    /// <summary>Consumes flags in the pre-order traversal defined by the C# nullable metadata spec.</summary>
    public sealed class Cursor
    {
        private readonly byte[]? _flags;
        private readonly byte _uniform;
        private int _index;

        internal Cursor(byte[]? flags, byte uniform)
        {
            _flags = flags;
            _uniform = uniform;
        }

        public byte Next()
        {
            if (_flags == null) return _uniform;
            return _index < _flags.Length ? _flags[_index++] : (byte)0;
        }
    }
}

/// <summary>
/// Renders <see cref="TypeSig"/> trees as C# type syntax, applying nullable annotations at every position
/// (e.g. <c>Dictionary&lt;string, object?&gt;?</c>, <c>string?[]</c>), following
/// https://github.com/dotnet/roslyn/blob/main/docs/features/nullable-metadata.md:
/// reference types and arrays consume a flag then their type arguments / element; generic value types consume a
/// (placeholder) flag then their arguments; non-generic value types consume nothing; <c>Nullable&lt;T&gt;</c> is
/// represented by its argument only; type parameters always consume a flag.
/// </summary>
internal static class TypeSigRenderer
{
    /// <summary>Renders without nullable annotations (equivalent to <see cref="DisplaySignatureProvider"/> output).</summary>
    public static string Render(TypeSig sig) => Render(sig, NullableFlags.Oblivious);

    public static string Render(TypeSig sig, NullableFlags flags)
    {
        var sb = new StringBuilder();
        Append(sb, sig, flags.CreateCursor());
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, TypeSig sig, NullableFlags.Cursor flags)
    {
        switch (sig)
        {
            case PrimitiveTypeSig p:
            {
                var annotated = p.IsReferenceType && flags.Next() == 2;
                sb.Append(DisplaySignatureProvider.FormatPrimitive(p.Code));
                if (annotated) sb.Append('?');
                break;
            }
            case NamedTypeSig n:
            {
                var annotated = !n.IsValueType && flags.Next() == 2;
                sb.Append(n.DisplayName);
                if (annotated) sb.Append('?');
                break;
            }
            case GenericInstanceTypeSig g when g.IsNullableValueType:
                Append(sb, g.Arguments[0], flags);
                sb.Append('?');
                break;
            case GenericInstanceTypeSig g:
            {
                var own = flags.Next(); // generic value types carry an explicit placeholder byte
                var annotated = !g.Definition.IsValueType && own == 2;
                sb.Append(g.Definition.DisplayName).Append('<');
                for (var i = 0; i < g.Arguments.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Append(sb, g.Arguments[i], flags);
                }
                sb.Append('>');
                if (annotated) sb.Append('?');
                break;
            }
            case ArrayTypeSig a:
            {
                var annotated = flags.Next() == 2;
                Append(sb, a.Element, flags);
                sb.Append(a.Rank <= 1 ? "[]" : $"[{new string(',', a.Rank - 1)}]");
                if (annotated) sb.Append('?');
                break;
            }
            case ByRefTypeSig r:
                Append(sb, r.Element, flags);
                sb.Append('&');
                break;
            case PointerTypeSig ptr:
                Append(sb, ptr.Element, flags);
                sb.Append('*');
                break;
            case GenericParameterTypeSig gp:
            {
                var annotated = flags.Next() == 2 && !gp.IsValueTypeConstrained;
                sb.Append(gp.Name);
                if (annotated) sb.Append('?');
                break;
            }
            case FunctionPointerTypeSig fp:
            {
                // Function pointer nullability encoding is not covered by the spec above; render oblivious.
                var parts = fp.Signature.ParameterTypes.Append(fp.Signature.ReturnType).Select(Render);
                sb.Append("delegate*<").Append(string.Join(", ", parts)).Append('>');
                break;
            }
        }
    }
}

/// <summary>
/// Projections of a decoded <see cref="TypeSig"/> used by extraction, so each signature blob is decoded once and the
/// display form, XML doc ID form, and by-type references are all derived from the same tree.
/// </summary>
internal static class TypeSigProjections
{
    private static readonly IReadOnlyList<(string FullName, string SimpleName)> NoRefs = Array.Empty<(string, string)>();

    /// <summary>XML documentation ID form (e.g. <c>System.Collections.Generic.List{System.String}</c>, <c>``0</c>, <c>System.Int32@</c>).</summary>
    public static string ToXmlDocName(TypeSig sig) => sig switch
    {
        PrimitiveTypeSig p => XmlDocSignatureProvider.Instance.GetPrimitiveType(p.Code),
        NamedTypeSig n => n.XmlDocName,
        GenericInstanceTypeSig g => XmlDocSignatureProvider.Instance.GetGenericInstantiation(
            g.Definition.XmlDocName, g.Arguments.Select(ToXmlDocName).ToImmutableArray()),
        ArrayTypeSig a => a.Rank <= 1
            ? ToXmlDocName(a.Element) + "[]"
            : XmlDocSignatureProvider.Instance.GetArrayType(ToXmlDocName(a.Element), new ArrayShape(a.Rank, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty)),
        ByRefTypeSig r => ToXmlDocName(r.Element) + "@",
        PointerTypeSig ptr => ToXmlDocName(ptr.Element) + "*",
        GenericParameterTypeSig gp => (gp.IsMethodParameter ? "``" : "`") + gp.Index,
        FunctionPointerTypeSig => "System.IntPtr",
        _ => string.Empty,
    };

    /// <summary>
    /// Named types referenced anywhere in the signature as (namespace-qualified name, simple alias), for the by-type
    /// reverse lookup table. Generic parameters and <c>void</c> contribute nothing; <c>Nullable&lt;T&gt;</c> contributes <c>T</c>.
    /// </summary>
    public static IReadOnlyList<(string FullName, string SimpleName)> CollectTypeRefs(TypeSig sig)
    {
        var list = new List<(string, string)>();
        Collect(sig, list);
        return list.Count == 0 ? NoRefs : list;
    }

    private static void Collect(TypeSig sig, List<(string FullName, string SimpleName)> list)
    {
        switch (sig)
        {
            case PrimitiveTypeSig p when p.Code != PrimitiveTypeCode.Void:
                list.Add((XmlDocSignatureProvider.Instance.GetPrimitiveType(p.Code), DisplaySignatureProvider.FormatPrimitive(p.Code)));
                break;
            case NamedTypeSig n when !string.IsNullOrEmpty(n.FullName):
                list.Add((n.FullName, LeafAlias(n.FullName)));
                break;
            case GenericInstanceTypeSig g when g.IsNullableValueType:
                Collect(g.Arguments[0], list);
                break;
            case GenericInstanceTypeSig g:
                Collect(g.Definition, list);
                foreach (var arg in g.Arguments) Collect(arg, list);
                break;
            case ArrayTypeSig a:
                Collect(a.Element, list);
                break;
            case ByRefTypeSig r:
                Collect(r.Element, list);
                break;
            case PointerTypeSig ptr:
                Collect(ptr.Element, list);
                break;
            case FunctionPointerTypeSig fp:
                foreach (var pt in fp.Signature.ParameterTypes) Collect(pt, list);
                Collect(fp.Signature.ReturnType, list);
                break;
        }
    }

    private static string LeafAlias(string fullName)
    {
        var idx = fullName.LastIndexOf('.');
        return DisplaySignatureProvider.NormalizePrimitiveAlias(idx >= 0 ? fullName[(idx + 1)..] : fullName);
    }
}