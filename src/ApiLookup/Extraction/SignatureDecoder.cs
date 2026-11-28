using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;

namespace ApiLookup;

public sealed record GenericContext(
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<string> MethodParams,
    IReadOnlySet<string>? ValueTypeGenericParams = null)
{
    public static readonly GenericContext Empty = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Decodes ECMA-335 signature blobs into concise C# display names (for humans/agents).
/// </summary>
public sealed class DisplaySignatureProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly DisplaySignatureProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => FormatPrimitive(typeCode);

    internal static string FormatPrimitive(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString(),
    };

    public static string NormalizePrimitiveAlias(string simpleName) => simpleName.ToLowerInvariant() switch
    {
        "void" => "void",
        "boolean" => "bool",
        "bool" => "bool",
        "char" => "char",
        "sbyte" => "sbyte",
        "byte" => "byte",
        "int16" => "short",
        "short" => "short",
        "uint16" => "ushort",
        "ushort" => "ushort",
        "int32" => "int",
        "int" => "int",
        "uint32" => "uint",
        "uint" => "uint",
        "int64" => "long",
        "long" => "long",
        "uint64" => "ulong",
        "ulong" => "ulong",
        "single" => "float",
        "float" => "float",
        "double" => "double",
        "decimal" => "decimal",
        "string" => "string",
        "object" => "object",
        "intptr" => "nint",
        "nint" => "nint",
        "uintptr" => "nuint",
        "nuint" => "nuint",
        _ => simpleName,
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        var name = StripGenericArity(reader.GetString(td.Name));
        if (td.IsNested)
        {
            var decl = GetTypeFromDefinition(reader, td.GetDeclaringType(), 0);
            return $"{decl}.{name}";
        }
        return name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        var name = StripGenericArity(reader.GetString(tr.Name));
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var decl = GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, 0);
            return $"{decl}.{name}";
        }
        var ns = reader.GetString(tr.Namespace);
        return ShortenWellKnown(ns, name);
    }

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
        => shape.Rank <= 1 ? elementType + "[]" : $"{elementType}[{new string(',', shape.Rank - 1)}]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        => unmodifiedType; // Strip C++/CLI modopt/modreq noise (IsLong, IsConst, etc.)

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        if ((genericType == "Nullable" || genericType == "System.Nullable") && typeArguments.Length == 1)
            return typeArguments[0] + "?";
        return $"{genericType}<{string.Join(", ", typeArguments)}>";
    }

    public string GetGenericTypeParameter(GenericContext genericContext, int index)
        => index >= 0 && index < genericContext.TypeParams.Count
            ? genericContext.TypeParams[index]
            : $"T{index}";

    public string GetGenericMethodParameter(GenericContext genericContext, int index)
        => index >= 0 && index < genericContext.MethodParams.Count
            ? genericContext.MethodParams[index]
            : $"TMethod{index}";

    public string GetFunctionPointerType(MethodSignature<string> signature)
    {
        var args = signature.ParameterTypes.Concat(new[] { signature.ReturnType });
        return $"delegate*<{string.Join(", ", args)}>";
    }

    public static string StripGenericArity(string name)
    {
        var idx = name.IndexOf('`');
        return idx >= 0 ? name[..idx] : name;
    }

    private static string ShortenWellKnown(string ns, string name)
    {
        if (ns == "System")
        {
            return name switch
            {
                "Decimal" => "decimal",
                _ => name,
            };
        }
        return name;
    }
}

/// <summary>
/// Decodes signatures into namespace-qualified C# display names while preserving GenericContext parameter names
/// (e.g. System.Collections.Generic.List&lt;T&gt;). Used for BaseType and Interface full-name resolution.
/// </summary>
public sealed class FullNameDisplaySignatureProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly FullNameDisplaySignatureProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => DisplaySignatureProvider.FormatPrimitive(typeCode);

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        var name = DisplaySignatureProvider.StripGenericArity(reader.GetString(td.Name));
        if (td.IsNested)
            return $"{GetTypeFromDefinition(reader, td.GetDeclaringType(), 0)}.{name}";
        var ns = reader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        var name = DisplaySignatureProvider.StripGenericArity(reader.GetString(tr.Name));
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return $"{GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, 0)}.{name}";
        var ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
        => shape.Rank <= 1 ? elementType + "[]" : $"{elementType}[{new string(',', shape.Rank - 1)}]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        if (genericType == "System.Nullable" && typeArguments.Length == 1)
            return typeArguments[0] + "?";
        return $"{genericType}<{string.Join(", ", typeArguments)}>";
    }

    public string GetGenericTypeParameter(GenericContext genericContext, int index)
        => index >= 0 && index < genericContext.TypeParams.Count
            ? genericContext.TypeParams[index]
            : $"T{index}";

    public string GetGenericMethodParameter(GenericContext genericContext, int index)
        => index >= 0 && index < genericContext.MethodParams.Count
            ? genericContext.MethodParams[index]
            : $"TMethod{index}";

    public string GetFunctionPointerType(MethodSignature<string> signature)
    {
        var args = signature.ParameterTypes.Concat(new[] { signature.ReturnType });
        return $"delegate*<{string.Join(", ", args)}>";
    }
}

