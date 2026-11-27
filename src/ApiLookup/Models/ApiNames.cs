namespace ApiLookup;

/// <summary>
/// CLR base types that every class/struct/enum/delegate inherits implicitly. They are never printed as an
/// explicit base in a declaration and never walked when building inheritance chains.
/// </summary>
internal static class WellKnownTypes
{
    public const string Object = "System.Object";
    public const string ValueType = "System.ValueType";
    public const string Enum = "System.Enum";
    public const string MulticastDelegate = "System.MulticastDelegate";
    public const string Delegate = "System.Delegate";

    public static bool IsImplicitBase(string? bareFullName)
        => bareFullName is Object or ValueType or Enum or MulticastDelegate or Delegate;

    public static bool IsDelegateBase(string? fullName)
        => fullName is MulticastDelegate or Delegate;
}

/// <summary>Values of the FTS <c>entity_type</c> column and <see cref="SearchHit.EntityType"/>.</summary>
internal static class EntityTypes
{
    public const string Type = "type";
    public const string Member = "member";
}

/// <summary>Values of <c>types.kind</c> / <see cref="TypeRow.Kind"/>.</summary>
internal static class TypeKinds
{
    public const string Class = "class";
    public const string Struct = "struct";
    public const string Interface = "interface";
    public const string Enum = "enum";
    public const string Delegate = "delegate";

    public static bool IsInterface(string kind) => kind.Equals(Interface, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Values of <c>members.kind</c> / <see cref="MemberRow.Kind"/>.</summary>
internal static class MemberKinds
{
    public const string Constructor = "ctor";
    public const string Method = "method";
    public const string Property = "property";
    public const string Field = "field";
    public const string Event = "event";
    public const string EnumValue = "enum";
}

/// <summary>Values of <c>member_type_refs.role</c> and the <c>--role</c> option.</summary>
internal static class TypeRefRoles
{
    public const string Return = "return";
    public const string Param = "param";
    public const string Both = "both";
}

/// <summary>C# accessibility keywords stored for types and members.</summary>
internal static class Accessibilities
{
    public const string Public = "public";
    public const string Protected = "protected";
    public const string Internal = "internal";
    public const string Private = "private";

    /// <summary>True for members visible to external consumers (indexed by default).</summary>
    public static bool IsPublicApi(string? accessibility) => accessibility is Public or Protected;
}
