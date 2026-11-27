using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ApiLookup;

public static class MetadataExtractor
{
    public static ExtractedAssembly? Extract(string assemblyPath, bool includeNonPublic = false, List<string>? warnings = null)
    {
        if (!File.Exists(assemblyPath)) return null;
        var fi = new FileInfo(assemblyPath);
        var xmlPath = XmlDocReader.FindXmlDocPath(assemblyPath);
        return ExtractCore(Path.GetFullPath(assemblyPath), xmlPath, fi.Length, fi.LastWriteTimeUtc.Ticks, includeNonPublic, warnings);
    }

    public static ExtractedAssembly? Extract(ResolvedAssembly resolved, bool includeNonPublic = false, List<string>? warnings = null)
    {
        if (!File.Exists(resolved.Path)) return null;
        var size = resolved.FileSize;
        var mtime = resolved.MtimeUtc;
        if (size == 0 && mtime == 0)
        {
            var fi = new FileInfo(resolved.Path);
            size = fi.Length;
            mtime = fi.LastWriteTimeUtc.Ticks;
        }
        return ExtractCore(resolved.Path, resolved.XmlPath, size, mtime, includeNonPublic, warnings);
    }

    private static ExtractedAssembly? ExtractCore(
        string fullPath,
        string? xmlPath,
        long fileSize,
        long mtimeUtc,
        bool includeNonPublic,
        List<string>? warnings)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var peReader = new PEReader(fs);
            if (!peReader.HasMetadata) return null;

            var md = peReader.GetMetadataReader();
            if (!md.IsAssembly) return null;

            var asmDef = md.GetAssemblyDefinition();
            var asmName = md.GetString(asmDef.Name);
            var asmVersion = asmDef.Version.ToString();
            var xmlDocs = XmlDocReader.Load(xmlPath, warnings);
            var (enumLookup, refLookup) = ConstantAndAttributeReader.ScanAssemblyTypes(md, includeNonPublic);

            var types = new List<ExtractedType>();
            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                var rawName = md.GetString(td.Name);
                if (ShouldSkipType(md, td, rawName, includeNonPublic))
                    continue;

