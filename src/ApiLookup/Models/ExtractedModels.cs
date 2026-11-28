namespace ApiLookup;

/// <summary>Normalized interface reference implemented by a type.</summary>
/// <param name="FullName">Namespace-qualified name with generic arguments stripped (e.g. <c>System.Collections.Generic.IList</c>).</param>
/// <param name="ClrName">Exact CLR name of the interface definition (e.g. <c>System.Collections.Generic.IList`1</c>).</param>
public sealed record InterfaceRef(string FullName, string SimpleName, string ClrName);

/// <summary>Normalized type reference appearing in a member's return type or parameters.</summary>
public sealed record MemberTypeRef(string TypeFull, string TypeSimple, string Role);

/// <summary>Normalized target type (or generic constraint target) for an extension method.</summary>
public sealed record ExtensionTargetRef(string TargetFull, string TargetSimple);

/// <summary>Metadata and XML documentation extracted from a single managed assembly.</summary>
public sealed record ExtractedAssembly(
    string Name,
    string Path,
    string Version,
    string? XmlPath,
    long FileSize,
    long MtimeUtc,
    IReadOnlyList<ExtractedType> Types);

/// <summary>Metadata and XML documentation extracted for a CLR type.</summary>
/// <param name="BaseClr">Exact CLR name of the base type definition (e.g. <c>Ns.Base`1</c>), used for arity-safe resolution.</param>
/// <param name="InheritDoc">
/// <c>null</c> when the type has its own docs (or none); otherwise the type's XML doc used <c>&lt;inheritdoc/&gt;</c>:
/// empty string for an implicit target, or the <c>cref</c> doc ID. Resolved after all assemblies are indexed.
/// </param>
public sealed record ExtractedType(
    string FullName,
    string BareFullName,
    string ClrName,
    string Namespace,
    string Name,
    string SimpleName,
    string Kind,
    string Accessibility,
    string Modifiers,
    string? BaseType,
    string? BaseBare,
    string? BaseSimple,
    string? BaseClr,
    string Interfaces,
    IReadOnlyList<InterfaceRef> InterfaceRefs,
    string GenericParams,
    string GenericConstraints,
    string? DelegateSignature,
    bool IsExtensionContainer,
    string? Summary,
    string? Remarks,
    string DocId,
    string? InheritDoc,
    IReadOnlyList<ExtractedMember> Members);

/// <summary>Metadata and XML documentation extracted for a CLR member.</summary>
/// <param name="InheritDoc">See <see cref="ExtractedType.InheritDoc"/>.</param>
public sealed record ExtractedMember(
    string Kind,
    string Name,
    string Accessibility,
    string Modifiers,
    string ReturnType,
    string ReturnSimple,
    string Signature,
    string ParamTypes,
    IReadOnlyList<MemberTypeRef> TypeRefs,
    string? ExtTargetType,
    string? ExtTargetSimple,
    IReadOnlyList<ExtensionTargetRef> ExtensionTargets,
    string? EnumValue,
    string? Summary,
    string? ParamsDoc,
    string? ReturnsDoc,
    string DocId,
    string? InheritDoc = null);
