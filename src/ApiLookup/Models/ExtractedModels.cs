namespace ApiLookup;

/// <summary>Normalized interface reference implemented by a type.</summary>
public sealed record InterfaceRef(string FullName, string SimpleName);

/// <summary>Normalized type reference appearing in a member's return type or parameters.</summary>
public sealed record MemberTypeRef(string TypeSimple, string Role);

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
    string Interfaces,
    IReadOnlyList<InterfaceRef> InterfaceRefs,
    string GenericParams,
    string GenericConstraints,
    string? DelegateSignature,
    bool IsExtensionContainer,
    string? Summary,
    string? Remarks,
    string DocId,
    IReadOnlyList<ExtractedMember> Members);

/// <summary>Metadata and XML documentation extracted for a CLR member.</summary>
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
    string? EnumValue,
    string? Summary,
    string? ParamsDoc,
    string? ReturnsDoc,
    string DocId);
