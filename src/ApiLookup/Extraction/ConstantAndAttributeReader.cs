using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace ApiLookup;

internal sealed class EnumLookup
{
    private sealed record EnumEntry(Dictionary<string, string> ByValue, List<(long Bits, string Name)> FlagMembers, bool IsFlags);

    private readonly Dictionary<string, EnumEntry> _byFull = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumEntry?> _bySimple = new(StringComparer.Ordinal);

    public void Register(string fullName, string simpleName, Dictionary<string, string> values, List<(long Bits, string Name)> flagMembers, bool isFlags)
    {
        if (values.Count == 0) return;
        var entry = new EnumEntry(values, flagMembers, isFlags);
        _byFull[fullName] = entry;
        if (!_bySimple.TryAdd(simpleName, entry))
            _bySimple[simpleName] = null; // Ambiguous simple name
    }

    public string? ResolveExpression(string typeDisplayName, string numericValue)
    {
        var cleanType = typeDisplayName.TrimEnd('?');
        var bare = TypeNameHelper.StripGenerics(cleanType).TrimEnd('?', '&', '*');

        EnumEntry? entry = null;
        if (!_byFull.TryGetValue(bare, out entry))
        {
            var simple = TypeNameHelper.ExtractSimpleTypeName(bare);
            if (_bySimple.TryGetValue(simple, out var candidate))
                entry = candidate;
        }

        if (entry == null) return null;

        if (entry.ByValue.TryGetValue(numericValue, out var exactName))
            return $"{cleanType}.{exactName}";

        if (entry.IsFlags && long.TryParse(numericValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target) && target != 0)
        {
            var remaining = target;
            var parts = new List<string>();
            foreach (var (bits, name) in entry.FlagMembers)
            {
                if (bits != 0 && (remaining & bits) == bits)
                {
                    parts.Add($"{cleanType}.{name}");
                    remaining &= ~bits;
                    if (remaining == 0) break;
                }
            }
            if (remaining == 0 && parts.Count > 0)
                return string.Join(" | ", parts);
        }

        return null;
    }
}

internal sealed class ReferenceTypeLookup
{
    private static readonly HashSet<string> WellKnownRefTypes = new(StringComparer.Ordinal)
    {
        "string", "String", "System.String",
        "object", "Object", "System.Object",
        "Exception", "System.Exception",
        "Type", "System.Type",
        "Uri", "System.Uri",
        "Task", "System.Threading.Tasks.Task",
        "EventHandler", "System.EventHandler",
        "Delegate", "System.Delegate",
        "MulticastDelegate", "System.MulticastDelegate",
        "Attribute", "System.Attribute",
        "Stream", "System.IO.Stream",
    };

    private readonly HashSet<string> _refNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _valueNames = new(StringComparer.Ordinal);

    public void RegisterType(string bareFullName, string simpleName, bool isValueType)
    {
        if (isValueType)
        {
            _valueNames.Add(bareFullName);
            _valueNames.Add(simpleName);
        }
        else
        {
            _refNames.Add(bareFullName);
            _refNames.Add(simpleName);
        }
    }

    public bool IsConfirmedReferenceType(string cleanDisplayType)
    {
        if (string.IsNullOrEmpty(cleanDisplayType)) return false;
        if (cleanDisplayType.EndsWith(']')) return true; // All CLR arrays are reference types
        if (WellKnownRefTypes.Contains(cleanDisplayType)) return true;
        if (_valueNames.Contains(cleanDisplayType)) return false;
        return _refNames.Contains(cleanDisplayType);
    }
}

