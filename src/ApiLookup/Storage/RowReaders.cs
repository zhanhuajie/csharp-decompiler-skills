using Microsoft.Data.Sqlite;

namespace ApiLookup;

/// <summary>
/// Maps <c>types</c> rows (joined with <c>assemblies</c>) to <see cref="TypeRow"/>. Columns are resolved by alias
/// name once per reader, so reordering the SELECT list can never silently shift values between fields.
/// </summary>
internal sealed class TypeRowReader
{
    public const string Columns = @"
SELECT t.id AS t_id, a.name AS t_asm_name, a.path AS t_asm_path, a.xml_path AS t_xml_path,
       t.full_name AS t_full_name, t.bare_full_name AS t_bare_full_name, t.clr_name AS t_clr_name,
       t.namespace AS t_namespace, t.name AS t_name, t.simple_name AS t_simple_name, t.kind AS t_kind,
       t.accessibility AS t_accessibility, t.modifiers AS t_modifiers, t.base_type AS t_base_type,
       t.base_simple AS t_base_simple, t.interfaces AS t_interfaces, t.generic_params AS t_generic_params,
       t.generic_constraints AS t_generic_constraints, t.delegate_signature AS t_delegate_signature,
       t.summary AS t_summary, t.remarks AS t_remarks";

    public const string From = @"
FROM types t
JOIN assemblies a ON a.id = t.assembly_id";

    /// <summary>Full <c>SELECT ... FROM types JOIN assemblies</c> prefix; append JOIN/WHERE/ORDER clauses.</summary>
    public const string Select = Columns + From;

    private readonly int _id, _asmName, _asmPath, _xmlPath, _fullName, _bareFullName, _clrName, _namespace, _name,
        _simpleName, _kind, _accessibility, _modifiers, _baseType, _baseSimple, _interfaces, _genericParams,
        _genericConstraints, _delegateSignature, _summary, _remarks;

    public TypeRowReader(SqliteDataReader r)
    {
        _id = r.GetOrdinal("t_id");
        _asmName = r.GetOrdinal("t_asm_name");
        _asmPath = r.GetOrdinal("t_asm_path");
        _xmlPath = r.GetOrdinal("t_xml_path");
        _fullName = r.GetOrdinal("t_full_name");
        _bareFullName = r.GetOrdinal("t_bare_full_name");
        _clrName = r.GetOrdinal("t_clr_name");
        _namespace = r.GetOrdinal("t_namespace");
        _name = r.GetOrdinal("t_name");
        _simpleName = r.GetOrdinal("t_simple_name");
        _kind = r.GetOrdinal("t_kind");
        _accessibility = r.GetOrdinal("t_accessibility");
        _modifiers = r.GetOrdinal("t_modifiers");
        _baseType = r.GetOrdinal("t_base_type");
        _baseSimple = r.GetOrdinal("t_base_simple");
        _interfaces = r.GetOrdinal("t_interfaces");
        _genericParams = r.GetOrdinal("t_generic_params");
        _genericConstraints = r.GetOrdinal("t_generic_constraints");
        _delegateSignature = r.GetOrdinal("t_delegate_signature");
        _summary = r.GetOrdinal("t_summary");
        _remarks = r.GetOrdinal("t_remarks");
    }

    public TypeRow Read(SqliteDataReader r) => new(
        Id: r.GetInt64(_id),
        AssemblyName: r.GetString(_asmName),
        AssemblyPath: r.GetString(_asmPath),
        XmlPath: r.GetNullableString(_xmlPath),
        FullName: r.GetString(_fullName),
        BareFullName: r.GetString(_bareFullName),
        ClrName: r.GetString(_clrName),
        Namespace: r.GetString(_namespace),
        Name: r.GetString(_name),
        SimpleName: r.GetString(_simpleName),
        Kind: r.GetString(_kind),
        Accessibility: r.GetString(_accessibility),
        Modifiers: r.GetString(_modifiers),
        BaseType: r.GetNullableString(_baseType),
        BaseSimple: r.GetNullableString(_baseSimple),
        Interfaces: r.GetString(_interfaces),
        GenericParams: r.GetString(_genericParams),
        GenericConstraints: r.GetString(_genericConstraints),
        DelegateSignature: r.GetNullableString(_delegateSignature),
        Summary: r.GetNullableString(_summary),
        Remarks: r.GetNullableString(_remarks));