/// <summary>
/// Collects structured (FullName, SimpleName) type references directly from ECMA-335 signatures
/// without string re-parsing, preserving full namespaces while ignoring C++/CLI modopt/modreq modifiers.
/// </summary>
internal sealed class TypeRefCollectorProvider : ISignatureTypeProvider<IReadOnlyList<(string FullName, string SimpleName)>, GenericContext>
{
    public static readonly TypeRefCollectorProvider Instance = new();
    private static readonly IReadOnlyList<(string FullName, string SimpleName)> Empty = Array.Empty<(string, string)>();

    public IReadOnlyList<(string FullName, string SimpleName)> GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        if (typeCode == PrimitiveTypeCode.Void) return Empty;
        var full = XmlDocSignatureProvider.Instance.GetPrimitiveType(typeCode);
        var simple = DisplaySignatureProvider.FormatPrimitive(typeCode);
        return new[] { (full, simple) };
    }

    public IReadOnlyList<(string FullName, string SimpleName)> GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var full = FullNameDisplaySignatureProvider.Instance.GetTypeFromDefinition(reader, handle, rawTypeKind);
        return string.IsNullOrEmpty(full) ? Empty : new[] { (full, ExtractLeafAlias(full)) };
    }

    public IReadOnlyList<(string FullName, string SimpleName)> GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var full = FullNameDisplaySignatureProvider.Instance.GetTypeFromReference(reader, handle, rawTypeKind);
        return string.IsNullOrEmpty(full) ? Empty : new[] { (full, ExtractLeafAlias(full)) };
    }

    public IReadOnlyList<(string FullName, string SimpleName)> GetTypeFromSpecification(
        MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public IReadOnlyList<(string FullName, string SimpleName)> GetSZArrayType(IReadOnlyList<(string FullName, string SimpleName)> elementType)
        => elementType;

    public IReadOnlyList<(string FullName, string SimpleName)> GetArrayType(IReadOnlyList<(string FullName, string SimpleName)> elementType, ArrayShape shape)
        => elementType;

    public IReadOnlyList<(string FullName, string SimpleName)> GetByReferenceType(IReadOnlyList<(string FullName, string SimpleName)> elementType)
        => elementType;

    public IReadOnlyList<(string FullName, string SimpleName)> GetPointerType(IReadOnlyList<(string FullName, string SimpleName)> elementType)
        => elementType;

    public IReadOnlyList<(string FullName, string SimpleName)> GetPinnedType(IReadOnlyList<(string FullName, string SimpleName)> elementType)
        => elementType;

    public IReadOnlyList<(string FullName, string SimpleName)> GetModifiedType(
        IReadOnlyList<(string FullName, string SimpleName)> modifier,
        IReadOnlyList<(string FullName, string SimpleName)> unmodifiedType,
        bool isRequired)
        => unmodifiedType;

    public IReadOnlyList<(string FullName, string SimpleName)> GetGenericInstantiation(
        IReadOnlyList<(string FullName, string SimpleName)> genericType,
        ImmutableArray<IReadOnlyList<(string FullName, string SimpleName)>> typeArguments)
    {
        if (genericType.Count == 1 && genericType[0].FullName == "System.Nullable" && typeArguments.Length == 1)
            return typeArguments[0];

        var list = new List<(string FullName, string SimpleName)>(genericType.Count + typeArguments.Length);
        list.AddRange(genericType);
        foreach (var arg in typeArguments)
            list.AddRange(arg);
        return list;
    }

    public IReadOnlyList<(string FullName, string SimpleName)> GetGenericTypeParameter(GenericContext genericContext, int index) => Empty;

    public IReadOnlyList<(string FullName, string SimpleName)> GetGenericMethodParameter(GenericContext genericContext, int index) => Empty;

    public IReadOnlyList<(string FullName, string SimpleName)> GetFunctionPointerType(MethodSignature<IReadOnlyList<(string FullName, string SimpleName)>> signature)
    {
        var list = new List<(string FullName, string SimpleName)>();
        foreach (var pt in signature.ParameterTypes)
            list.AddRange(pt);
        list.AddRange(signature.ReturnType);
        return list;
    }

    public static IReadOnlyList<(string FullName, string SimpleName)> CollectFromEntity(MetadataReader md, EntityHandle handle, GenericContext ctx)
    {
        if (handle.IsNil) return Empty;
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => Instance.GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => Instance.GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => Instance.GetTypeFromSpecification(md, ctx, (TypeSpecificationHandle)handle, 0),
            _ => Empty,
        };
    }

    private static string ExtractLeafAlias(string bareFull)
    {
        var idx = bareFull.LastIndexOf('.');
        var leaf = idx >= 0 ? bareFull[(idx + 1)..] : bareFull;
        return DisplaySignatureProvider.NormalizePrimitiveAlias(leaf);
    }
}