internal static class ConstantAndAttributeReader
{
    private static readonly HashSet<string> NonEnumKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bool", "boolean", "System.Boolean",
        "byte", "System.Byte",
        "sbyte", "System.SByte",
        "short", "int16", "System.Int16",
        "ushort", "uint16", "System.UInt16",
        "int", "int32", "System.Int32",
        "uint", "uint32", "System.UInt32",
        "long", "int64", "System.Int64",
        "ulong", "uint64", "System.UInt64",
        "float", "single", "System.Single",
        "double", "System.Double",
        "decimal", "System.Decimal",
        "char", "System.Char",
        "string", "System.String",
        "object", "System.Object",
        "nint", "intptr", "System.IntPtr",
        "nuint", "uintptr", "System.UIntPtr",
    };

    public static (EnumLookup Enums, ReferenceTypeLookup RefTypes) ScanAssemblyTypes(MetadataReader md, bool includeNonPublic)
    {
        var enums = new EnumLookup();
        var refTypes = new ReferenceTypeLookup();

        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            var rawName = md.GetString(td.Name);
            if (MetadataExtractor.ShouldSkipType(md, td, rawName, includeNonPublic))
                continue;

            var (_, _, _, bareFull, _, simpleName) = TypeNameHelper.BuildTypeNames(md, td);
            var isInterface = (td.Attributes & TypeAttributes.Interface) != 0;
            var baseFull = td.BaseType.IsNil ? null : TypeNameHelper.ResolveEntityFullName(md, td.BaseType, GenericContext.Empty);
            var isEnum = !isInterface && baseFull == WellKnownTypes.Enum;
            var isStruct = !isInterface && baseFull == WellKnownTypes.ValueType;

            refTypes.RegisterType(bareFull, simpleName, isValueType: isEnum || isStruct);

            if (!isEnum) continue;

            var isFlags = HasAttribute(md, td.GetCustomAttributes(), "System", "FlagsAttribute");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var flagMembers = new List<(long Bits, string Name)>();

            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if ((f.Attributes & FieldAttributes.Literal) == 0) continue;
                var ch = f.GetDefaultValue();
                if (ch.IsNil) continue;
                var val = ReadConstantValue(md, md.GetConstant(ch));
                if (val == null) continue;
                var fName = md.GetString(f.Name);
                map.TryAdd(val, fName);
                if (isFlags && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) && bits != 0)
                    flagMembers.Add((bits, fName));
            }

            // Sort single-bit / smaller flags in definition order, preferring higher bit counts first only if composite flags exist
            flagMembers.Sort((a, b) => System.Numerics.BitOperations.PopCount((ulong)b.Bits).CompareTo(System.Numerics.BitOperations.PopCount((ulong)a.Bits)));
            enums.Register(bareFull, simpleName, map, flagMembers, isFlags);
        }

        return (enums, refTypes);
    }

    public static string FormatDefaultValue(
        string paramType,
        string rawDefault,
        bool isIntegerConstant,
        EnumLookup enumLookup,
        GenericContext ctx)
    {
        var resolved = enumLookup.ResolveExpression(paramType, rawDefault);
        if (resolved != null) return resolved;

        if (isIntegerConstant)
        {
            var cleanType = paramType.TrimEnd('?', '&', '*');
            if (!string.IsNullOrEmpty(cleanType) &&
                !NonEnumKeywords.Contains(cleanType) &&
                !TypeNameHelper.IsGenericParamName(cleanType, ctx))
            {
                // Cross-assembly enum whose member names weren't in this DLL: cast integer so C# syntax stays valid
                return $"({cleanType}){rawDefault}";
            }
        }

        return rawDefault;
    }

    public static bool IsIntegerConstantType(ConstantTypeCode code) => code is
        ConstantTypeCode.SByte or ConstantTypeCode.Byte or
        ConstantTypeCode.Int16 or ConstantTypeCode.UInt16 or
        ConstantTypeCode.Int32 or ConstantTypeCode.UInt32 or
        ConstantTypeCode.Int64 or ConstantTypeCode.UInt64;

    public static string? ReadConstantValue(MetadataReader md, Constant constant)
    {
        try
        {
            var br = md.GetBlobReader(constant.Value);
            return constant.TypeCode switch
            {
                ConstantTypeCode.NullReference => "null",
                ConstantTypeCode.Boolean => br.ReadBoolean() ? "true" : "false",
                ConstantTypeCode.Char => EscapeCharLiteral(br.ReadChar()),
                ConstantTypeCode.SByte => br.ReadSByte().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Byte => br.ReadByte().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int16 => br.ReadInt16().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt16 => br.ReadUInt16().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int32 => br.ReadInt32().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt32 => br.ReadUInt32().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Int64 => br.ReadInt64().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.UInt64 => br.ReadUInt64().ToString(CultureInfo.InvariantCulture),
                ConstantTypeCode.Single => br.ReadSingle().ToString("G", CultureInfo.InvariantCulture),
                ConstantTypeCode.Double => br.ReadDouble().ToString("G", CultureInfo.InvariantCulture),
                ConstantTypeCode.String => EscapeStringLiteral(br.ReadUTF16(br.Length)),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool TryReadDecimalConstant(MetadataReader md, CustomAttributeHandleCollection attrs, out string? literal)
    {
        literal = null;
        foreach (var ah in attrs)
        {
            var ca = md.GetCustomAttribute(ah);
            if (!IsAttributeType(md, ca, "System.Runtime.CompilerServices", "DecimalConstantAttribute"))
                continue;
            try
            {
                var br = md.GetBlobReader(ca.Value);
                if (br.Length >= 16 && br.ReadUInt16() == 0x0001)
                {
                    var scale = br.ReadByte();
                    var sign = br.ReadByte();
                    var hi = br.ReadInt32();
                    var mid = br.ReadInt32();
                    var low = br.ReadInt32();
                    if (scale <= 28)
                    {
                        var d = new decimal(low, mid, hi, sign != 0, scale);
                        literal = d.ToString(CultureInfo.InvariantCulture) + "m";
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    public static byte? ReadNullableContext(MetadataReader md, CustomAttributeHandleCollection attrs)
    {
        foreach (var ah in attrs)
        {
            var ca = md.GetCustomAttribute(ah);
            if (!IsAttributeType(md, ca, "System.Runtime.CompilerServices", "NullableContextAttribute"))
                continue;
            try
            {
                var br = md.GetBlobReader(ca.Value);
                if (br.Length >= 3 && br.ReadUInt16() == 0x0001)
                    return br.ReadByte();
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public static byte? ReadTopLevelNullableFlag(MetadataReader md, CustomAttributeHandleCollection attrs)
    {
        foreach (var ah in attrs)
        {
            var ca = md.GetCustomAttribute(ah);
            if (!IsAttributeType(md, ca, "System.Runtime.CompilerServices", "NullableAttribute"))
                continue;
            try
            {
                var br = md.GetBlobReader(ca.Value);
                if (br.Length < 5 || br.ReadUInt16() != 0x0001)
                    return null;

                // NullableAttribute(byte) has fixed 5-byte blob: 01 00 <flag> 00 00
                if (br.Length == 5)
                    return br.ReadByte();

                // NullableAttribute(byte[]) has 01 00 <int32 len> <b0..> <uint16 numNamed>
                if (br.Length >= 8)
                {
                    var arrLen = br.ReadInt32();
                    if (arrLen > 0 && br.RemainingBytes >= 1)
                        return br.ReadByte();
                }
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public static string ApplyTopLevelNullability(
        string displayType,
        byte? attrFlag,
        byte contextFlag,
        ReferenceTypeLookup refTypes,
        GenericContext? genericCtx = null)
    {
        if (string.IsNullOrEmpty(displayType) ||
            displayType == "void" ||
            displayType.EndsWith('?') ||
            displayType.EndsWith('*'))
        {
            return displayType;
        }

        // Handle ByRef suffix '&' cleanly so `string&` -> `string?&` before `ref`/`out` extraction
        var hasAmp = displayType.EndsWith('&');
        var core = hasAmp ? displayType[..^1] : displayType;
        if (core.EndsWith('?') || core.Contains('<'))
            return displayType;

        bool isNonStructGenericParam = genericCtx != null
            && TypeNameHelper.IsGenericParamName(core, genericCtx)
            && (genericCtx.ValueTypeGenericParams == null || !genericCtx.ValueTypeGenericParams.Contains(core));

        var isNullable = attrFlag.HasValue
            ? attrFlag.Value == 2
            : (contextFlag == 2 && (refTypes.IsConfirmedReferenceType(core) || isNonStructGenericParam));

        if (!isNullable) return displayType;
        return hasAmp ? core + "?&" : core + "?";
    }

    public static string? ReadDefaultMemberName(MetadataReader md, TypeDefinition td)
    {
        foreach (var ah in td.GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(ah);
            if (!IsAttributeType(md, ca, "System.Reflection", "DefaultMemberAttribute"))
                continue;
            try
            {
                var br = md.GetBlobReader(ca.Value);
                if (br.Length >= 4 && br.ReadUInt16() == 0x0001)
                    return br.ReadSerializedString();
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public static bool HasAttribute(
        MetadataReader md,
        CustomAttributeHandleCollection attrs,
        string targetNs,
        string targetName)
    {
        foreach (var ah in attrs)
        {
            if (IsAttributeType(md, md.GetCustomAttribute(ah), targetNs, targetName))
                return true;
        }
        return false;
    }

    public static bool IsAttributeType(MetadataReader md, CustomAttribute ca, string targetNs, string targetName)
    {
        EntityHandle typeHandle = default;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            typeHandle = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            typeHandle = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType();

        if (typeHandle.Kind == HandleKind.TypeReference)
        {
            var tr = md.GetTypeReference((TypeReferenceHandle)typeHandle);
            return md.GetString(tr.Name) == targetName && md.GetString(tr.Namespace) == targetNs;
        }
        if (typeHandle.Kind == HandleKind.TypeDefinition)
        {
            var td = md.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
            return md.GetString(td.Name) == targetName && md.GetString(td.Namespace) == targetNs;
        }
        return false;
    }

    private static string EscapeCharLiteral(char c) => c switch
    {
        '\'' => "'\\''",
        '\\' => "'\\\\'",
        '\0' => "'\\0'",
        '\n' => "'\\n'",
        '\r' => "'\\r'",
        '\t' => "'\\t'",
        _ => $"'{c}'",
    };

    private static string EscapeStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\0': sb.Append("\\0"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