    public static List<TypeRow> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var reader = new TypeRowReader(r);
        var list = new List<TypeRow>();
        while (r.Read()) list.Add(reader.Read(r));
        return list;
    }
}

/// <summary>Maps <c>members</c> rows (joined with declaring type and assembly) to <see cref="MemberRow"/>.</summary>
internal sealed class MemberRowReader
{
    public const string Columns = @"
SELECT m.id AS m_id, m.type_id AS m_type_id, t.full_name AS m_decl_full_name, t.simple_name AS m_decl_simple_name,
       a.name AS m_asm_name, a.path AS m_asm_path, m.kind AS m_kind, m.name AS m_name,
       m.accessibility AS m_accessibility, m.modifiers AS m_modifiers, m.return_type AS m_return_type,
       m.signature AS m_signature, m.ext_target_type AS m_ext_target_type, m.enum_value AS m_enum_value,
       m.summary AS m_summary, m.params_doc AS m_params_doc, m.returns_doc AS m_returns_doc";

    public const string From = @"
FROM members m
JOIN types t ON t.id = m.type_id
JOIN assemblies a ON a.id = m.assembly_id";

    /// <summary>Full <c>SELECT ... FROM members JOIN types JOIN assemblies</c> prefix.</summary>
    public const string Select = Columns + From;

    private readonly int _id, _typeId, _declFullName, _declSimpleName, _asmName, _asmPath, _kind, _name,
        _accessibility, _modifiers, _returnType, _signature, _extTargetType, _enumValue, _summary, _paramsDoc, _returnsDoc;

    public MemberRowReader(SqliteDataReader r)
    {
        _id = r.GetOrdinal("m_id");
        _typeId = r.GetOrdinal("m_type_id");
        _declFullName = r.GetOrdinal("m_decl_full_name");
        _declSimpleName = r.GetOrdinal("m_decl_simple_name");
        _asmName = r.GetOrdinal("m_asm_name");
        _asmPath = r.GetOrdinal("m_asm_path");
        _kind = r.GetOrdinal("m_kind");
        _name = r.GetOrdinal("m_name");
        _accessibility = r.GetOrdinal("m_accessibility");
        _modifiers = r.GetOrdinal("m_modifiers");
        _returnType = r.GetOrdinal("m_return_type");
        _signature = r.GetOrdinal("m_signature");
        _extTargetType = r.GetOrdinal("m_ext_target_type");
        _enumValue = r.GetOrdinal("m_enum_value");
        _summary = r.GetOrdinal("m_summary");
        _paramsDoc = r.GetOrdinal("m_params_doc");
        _returnsDoc = r.GetOrdinal("m_returns_doc");
    }

    public MemberRow Read(SqliteDataReader r) => new(
        Id: r.GetInt64(_id),
        TypeId: r.GetInt64(_typeId),
        DeclaringFullName: r.GetString(_declFullName),
        DeclaringSimpleName: r.GetString(_declSimpleName),
        AssemblyName: r.GetString(_asmName),
        AssemblyPath: r.GetString(_asmPath),
        Kind: r.GetString(_kind),
        Name: r.GetString(_name),
        Accessibility: r.GetString(_accessibility),
        Modifiers: r.GetString(_modifiers),
        ReturnType: r.GetString(_returnType),
        Signature: r.GetString(_signature),
        ExtTargetType: r.GetNullableString(_extTargetType),
        EnumValue: r.GetNullableString(_enumValue),
        Summary: r.GetNullableString(_summary),
        ParamsDoc: r.GetNullableString(_paramsDoc),
        ReturnsDoc: r.GetNullableString(_returnsDoc));

    public static List<MemberRow> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var reader = new MemberRowReader(r);
        var list = new List<MemberRow>();
        while (r.Read()) list.Add(reader.Read(r));
        return list;
    }
}

internal static class SqliteReaderExtensions
{
    public static string? GetNullableString(this SqliteDataReader r, int ordinal)
        => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
}