                var extracted = ExtractType(md, td, xmlDocs, enumLookup, refLookup, includeNonPublic);
                if (extracted != null)
                    types.Add(extracted);
            }

            if (types.Count == 0 && md.ExportedTypes.Count > 0)
            {
                var forwarders = md.ExportedTypes.Count(h => md.GetExportedType(h).IsForwarder);
                if (forwarders > 0)
                    warnings?.Add($"Assembly '{asmName}' ({fullPath}) is a type-forwarding facade ({forwarders} forwarded types, 0 local definitions). Use 'locate' or index its target implementation assembly.");
            }

            return new ExtractedAssembly(
                asmName,
                fullPath,
                asmVersion,
                xmlPath,
                fileSize,
                mtimeUtc,
                types);
        }
        catch (Exception ex)
        {
            warnings?.Add($"Failed to extract metadata from '{fullPath}': {ex.Message}");
            return null;
        }
    }

    internal static bool ShouldSkipType(MetadataReader md, TypeDefinition td, string rawName, bool includeNonPublic)
    {
        if (rawName == "<Module>" || rawName.StartsWith('<') || rawName.Contains("<>"))
            return true;
        if (rawName.StartsWith("$ArrayType$", StringComparison.Ordinal) ||
            rawName.StartsWith("__", StringComparison.Ordinal))
            return true;

        var visibility = GetEffectiveVisibility(md, td);
        if (!includeNonPublic && visibility is not ("public" or "protected"))
            return true;

        return false;
    }

    internal static string GetEffectiveVisibility(MetadataReader md, TypeDefinition td)
    {
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        if (!td.IsNested)
            return vis == TypeAttributes.Public ? "public" : "internal";

        var parentVis = GetEffectiveVisibility(md, md.GetTypeDefinition(td.GetDeclaringType()));
        if (parentVis is "internal" or "private") return parentVis;

        return vis switch
        {
            TypeAttributes.NestedPublic => "public",
            TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => "protected",
            TypeAttributes.NestedAssembly or TypeAttributes.NestedFamANDAssem => "internal",
            _ => "private",
        };
    }

    internal static (string ns, string clrName, string displayFull, string bareFull, string displayName, string simpleName)
        BuildTypeNames(MetadataReader md, TypeDefinition td)
        => TypeNameHelper.BuildTypeNames(md, td);

    public static string StripGenerics(string fullName) => TypeNameHelper.StripGenerics(fullName);
    public static string ExtractSimpleTypeName(string typeName) => TypeNameHelper.ExtractSimpleTypeName(typeName);
    public static List<string> ExtractReferencedSimpleTypeNames(string displayType, GenericContext? ctx = null)
        => TypeNameHelper.ExtractReferencedSimpleTypeNames(displayType, ctx);

    private static ExtractedType? ExtractType(
        MetadataReader md,
        TypeDefinition td,
        XmlDocBook xmlDocs,
        EnumLookup enumLookup,
        ReferenceTypeLookup refLookup,
        bool includeNonPublic)
    {
        var (ns, clrName, displayFullName, bareFullName, displayName, simpleName) = TypeNameHelper.BuildTypeNames(md, td);

        var genericParamNames = TypeNameHelper.ReadGenericParamNames(md, td.GetGenericParameters());
        var valueGenericParams = TypeNameHelper.ReadValueTypeGenericParams(md, td.GetGenericParameters());
        var genericCtx = new GenericContext(genericParamNames, Array.Empty<string>(), valueGenericParams);

        var (baseFull, baseBare, baseSimple) = ResolveBaseType(md, td, genericCtx);
        var kind = DetermineTypeKind(td, baseFull);
        var visibility = GetEffectiveVisibility(md, td);
        var modifiers = DetermineTypeModifiers(md, td, kind);
        var (interfaces, interfaceRefs) = ResolveInterfaces(md, td, genericCtx);
        var genericConstraints = ReadGenericConstraints(md, td.GetGenericParameters(), genericCtx);
        var isExtContainer = ConstantAndAttributeReader.HasAttribute(md, td.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute");
        var defaultMemberName = ConstantAndAttributeReader.ReadDefaultMemberName(md, td) ?? "Item";
        var typeNullCtx = ResolveInheritedNullableContext(md, td);

        var docId = "T:" + clrName.Replace('+', '.');
        var typeDoc = xmlDocs.Find(docId);

        var delegateSignature = kind == "delegate"
            ? ExtractDelegateSignature(md, td, visibility, displayName, genericConstraints, genericCtx, typeNullCtx, enumLookup, refLookup)
            : null;

        var members = ExtractMembers(
            md, td, bareFullName, clrName, simpleName, kind, genericCtx, isExtContainer,
            defaultMemberName, typeNullCtx, xmlDocs, enumLookup, refLookup, includeNonPublic);

        return new ExtractedType(
            FullName: displayFullName,
            BareFullName: bareFullName,
            ClrName: clrName,
            Namespace: ns,
            Name: displayName,
            SimpleName: simpleName,
            Kind: kind,
            Accessibility: visibility,
            Modifiers: modifiers,
            BaseType: baseFull,
            BaseBare: baseBare,
            BaseSimple: baseSimple,
            Interfaces: string.Join(", ", interfaces),
            InterfaceRefs: interfaceRefs,
            GenericParams: string.Join(", ", genericParamNames),
            GenericConstraints: genericConstraints,
            DelegateSignature: delegateSignature,
            IsExtensionContainer: isExtContainer,
            Summary: typeDoc?.Summary,
            Remarks: typeDoc?.Remarks,
            DocId: docId,
            Members: members);
    }

    private static byte ResolveInheritedNullableContext(MetadataReader md, TypeDefinition td)
    {
        var cur = td;
        while (true)
        {
            var ctx = ConstantAndAttributeReader.ReadNullableContext(md, cur.GetCustomAttributes());
            if (ctx.HasValue) return ctx.Value;
            if (!cur.IsNested) return 0;
            cur = md.GetTypeDefinition(cur.GetDeclaringType());
        }
    }

    private static string ReadGenericConstraints(MetadataReader md, GenericParameterHandleCollection handles, GenericContext ctx)
    {
        if (handles.Count == 0) return string.Empty;

        var parts = new List<string>();
        foreach (var h in handles)
        {
            var gp = md.GetGenericParameter(h);
            var gpName = md.GetString(gp.Name);
            var attrs = gp.Attributes;
            var isStruct = (attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;

            var clauses = new List<string>();
            if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                clauses.Add("class");
            if (isStruct)
                clauses.Add("struct");

            foreach (var ch in gp.GetConstraints())
            {
                var constraint = md.GetGenericParameterConstraint(ch);
                var typeName = TypeNameHelper.ResolveEntityDisplayName(md, constraint.Type, ctx);
                if (string.IsNullOrEmpty(typeName)) continue;
                if (isStruct && typeName is "ValueType" or "System.ValueType") continue;
                clauses.Add(typeName);
            }

            if (!isStruct && (attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                clauses.Add("new()");

            if (clauses.Count > 0)
                parts.Add($"where {gpName} : {string.Join(", ", clauses)}");
        }

        return string.Join(" ", parts);
    }

    private static string? ExtractDelegateSignature(
        MetadataReader md,
        TypeDefinition td,
        string visibility,
        string displayName,
        string genericConstraints,
        GenericContext genericCtx,
        byte typeNullCtx,
        EnumLookup enumLookup,
        ReferenceTypeLookup refLookup)
    {
        foreach (var mh in td.GetMethods())
        {
            var method = md.GetMethodDefinition(mh);
            if (md.GetString(method.Name) != "Invoke") continue;

            MethodSignature<string> sig;
            try { sig = method.DecodeSignature(DisplaySignatureProvider.Instance, genericCtx); }
            catch { return null; }

            var methodNullCtx = ConstantAndAttributeReader.ReadNullableContext(md, method.GetCustomAttributes()) ?? typeNullCtx;
            var (retInfo, paramInfos) = ReadParamMetadata(md, method);

            var retRaw = ConstantAndAttributeReader.ApplyTopLevelNullability(sig.ReturnType, retInfo?.NullFlag, methodNullCtx, refLookup, genericCtx);
            var retText = FormatReturnRefPrefix(retRaw, retInfo);

            var sb = new StringBuilder();
            sb.Append(visibility).Append(" delegate ").Append(retText).Append(' ').Append(displayName).Append('(');
            for (var i = 0; i < sig.ParameterTypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                paramInfos.TryGetValue(i + 1, out var pi);
                var rawPt = ConstantAndAttributeReader.ApplyTopLevelNullability(sig.ParameterTypes[i], pi?.NullFlag, methodNullCtx, refLookup, genericCtx);

                if (pi?.IsParams == true) sb.Append("params ");
                var cleanPt = FormatByRefPrefix(rawPt, pi, sb);
                sb.Append(cleanPt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
                if (pi?.DefaultValue != null)
                {
                    var defText = ConstantAndAttributeReader.FormatDefaultValue(cleanPt, pi.DefaultValue, pi.IsIntegerConst, enumLookup, genericCtx);
                    sb.Append(" = ").Append(defText);
                }
            }
            sb.Append(')');
            if (!string.IsNullOrEmpty(genericConstraints))
                sb.Append(' ').Append(genericConstraints);
            return sb.ToString();
        }
        return null;
    }

    private static (string? full, string? bare, string? simple) ResolveBaseType(MetadataReader md, TypeDefinition td, GenericContext ctx)
    {
        if (td.BaseType.IsNil) return (null, null, null);
        var full = TypeNameHelper.ResolveEntityFullName(md, td.BaseType, ctx);
        if (string.IsNullOrEmpty(full)) return (null, null, null);
        var bare = TypeNameHelper.StripGenerics(full);
        var simple = TypeNameHelper.ExtractSimpleTypeName(bare);
        return (full, bare, simple);
    }

    private static (List<string> DisplayNames, List<InterfaceRef> Refs) ResolveInterfaces(
        MetadataReader md,
        TypeDefinition td,
        GenericContext ctx)
    {
        var displayNames = new List<string>();
        var refs = new List<InterfaceRef>();
        var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ih in td.GetInterfaceImplementations())
        {
            var impl = md.GetInterfaceImplementation(ih);
            var disp = TypeNameHelper.ResolveEntityDisplayName(md, impl.Interface, ctx);
            if (!string.IsNullOrEmpty(disp))
                displayNames.Add(disp);

            var full = TypeNameHelper.ResolveEntityFullName(md, impl.Interface, ctx);
            if (!string.IsNullOrEmpty(full))
            {
                var bare = TypeNameHelper.StripGenerics(full);
                var simple = TypeNameHelper.ExtractSimpleTypeName(bare);
                if (!string.IsNullOrEmpty(bare) && seenRefs.Add(bare))
                    refs.Add(new InterfaceRef(bare, simple));
            }
        }
        return (displayNames, refs);
    }

    private static string DetermineTypeKind(TypeDefinition td, string? baseFull)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0) return "interface";
        if (baseFull == "System.Enum") return "enum";
        if (baseFull == "System.ValueType") return "struct";
        if (baseFull is "System.MulticastDelegate" or "System.Delegate") return "delegate";
        return "class";
    }

    private static string DetermineTypeModifiers(MetadataReader md, TypeDefinition td, string kind)
    {
        if (kind == "struct")
        {
            var attrs = td.GetCustomAttributes();
            var isReadOnly = ConstantAndAttributeReader.HasAttribute(md, attrs, "System.Runtime.CompilerServices", "IsReadOnlyAttribute");
            var isByRefLike = ConstantAndAttributeReader.HasAttribute(md, attrs, "System.Runtime.CompilerServices", "IsByRefLikeAttribute");
            if (isReadOnly && isByRefLike) return "readonly ref";
            if (isReadOnly) return "readonly";
            if (isByRefLike) return "ref";
            return string.Empty;
        }

        if (kind != "class") return string.Empty;
        var isAbs = (td.Attributes & TypeAttributes.Abstract) != 0;
        var isSealed = (td.Attributes & TypeAttributes.Sealed) != 0;
        if (isAbs && isSealed) return "static";
        if (isAbs) return "abstract";
        if (isSealed) return "sealed";
        return string.Empty;
    }

    private static List<ExtractedMember> ExtractMembers(
        MetadataReader md,
        TypeDefinition td,
        string bareTypeFullName,
        string clrTypeName,
        string simpleTypeName,
        string typeKind,
        GenericContext typeGenericCtx,
        bool isExtContainer,
        string defaultMemberName,
        byte typeNullCtx,
        XmlDocBook xmlDocs,
        EnumLookup enumLookup,
        ReferenceTypeLookup refLookup,
        bool includeNonPublic)
    {
        var members = new List<ExtractedMember>();
        var docTypePrefix = clrTypeName.Replace('+', '.');
        var isInterfaceType = typeKind == "interface";

        var accessorMethods = new HashSet<MethodDefinitionHandle>();

        foreach (var ph in td.GetProperties())
        {
            var prop = md.GetPropertyDefinition(ph);
            var accessors = prop.GetAccessors();
            if (!accessors.Getter.IsNil) accessorMethods.Add(accessors.Getter);
            if (!accessors.Setter.IsNil) accessorMethods.Add(accessors.Setter);

            var extracted = ExtractProperty(
                md, prop, accessors, docTypePrefix, typeGenericCtx, isInterfaceType, defaultMemberName, typeNullCtx, xmlDocs, enumLookup, refLookup, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        foreach (var eh in td.GetEvents())
        {
            var ev = md.GetEventDefinition(eh);
            var accessors = ev.GetAccessors();
            if (!accessors.Adder.IsNil) accessorMethods.Add(accessors.Adder);
            if (!accessors.Remover.IsNil) accessorMethods.Add(accessors.Remover);
            if (!accessors.Raiser.IsNil) accessorMethods.Add(accessors.Raiser);

            var extracted = ExtractEvent(
                md, ev, accessors, docTypePrefix, typeGenericCtx, isInterfaceType, typeNullCtx, xmlDocs, refLookup, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        foreach (var fh in td.GetFields())
        {
            var field = md.GetFieldDefinition(fh);
            var extracted = ExtractField(md, field, typeKind, docTypePrefix, typeGenericCtx, typeNullCtx, xmlDocs, refLookup, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        foreach (var mh in td.GetMethods())
        {
            if (accessorMethods.Contains(mh)) continue;
            var method = md.GetMethodDefinition(mh);
            var extracted = ExtractMethod(
                md, method, bareTypeFullName, simpleTypeName, docTypePrefix, typeGenericCtx, isInterfaceType, isExtContainer, typeNullCtx, xmlDocs, enumLookup, refLookup, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        return members;
    }

    private static ExtractedMember? ExtractProperty(
        MetadataReader md,
        PropertyDefinition prop,
        PropertyAccessors accessors,
        string docTypePrefix,
        GenericContext ctx,
        bool isInterfaceType,
        string defaultMemberName,
        byte typeNullCtx,
        XmlDocBook xmlDocs,
        EnumLookup enumLookup,
        ReferenceTypeLookup refLookup,
        bool includeNonPublic)
    {
        MethodDefinition? getter = accessors.Getter.IsNil ? null : md.GetMethodDefinition(accessors.Getter);
        MethodDefinition? setter = accessors.Setter.IsNil ? null : md.GetMethodDefinition(accessors.Setter);

        var getterAcc = getter.HasValue ? GetMethodAccessibility(getter.Value.Attributes) : null;
        var setterAcc = setter.HasValue ? GetMethodAccessibility(setter.Value.Attributes) : null;
        var bestAcc = PickHigherAccessibility(getterAcc, setterAcc);

        if (!includeNonPublic && bestAcc is not ("public" or "protected"))
            return null;

        var name = md.GetString(prop.Name);
        var primaryMethod = getter ?? setter;
        var modifiers = primaryMethod.HasValue ? GetMethodModifiers(primaryMethod.Value.Attributes, isInterfaceType) : string.Empty;

        MethodSignature<string> dispSig;
        MethodSignature<IReadOnlyList<(string FullName, string SimpleName)>> refSig;
        try
        {
            dispSig = prop.DecodeSignature(DisplaySignatureProvider.Instance, ctx);
            refSig = prop.DecodeSignature(TypeRefCollectorProvider.Instance, ctx);
        }
        catch
        {
            return null;
        }

        var needDocSig = xmlDocs.Count > 0 && dispSig.ParameterTypes.Length > 0;
        MethodSignature<string>? docSig = null;
        if (needDocSig)
        {
            try { docSig = prop.DecodeSignature(XmlDocSignatureProvider.Instance, ctx); }
            catch { docSig = null; }
        }

        var (retInfo, paramNames) = primaryMethod.HasValue
            ? ReadParamMetadata(md, primaryMethod.Value)
            : (null, new Dictionary<int, ParamInfo>());

        var propNullCtx = (primaryMethod.HasValue ? ConstantAndAttributeReader.ReadNullableContext(md, primaryMethod.Value.GetCustomAttributes()) : null) ?? typeNullCtx;
        var propNullFlag = ConstantAndAttributeReader.ReadTopLevelNullableFlag(md, prop.GetCustomAttributes()) ?? retInfo?.NullFlag;
        var rawRet = ConstantAndAttributeReader.ApplyTopLevelNullability(dispSig.ReturnType, propNullFlag, propNullCtx, refLookup, ctx);
        var returnType = FormatReturnRefPrefix(rawRet, retInfo);
        var returnSimple = TypeNameHelper.ExtractSimpleTypeName(dispSig.ReturnType);

        var isInit = setter.HasValue && IsInitOnlySetter(md, setter.Value);
        var accessorText = BuildPropertyAccessors(getterAcc, setterAcc, bestAcc, isInit);

        var sb = new StringBuilder();
        sb.Append(bestAcc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append(returnType).Append(' ');

        var paramSimpleList = new List<string>();
        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeFull, string Role)>(TypeRefComparer.Instance);
        AddCollectedRefs(typeRefs, seenRefs, refSig.ReturnType, "return");

        string docId;
        if (dispSig.ParameterTypes.Length > 0)
        {
            var propDisplayName = string.Equals(name, defaultMemberName, StringComparison.Ordinal) ? "this" : name;
            sb.Append(propDisplayName).Append('[');
            for (var i = 0; i < dispSig.ParameterTypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                paramNames.TryGetValue(i + 1, out var pi);
                var pt = ConstantAndAttributeReader.ApplyTopLevelNullability(dispSig.ParameterTypes[i], pi?.NullFlag, propNullCtx, refLookup, ctx);
                if (pi?.IsParams == true) sb.Append("params ");
                var cleanPt = FormatByRefPrefix(pt, pi, sb);
                paramSimpleList.Add(TypeNameHelper.ExtractSimpleTypeName(cleanPt));
                AddCollectedRefs(typeRefs, seenRefs, refSig.ParameterTypes[i], "param");
                sb.Append(cleanPt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
                if (pi?.DefaultValue != null)
                {
                    var defText = ConstantAndAttributeReader.FormatDefaultValue(cleanPt, pi.DefaultValue, pi.IsIntegerConst, enumLookup, ctx);
                    sb.Append(" = ").Append(defText);
                }
            }
            sb.Append(']');
            docId = docSig.HasValue
                ? $"P:{docTypePrefix}.{name}({string.Join(",", docSig.Value.ParameterTypes)})"
                : $"P:{docTypePrefix}.{name}";
        }
        else
        {
            sb.Append(name);
            docId = $"P:{docTypePrefix}.{name}";
        }

        sb.Append(" { ").Append(accessorText).Append(" }");
        var doc = xmlDocs.Find(docId);

        return new ExtractedMember(
            Kind: "property",
            Name: name,
            Accessibility: bestAcc,
            Modifiers: modifiers,
            ReturnType: returnType,
            ReturnSimple: returnSimple,
            Signature: sb.ToString(),
            ParamTypes: string.Join("|", paramSimpleList),
            TypeRefs: typeRefs,
            ExtTargetType: null,
            ExtTargetSimple: null,
            ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: FormatParamsDoc(doc),
            ReturnsDoc: doc?.Returns,
            DocId: docId);
    }

    private static ExtractedMember? ExtractEvent(
        MetadataReader md,
        EventDefinition ev,
        EventAccessors accessors,
        string docTypePrefix,
        GenericContext ctx,
        bool isInterfaceType,
        byte typeNullCtx,
        XmlDocBook xmlDocs,
        ReferenceTypeLookup refLookup,
        bool includeNonPublic)
    {
        MethodDefinition? adder = accessors.Adder.IsNil ? null : md.GetMethodDefinition(accessors.Adder);
        MethodDefinition? remover = accessors.Remover.IsNil ? null : md.GetMethodDefinition(accessors.Remover);
        var adderAcc = adder.HasValue ? GetMethodAccessibility(adder.Value.Attributes) : null;
        var removerAcc = remover.HasValue ? GetMethodAccessibility(remover.Value.Attributes) : null;
        var acc = PickHigherAccessibility(adderAcc, removerAcc);

        if (!includeNonPublic && acc is not ("public" or "protected"))
            return null;

        var name = md.GetString(ev.Name);
        var primary = adder ?? remover;
        var modifiers = primary.HasValue ? GetMethodModifiers(primary.Value.Attributes, isInterfaceType) : string.Empty;
        var rawHandler = ev.Type.IsNil ? "EventHandler" : TypeNameHelper.ResolveEntityDisplayName(md, ev.Type, ctx);
        var evNullFlag = ConstantAndAttributeReader.ReadTopLevelNullableFlag(md, ev.GetCustomAttributes());
        var handlerType = ConstantAndAttributeReader.ApplyTopLevelNullability(rawHandler, evNullFlag, typeNullCtx, refLookup, ctx);
        var handlerSimple = TypeNameHelper.ExtractSimpleTypeName(rawHandler);

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append("event ").Append(handlerType).Append(' ').Append(name);

        var docId = $"E:{docTypePrefix}.{name}";
        var doc = xmlDocs.Find(docId);

        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeFull, string Role)>(TypeRefComparer.Instance);
        var collected = ev.Type.IsNil
            ? new[] { ("System.EventHandler", "EventHandler") }
            : TypeRefCollectorProvider.CollectFromEntity(md, ev.Type, ctx);
        AddCollectedRefs(typeRefs, seenRefs, collected, "return");

        return new ExtractedMember(
            Kind: "event",
            Name: name,
            Accessibility: acc,
            Modifiers: modifiers,
            ReturnType: handlerType,
            ReturnSimple: handlerSimple,
            Signature: sb.ToString(),
            ParamTypes: string.Empty,
            TypeRefs: typeRefs,
            ExtTargetType: null,
            ExtTargetSimple: null,
            ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: null,
            ReturnsDoc: null,
            DocId: docId);
    }

    private static ExtractedMember? ExtractField(
        MetadataReader md,
        FieldDefinition field,
        string typeKind,
        string docTypePrefix,
        GenericContext ctx,
        byte typeNullCtx,
        XmlDocBook xmlDocs,
        ReferenceTypeLookup refLookup,
        bool includeNonPublic)
    {
        var name = md.GetString(field.Name);
        if (name == "value__" || name.StartsWith('<')) return null;

        var acc = GetFieldAccessibility(field.Attributes);
        if (!includeNonPublic && acc is not ("public" or "protected"))
            return null;

        string rawFieldType;
        IReadOnlyList<(string FullName, string SimpleName)> collectedRefs;
        try
        {
            rawFieldType = field.DecodeSignature(DisplaySignatureProvider.Instance, ctx);
            collectedRefs = field.DecodeSignature(TypeRefCollectorProvider.Instance, ctx);
        }
        catch
        {
            return null;
        }

        var fieldAttrs = field.GetCustomAttributes();
        var fieldNullFlag = ConstantAndAttributeReader.ReadTopLevelNullableFlag(md, fieldAttrs);
        var fieldType = ConstantAndAttributeReader.ApplyTopLevelNullability(rawFieldType, fieldNullFlag, typeNullCtx, refLookup, ctx);

        var isLiteral = (field.Attributes & FieldAttributes.Literal) != 0;
        var isStatic = (field.Attributes & FieldAttributes.Static) != 0;
        var isInitOnly = (field.Attributes & FieldAttributes.InitOnly) != 0;

        string? constVal = null;
        var constHandle = field.GetDefaultValue();
        if (!constHandle.IsNil)
        {
            constVal = ConstantAndAttributeReader.ReadConstantValue(md, md.GetConstant(constHandle));
        }
        else if (isStatic && isInitOnly && ConstantAndAttributeReader.TryReadDecimalConstant(md, fieldAttrs, out var decVal))
        {
            constVal = decVal;
            isLiteral = true;
        }

        var docId = $"F:{docTypePrefix}.{name}";
        var doc = xmlDocs.Find(docId);

        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeFull, string Role)>(TypeRefComparer.Instance);
        AddCollectedRefs(typeRefs, seenRefs, collectedRefs, "return");

        if (typeKind == "enum" && isLiteral)
        {
            var sig = constVal != null ? $"{name} = {constVal}" : name;
            return new ExtractedMember(
                Kind: "enum",
                Name: name,
                Accessibility: "public",
                Modifiers: "const",
                ReturnType: fieldType,
                ReturnSimple: TypeNameHelper.ExtractSimpleTypeName(rawFieldType),
                Signature: sig,
                ParamTypes: string.Empty,
                TypeRefs: typeRefs,
                ExtTargetType: null,
                ExtTargetSimple: null,
                ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
                EnumValue: constVal,
                Summary: doc?.Summary,
                ParamsDoc: null,
                ReturnsDoc: null,
                DocId: docId);
        }

        var mods = new List<string>();
        if (isLiteral) mods.Add("const");
        else
        {
            if (isStatic) mods.Add("static");
            if (isInitOnly) mods.Add("readonly");
        }
        var modText = string.Join(" ", mods);

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modText)) sb.Append(modText).Append(' ');
        sb.Append(fieldType).Append(' ').Append(name);
        if (constVal != null && isLiteral) sb.Append(" = ").Append(constVal);

        return new ExtractedMember(
            Kind: "field",
            Name: name,
            Accessibility: acc,
            Modifiers: modText,
            ReturnType: fieldType,
            ReturnSimple: TypeNameHelper.ExtractSimpleTypeName(rawFieldType),
            Signature: sb.ToString(),
            ParamTypes: string.Empty,
            TypeRefs: typeRefs,
            ExtTargetType: null,
            ExtTargetSimple: null,
            ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
            EnumValue: constVal,
            Summary: doc?.Summary,
            ParamsDoc: null,
            ReturnsDoc: null,
            DocId: docId);
    }

    private static ExtractedMember? ExtractMethod(
        MetadataReader md,
        MethodDefinition method,
        string bareTypeFullName,
        string simpleTypeName,
        string docTypePrefix,
        GenericContext typeCtx,
        bool isInterfaceType,
        bool isExtContainer,
        byte typeNullCtx,
        XmlDocBook xmlDocs,
        EnumLookup enumLookup,
        ReferenceTypeLookup refLookup,
        bool includeNonPublic)
    {
        var name = md.GetString(method.Name);
        if (name == ".cctor" || name.StartsWith('<')) return null;

        var isCtor = name == ".ctor";
        var isSpecial = (method.Attributes & MethodAttributes.SpecialName) != 0;
        if (isSpecial && !isCtor && !name.StartsWith("op_", StringComparison.Ordinal))
            return null;

        var acc = GetMethodAccessibility(method.Attributes);
        if (!includeNonPublic && acc is not ("public" or "protected"))
            return null;

        var methodGp = TypeNameHelper.ReadGenericParamNames(md, method.GetGenericParameters());
        var fullCtx = methodGp.Count > 0
            ? typeCtx with
            {
                MethodParams = methodGp,
                ValueTypeGenericParams = TypeNameHelper.ReadValueTypeGenericParams(md, method.GetGenericParameters(), typeCtx.ValueTypeGenericParams),
            }
            : typeCtx;

        MethodSignature<string> dispSig;
        MethodSignature<IReadOnlyList<(string FullName, string SimpleName)>> refSig;
        try
        {
            dispSig = method.DecodeSignature(DisplaySignatureProvider.Instance, fullCtx);
            refSig = method.DecodeSignature(TypeRefCollectorProvider.Instance, fullCtx);
        }
        catch
        {
            return null;
        }

        var methodAttrs = method.GetCustomAttributes();
        var methodNullCtx = ConstantAndAttributeReader.ReadNullableContext(md, methodAttrs) ?? typeNullCtx;
        var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
        var isExt = isStatic && isExtContainer && dispSig.ParameterTypes.Length > 0 &&
                    ConstantAndAttributeReader.HasAttribute(md, methodAttrs, "System.Runtime.CompilerServices", "ExtensionAttribute");

        MethodSignature<string>? docSig = null;
        if (xmlDocs.Count > 0 || isExt)
        {
            try { docSig = method.DecodeSignature(XmlDocSignatureProvider.Instance, fullCtx); }
            catch { docSig = null; }
        }

        var (retInfo, paramInfos) = ReadParamMetadata(md, method);
        var modifiers = isCtor ? string.Empty : GetMethodModifiers(method.Attributes, isInterfaceType);
        var methodConstraints = methodGp.Count > 0 ? ReadGenericConstraints(md, method.GetGenericParameters(), fullCtx) : string.Empty;

        var rawRet = isCtor
            ? simpleTypeName
            : ConstantAndAttributeReader.ApplyTopLevelNullability(dispSig.ReturnType, retInfo?.NullFlag, methodNullCtx, refLookup, fullCtx);
        var returnType = isCtor ? simpleTypeName : FormatReturnRefPrefix(rawRet, retInfo);

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        if (isCtor)
        {
            sb.Append(simpleTypeName);
        }
        else
        {
            sb.Append(returnType).Append(' ').Append(name);
            if (methodGp.Count > 0)
                sb.Append('<').Append(string.Join(", ", methodGp)).Append('>');
        }

        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeFull, string Role)>(TypeRefComparer.Instance);
        if (isCtor)
            AddCollectedRefs(typeRefs, seenRefs, new[] { (bareTypeFullName, simpleTypeName) }, "return");
        else
            AddCollectedRefs(typeRefs, seenRefs, refSig.ReturnType, "return");

        sb.Append('(');
        var paramSimpleList = new List<string>(dispSig.ParameterTypes.Length);
        for (var i = 0; i < dispSig.ParameterTypes.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            paramInfos.TryGetValue(i + 1, out var pi);
            var rawPt = ConstantAndAttributeReader.ApplyTopLevelNullability(dispSig.ParameterTypes[i], pi?.NullFlag, methodNullCtx, refLookup, fullCtx);

            if (i == 0 && isExt) sb.Append("this ");
            if (pi?.IsParams == true) sb.Append("params ");

            var cleanPt = FormatByRefPrefix(rawPt, pi, sb);
            paramSimpleList.Add(TypeNameHelper.ExtractSimpleTypeName(cleanPt));
            AddCollectedRefs(typeRefs, seenRefs, refSig.ParameterTypes[i], "param");

            sb.Append(cleanPt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
            if (pi?.DefaultValue != null)
            {
                var defText = ConstantAndAttributeReader.FormatDefaultValue(cleanPt, pi.DefaultValue, pi.IsIntegerConst, enumLookup, fullCtx);
                sb.Append(" = ").Append(defText);
            }
        }
        sb.Append(')');
        if (!string.IsNullOrEmpty(methodConstraints))
            sb.Append(' ').Append(methodConstraints);

        IReadOnlyList<ExtensionTargetRef> extTargets = Array.Empty<ExtensionTargetRef>();
        if (isExt && docSig.HasValue && docSig.Value.ParameterTypes.Length > 0)
        {
            extTargets = TypeNameHelper.ResolveExtensionTargets(md, method, docSig.Value.ParameterTypes[0], fullCtx);
        }

        var extTargetFull = extTargets.Count > 0 ? extTargets[0].TargetFull : null;
        var extTargetSimple = extTargets.Count > 0 ? extTargets[0].TargetSimple : null;

        var docMethodName = isCtor ? "#ctor" : name;
        if (methodGp.Count > 0) docMethodName += $"``{methodGp.Count}";
        var docId = docSig.HasValue && docSig.Value.ParameterTypes.Length > 0
            ? $"M:{docTypePrefix}.{docMethodName}({string.Join(",", docSig.Value.ParameterTypes)})"
            : $"M:{docTypePrefix}.{docMethodName}";
        if (docSig.HasValue && name is "op_Implicit" or "op_Explicit")
            docId += "~" + docSig.Value.ReturnType;

        XmlDocEntry? doc = null;
        if (xmlDocs.Count > 0)
        {
            var fbKey = XmlDocReader.BuildMethodFallbackKey(docId);
            doc = xmlDocs.Find(docId, fbKey);
        }

        return new ExtractedMember(
            Kind: isCtor ? "ctor" : "method",
            Name: isCtor ? simpleTypeName : name,
            Accessibility: acc,
            Modifiers: modifiers,
            ReturnType: returnType,
            ReturnSimple: isCtor ? simpleTypeName : TypeNameHelper.ExtractSimpleTypeName(dispSig.ReturnType),
            Signature: sb.ToString(),
            ParamTypes: string.Join("|", paramSimpleList),
            TypeRefs: typeRefs,
            ExtTargetType: extTargetFull,
            ExtTargetSimple: extTargetSimple,
            ExtensionTargets: extTargets,
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: FormatParamsDoc(doc),
            ReturnsDoc: doc?.Returns,
            DocId: docId);
    }

    private static string FormatReturnRefPrefix(string rawRet, ParamInfo? retInfo)
    {
        if (!rawRet.EndsWith('&')) return rawRet;
        var clean = rawRet[..^1];
        return retInfo?.IsReadOnly == true ? $"ref readonly {clean}" : $"ref {clean}";
    }

    private static string FormatByRefPrefix(string rawPt, ParamInfo? pi, StringBuilder sb)
    {
        if (!rawPt.EndsWith('&')) return rawPt;
        var cleanPt = rawPt[..^1];
        if (pi?.IsOut == true && pi.IsIn != true)
            sb.Append("out ");
        else if (pi?.IsReadOnly == true)
            sb.Append(pi.IsIn ? "in " : "ref readonly ");
        else
            sb.Append("ref ");
        return cleanPt;
    }

    private sealed record ParamInfo(
        string Name,
        bool IsOut,
        bool IsIn,
        bool IsReadOnly,
        bool IsParams,
        byte? NullFlag,
        string? DefaultValue,
        bool IsIntegerConst);

    private static (ParamInfo? ReturnParam, Dictionary<int, ParamInfo> Params) ReadParamMetadata(MetadataReader md, MethodDefinition method)
    {
        ParamInfo? returnParam = null;
        var map = new Dictionary<int, ParamInfo>();

        foreach (var ph in method.GetParameters())
        {
            var p = md.GetParameter(ph);
            var attrs = p.GetCustomAttributes();
            var nullFlag = ConstantAndAttributeReader.ReadTopLevelNullableFlag(md, attrs);
            var isReadOnly = ConstantAndAttributeReader.HasAttribute(md, attrs, "System.Runtime.CompilerServices", "IsReadOnlyAttribute")
                          || ConstantAndAttributeReader.HasAttribute(md, attrs, "System.Runtime.CompilerServices", "RequiresLocationAttribute");

            if (p.SequenceNumber == 0)
            {
                returnParam = new ParamInfo(string.Empty, false, false, isReadOnly, false, nullFlag, null, false);
                continue;
            }

            var pName = md.GetString(p.Name);
            var isOut = (p.Attributes & ParameterAttributes.Out) != 0;
            var isIn = (p.Attributes & ParameterAttributes.In) != 0;
            var isParams = ConstantAndAttributeReader.HasAttribute(md, attrs, "System", "ParamArrayAttribute");

            string? defVal = null;
            var isIntConst = false;
            if ((p.Attributes & ParameterAttributes.HasDefault) != 0)
            {
                var ch = p.GetDefaultValue();
                if (!ch.IsNil)
                {
                    var c = md.GetConstant(ch);
                    defVal = ConstantAndAttributeReader.ReadConstantValue(md, c);
                    isIntConst = ConstantAndAttributeReader.IsIntegerConstantType(c.TypeCode);
                }
            }
            else if (ConstantAndAttributeReader.TryReadDecimalConstant(md, attrs, out var decDef))
            {
                defVal = decDef;
            }
            else if ((p.Attributes & ParameterAttributes.Optional) != 0)
            {
                defVal = "default";
            }

            map[p.SequenceNumber] = new ParamInfo(pName, isOut, isIn, isReadOnly, isParams, nullFlag, defVal, isIntConst);
        }

        return (returnParam, map);
    }

    private static bool IsInitOnlySetter(MetadataReader md, MethodDefinition setter)
    {
        try
        {
            return setter.DecodeSignature(InitOnlyDetectorProvider.Instance, null).ReturnType;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMethodAccessibility(MethodAttributes attrs) => (attrs & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Family or MethodAttributes.FamORAssem => "protected",
        MethodAttributes.Assembly or MethodAttributes.FamANDAssem => "internal",
        _ => "private",
    };

    private static string GetFieldAccessibility(FieldAttributes attrs) => (attrs & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Family or FieldAttributes.FamORAssem => "protected",
        FieldAttributes.Assembly or FieldAttributes.FamANDAssem => "internal",
        _ => "private",
    };

    private static string PickHigherAccessibility(string? a, string? b)
    {
        static int Rank(string? acc) => acc switch
        {
            "public" => 4,
            "protected" => 3,
            "internal" => 2,
            "private" => 1,
            _ => 0,
        };
        return Rank(a) >= Rank(b) ? (a ?? "private") : (b ?? "private");
    }

    private static string BuildPropertyAccessors(string? getterAcc, string? setterAcc, string bestAcc, bool isInit)
    {
        var parts = new List<string>(2);
        if (getterAcc != null)
            parts.Add(getterAcc == bestAcc ? "get;" : $"{getterAcc} get;");
        if (setterAcc != null)
        {
            var kw = isInit ? "init;" : "set;";
            parts.Add(setterAcc == bestAcc ? kw : $"{setterAcc} {kw}");
        }
        return string.Join(" ", parts);
    }

    private static string GetMethodModifiers(MethodAttributes attrs, bool isInterfaceType)
    {
        if (isInterfaceType)
        {
            if ((attrs & MethodAttributes.Static) == 0)
                return string.Empty;
            if ((attrs & MethodAttributes.Abstract) != 0) return "static abstract";
            if ((attrs & MethodAttributes.Virtual) != 0) return "static virtual";
            return "static";
        }

        if ((attrs & MethodAttributes.Static) != 0) return "static";
        if ((attrs & MethodAttributes.Abstract) != 0) return "abstract";
        if ((attrs & MethodAttributes.Virtual) != 0)
        {
            var isNewSlot = (attrs & MethodAttributes.NewSlot) != 0;
            var isFinal = (attrs & MethodAttributes.Final) != 0;
            if (isFinal) return isNewSlot ? string.Empty : "sealed override";
            return isNewSlot ? "virtual" : "override";
        }
        return string.Empty;
    }

    private static string? FormatParamsDoc(XmlDocEntry? doc)
    {
        if (doc == null || doc.Params.Count == 0) return null;
        return string.Join("\n", doc.Params.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    private static void AddCollectedRefs(
        List<MemberTypeRef> list,
        HashSet<(string TypeFull, string Role)> seen,
        IReadOnlyList<(string FullName, string SimpleName)> refs,
        string role)
    {
        foreach (var (full, simple) in refs)
        {
            if (!string.IsNullOrEmpty(simple) && seen.Add((full, role)))
                list.Add(new MemberTypeRef(full, simple, role));
        }
    }

    private sealed class TypeRefComparer : IEqualityComparer<(string TypeFull, string Role)>
    {
        public static readonly TypeRefComparer Instance = new();

        public bool Equals((string TypeFull, string Role) x, (string TypeFull, string Role) y)
            => string.Equals(x.TypeFull, y.TypeFull, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Role, y.Role, StringComparison.Ordinal);

        public int GetHashCode((string TypeFull, string Role) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeFull),
                StringComparer.Ordinal.GetHashCode(obj.Role));
    }
}