/// <summary>
/// Lightweight signature provider that checks whether a property setter has modreq(System.Runtime.CompilerServices.IsExternalInit).
/// </summary>
internal sealed class InitOnlyDetectorProvider : ISignatureTypeProvider<bool, object?>
{
    public static readonly InitOnlyDetectorProvider Instance = new();

    public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;

    public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        return reader.GetString(td.Name) == "IsExternalInit" &&
               reader.GetString(td.Namespace) == "System.Runtime.CompilerServices";
    }

    public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        return reader.GetString(tr.Name) == "IsExternalInit" &&
               reader.GetString(tr.Namespace) == "System.Runtime.CompilerServices";
    }

    public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public bool GetSZArrayType(bool elementType) => false;
    public bool GetArrayType(bool elementType, ArrayShape shape) => false;
    public bool GetByReferenceType(bool elementType) => false;
    public bool GetPointerType(bool elementType) => false;
    public bool GetPinnedType(bool elementType) => false;
    public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => (isRequired && modifier) || unmodifiedType;
    public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => false;
    public bool GetGenericTypeParameter(object? genericContext, int index) => false;
    public bool GetGenericMethodParameter(object? genericContext, int index) => false;
    public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;
}

/// <summary>
/// Decodes signatures into full CLR / XML-doc-ID parameter type names (e.g. System.Collections.Generic.List{System.String}).
/// </summary>
public sealed class XmlDocSignatureProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly XmlDocSignatureProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => "System." + typeCode,
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        var name = reader.GetString(td.Name);
        if (td.IsNested)
            return GetTypeFromDefinition(reader, td.GetDeclaringType(), 0) + "." + name;
        var ns = reader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, 0) + "." + name;
        var ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
    {
        if (shape.Rank <= 1) return elementType + "[]";
        var sb = new StringBuilder(elementType).Append('[');
        for (var i = 0; i < shape.Rank; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("0:");
        }
        return sb.Append(']').ToString();
    }

    public string GetByReferenceType(string elementType) => elementType + "@";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        if (typeArguments.IsDefaultOrEmpty)
            return TypeNameHelper.StripGenericArityFromSegments(genericType);

        var segments = genericType.Split('.');
        var argIndex = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var tick = seg.IndexOf('`');
            if (tick < 0) continue;

            var namePart = seg[..tick];
            if (int.TryParse(seg[(tick + 1)..], out var arity) && arity > 0 && argIndex < typeArguments.Length)
            {
                var count = Math.Min(arity, typeArguments.Length - argIndex);
                var sb = new StringBuilder(namePart).Append('{');
                for (var j = 0; j < count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(typeArguments[argIndex + j]);
                }
                sb.Append('}');
                segments[i] = sb.ToString();
                argIndex += count;
            }
            else
            {
                segments[i] = namePart;
            }
        }

        var result = string.Join(".", segments);
        if (argIndex < typeArguments.Length)
        {
            var rem = new StringBuilder(result).Append('{');
            for (var j = argIndex; j < typeArguments.Length; j++)
            {
                if (j > argIndex) rem.Append(',');
                rem.Append(typeArguments[j]);
            }
            rem.Append('}');
            result = rem.ToString();
        }
        return result;
    }

    public string GetGenericTypeParameter(GenericContext genericContext, int index) => $"`{index}";

    public string GetGenericMethodParameter(GenericContext genericContext, int index) => $"``{index}";

    public string GetFunctionPointerType(MethodSignature<string> signature) => "System.IntPtr";
}

/// <summary>
/// Decodes type handles into CLR (reflection-style) names: namespace-qualified, <c>+</c> between nested types,
/// generic arity suffixes kept, and generic instantiations collapsed to their definition
/// (e.g. <c>System.Collections.Generic.List`1</c>). These match <c>types.clr_name</c> exactly, so base classes and
/// interfaces can be resolved without confusing <c>Foo</c> with <c>Foo&lt;T&gt;</c>.
/// </summary>
internal sealed class ClrNameProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly ClrNameProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => XmlDocSignatureProvider.Instance.GetPrimitiveType(typeCode);

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        var name = reader.GetString(td.Name);
        if (td.IsNested)
            return GetTypeFromDefinition(reader, td.GetDeclaringType(), 0) + "+" + name;
        var ns = reader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, 0) + "+" + name;
        var ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetGenericTypeParameter(GenericContext genericContext, int index) => "!" + index;
    public string GetGenericMethodParameter(GenericContext genericContext, int index) => "!!" + index;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "System.IntPtr";
}