using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ApiLookup;

public static class MetadataExtractor
{
    /// <summary>State shared by every type and member extracted from one assembly.</summary>
    private sealed record AssemblyContext(
        MetadataReader Md,
        XmlDocBook XmlDocs,
        EnumLookup Enums,
        bool IncludeNonPublic);

    /// <summary>State shared by every member extracted from one type.</summary>
    private sealed record TypeContext(
        AssemblyContext Asm,
        string BareFullName,
        string SimpleName,
        string Kind,
        string DocTypePrefix,
        GenericContext GenericCtx,
        bool IsInterface,
        bool IsExtensionContainer,
        string DefaultMemberName,
        byte NullableContext)
    {
        public MetadataReader Md => Asm.Md;
    }

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
            var enumLookup = ConstantAndAttributeReader.ScanAssemblyTypes(md, includeNonPublic);
            var asm = new AssemblyContext(md, xmlDocs, enumLookup, includeNonPublic);

            var types = new List<ExtractedType>();
            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                if (ShouldSkipType(md, td, md.GetString(td.Name), includeNonPublic))
                    continue;
                types.Add(ExtractType(asm, td));
            }

            if (types.Count == 0 && md.ExportedTypes.Count > 0)
            {
                var forwarders = md.ExportedTypes.Count(h => md.GetExportedType(h).IsForwarder);
                if (forwarders > 0)
                    warnings?.Add($"Assembly '{asmName}' ({fullPath}) is a type-forwarding facade ({forwarders} forwarded types, 0 local definitions). Use 'locate' or index its target implementation assembly.");
            }

            return new ExtractedAssembly(asmName, fullPath, asmVersion, xmlPath, fileSize, mtimeUtc, types);
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

        return !includeNonPublic && !Accessibilities.IsPublicApi(GetEffectiveVisibility(md, td));
    }

    internal static string GetEffectiveVisibility(MetadataReader md, TypeDefinition td)
    {
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        if (!td.IsNested)
            return vis == TypeAttributes.Public ? Accessibilities.Public : Accessibilities.Internal;

        var parentVis = GetEffectiveVisibility(md, md.GetTypeDefinition(td.GetDeclaringType()));
        if (parentVis is Accessibilities.Internal or Accessibilities.Private) return parentVis;

        return vis switch
        {
            TypeAttributes.NestedPublic => Accessibilities.Public,
            TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => Accessibilities.Protected,
            TypeAttributes.NestedAssembly or TypeAttributes.NestedFamANDAssem => Accessibilities.Internal,
            _ => Accessibilities.Private,
        };
    }

    // ---- Types -------------------------------------------------------------------------------------------

    private static ExtractedType ExtractType(AssemblyContext asm, TypeDefinition td)
    {
        var md = asm.Md;
        var (ns, clrName, displayFullName, bareFullName, displayName, simpleName) = TypeNameHelper.BuildTypeNames(md, td);

        var genericParamNames = TypeNameHelper.ReadGenericParamNames(md, td.GetGenericParameters());
        var valueGenericParams = TypeNameHelper.ReadValueTypeGenericParams(md, td.GetGenericParameters());
        var genericCtx = new GenericContext(genericParamNames, Array.Empty<string>(), valueGenericParams);

        var (baseFull, baseBare, baseSimple, baseClr) = ResolveBaseType(md, td, genericCtx);
        var kind = DetermineTypeKind(td, baseFull);
        var visibility = GetEffectiveVisibility(md, td);
        var modifiers = DetermineTypeModifiers(md, td, kind);
        var (interfaces, interfaceRefs) = ResolveInterfaces(md, td, genericCtx);
        var genericConstraints = ReadGenericConstraints(md, td.GetGenericParameters(), genericCtx);

        var docTypePrefix = clrName.Replace('+', '.');
        var docId = "T:" + docTypePrefix;
        var typeDoc = asm.XmlDocs.Find(docId);

        var typeCtx = new TypeContext(
            Asm: asm,
            BareFullName: bareFullName,
            SimpleName: simpleName,
            Kind: kind,
            DocTypePrefix: docTypePrefix,
            GenericCtx: genericCtx,
            IsInterface: kind == TypeKinds.Interface,
            IsExtensionContainer: ConstantAndAttributeReader.HasAttribute(md, td.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute"),
            DefaultMemberName: ConstantAndAttributeReader.ReadDefaultMemberName(md, td) ?? "Item",
            NullableContext: ResolveInheritedNullableContext(md, td));

        var delegateSignature = kind == TypeKinds.Delegate
            ? ExtractDelegateSignature(typeCtx, td, visibility, displayName, genericConstraints)
            : null;

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
            BaseClr: baseClr,
            Interfaces: string.Join(", ", interfaces),
            InterfaceRefs: interfaceRefs,
            GenericParams: string.Join(", ", genericParamNames),
            GenericConstraints: genericConstraints,
            DelegateSignature: delegateSignature,
            IsExtensionContainer: typeCtx.IsExtensionContainer,
            Summary: typeDoc?.Summary,
            Remarks: typeDoc?.Remarks,
            DocId: docId,
            InheritDoc: InheritDocOf(typeDoc),
            Members: ExtractMembers(typeCtx, td));
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
                if (isStruct && typeName is "ValueType" or WellKnownTypes.ValueType) continue;
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
        TypeContext t,
        TypeDefinition td,
        string visibility,
        string displayName,
        string genericConstraints)
    {
        var md = t.Md;
        foreach (var mh in td.GetMethods())
        {
            var method = md.GetMethodDefinition(mh);
            if (md.GetString(method.Name) != "Invoke") continue;

            MethodSignature<TypeSig> sig;
            try { sig = method.DecodeSignature(TypeSigProvider.Instance, t.GenericCtx); }
            catch { return null; }

            var methodNullCtx = ConstantAndAttributeReader.ReadNullableContext(md, method.GetCustomAttributes()) ?? t.NullableContext;
            var (retInfo, paramInfos) = ReadParamMetadata(md, method);

            var retRaw = TypeSigRenderer.Render(sig.ReturnType, EffectiveFlags(retInfo?.NullFlags, methodNullCtx));
            var retText = FormatReturnRefPrefix(retRaw, retInfo);

            var sb = new StringBuilder();
            sb.Append(visibility).Append(" delegate ").Append(retText).Append(' ').Append(displayName).Append('(');
            AppendParameters(sb, t.Asm, sig.ParameterTypes, paramInfos, methodNullCtx, t.GenericCtx);
            sb.Append(')');
            if (!string.IsNullOrEmpty(genericConstraints))
                sb.Append(' ').Append(genericConstraints);
            return sb.ToString();
        }
        return null;
    }

    private static (string? full, string? bare, string? simple, string? clr) ResolveBaseType(MetadataReader md, TypeDefinition td, GenericContext ctx)
    {
        if (td.BaseType.IsNil) return (null, null, null, null);
        var full = TypeNameHelper.ResolveEntityFullName(md, td.BaseType, ctx);
        if (string.IsNullOrEmpty(full)) return (null, null, null, null);
        var bare = TypeNameHelper.StripGenerics(full);
        var simple = TypeNameHelper.ExtractSimpleTypeName(bare);
        var clr = TypeNameHelper.ResolveEntityClrName(md, td.BaseType, ctx);
        return (full, bare, simple, string.IsNullOrEmpty(clr) ? null : clr);
    }

    /// <summary>Records an unresolved <c>&lt;inheritdoc/&gt;</c> only when the entry has no summary of its own.</summary>
    private static string? InheritDocOf(XmlDocEntry? doc) => doc?.Summary == null ? doc?.InheritDocCref : null;

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
                    refs.Add(new InterfaceRef(bare, simple, TypeNameHelper.ResolveEntityClrName(md, impl.Interface, ctx)));
            }
        }
        return (displayNames, refs);
    }

    private static string DetermineTypeKind(TypeDefinition td, string? baseFull)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0) return TypeKinds.Interface;
        if (baseFull == WellKnownTypes.Enum) return TypeKinds.Enum;
        if (baseFull == WellKnownTypes.ValueType) return TypeKinds.Struct;
        if (WellKnownTypes.IsDelegateBase(baseFull)) return TypeKinds.Delegate;
        return TypeKinds.Class;
    }

    private static string DetermineTypeModifiers(MetadataReader md, TypeDefinition td, string kind)
    {
        if (kind == TypeKinds.Struct)
        {
            var attrs = td.GetCustomAttributes();
            var isReadOnly = ConstantAndAttributeReader.HasAttribute(md, attrs, "System.Runtime.CompilerServices", "IsReadOnlyAttribute");
            var isByRefLike = ConstantAndAttributeReader.HasAttribute(md, attrs, "System.Runtime.CompilerServices", "IsByRefLikeAttribute");
            if (isReadOnly && isByRefLike) return "readonly ref";
            if (isReadOnly) return "readonly";
            if (isByRefLike) return "ref";
            return string.Empty;
        }

        if (kind != TypeKinds.Class) return string.Empty;
        var isAbs = (td.Attributes & TypeAttributes.Abstract) != 0;
        var isSealed = (td.Attributes & TypeAttributes.Sealed) != 0;
        if (isAbs && isSealed) return "static";
        if (isAbs) return "abstract";
        if (isSealed) return "sealed";
        return string.Empty;
    }

    // ---- Members -----------------------------------------------------------------------------------------

    private static List<ExtractedMember> ExtractMembers(TypeContext t, TypeDefinition td)
    {
        var md = t.Md;
        var members = new List<ExtractedMember>();
        var accessorMethods = new HashSet<MethodDefinitionHandle>();

        foreach (var ph in td.GetProperties())
        {
            var prop = md.GetPropertyDefinition(ph);
            var accessors = prop.GetAccessors();
            if (!accessors.Getter.IsNil) accessorMethods.Add(accessors.Getter);
            if (!accessors.Setter.IsNil) accessorMethods.Add(accessors.Setter);

            var extracted = ExtractProperty(t, prop, accessors);
            if (extracted != null) members.Add(extracted);
        }

        foreach (var eh in td.GetEvents())
        {
            var ev = md.GetEventDefinition(eh);
            var accessors = ev.GetAccessors();
            if (!accessors.Adder.IsNil) accessorMethods.Add(accessors.Adder);
            if (!accessors.Remover.IsNil) accessorMethods.Add(accessors.Remover);
            if (!accessors.Raiser.IsNil) accessorMethods.Add(accessors.Raiser);

            var extracted = ExtractEvent(t, ev, accessors);
            if (extracted != null) members.Add(extracted);
        }

        foreach (var fh in td.GetFields())
        {
            var extracted = ExtractField(t, md.GetFieldDefinition(fh));
            if (extracted != null) members.Add(extracted);
        }

        foreach (var mh in td.GetMethods())
        {
            if (accessorMethods.Contains(mh)) continue;
            var extracted = ExtractMethod(t, md.GetMethodDefinition(mh));
            if (extracted != null) members.Add(extracted);
        }

        return members;
    }

    private static ExtractedMember? ExtractProperty(TypeContext t, PropertyDefinition prop, PropertyAccessors accessors)
    {
        var md = t.Md;
        MethodDefinition? getter = accessors.Getter.IsNil ? null : md.GetMethodDefinition(accessors.Getter);
        MethodDefinition? setter = accessors.Setter.IsNil ? null : md.GetMethodDefinition(accessors.Setter);

        var getterAcc = getter.HasValue ? GetMethodAccessibility(getter.Value.Attributes) : null;
        var setterAcc = setter.HasValue ? GetMethodAccessibility(setter.Value.Attributes) : null;
        var bestAcc = PickHigherAccessibility(getterAcc, setterAcc);

        if (!t.Asm.IncludeNonPublic && !Accessibilities.IsPublicApi(bestAcc))
            return null;

        var name = md.GetString(prop.Name);
        var primaryMethod = getter ?? setter;
        var modifiers = primaryMethod.HasValue ? GetMethodModifiers(primaryMethod.Value.Attributes, t.IsInterface) : string.Empty;
        var ctx = t.GenericCtx;

        MethodSignature<TypeSig> dispSig;
        MethodSignature<IReadOnlyList<(string FullName, string SimpleName)>> refSig;
        try
        {
            dispSig = prop.DecodeSignature(TypeSigProvider.Instance, ctx);
            refSig = prop.DecodeSignature(TypeRefCollectorProvider.Instance, ctx);
        }
        catch
        {
            return null;
        }

        MethodSignature<string>? docSig = null;
        if (t.Asm.XmlDocs.Count > 0 && dispSig.ParameterTypes.Length > 0)
        {
            try { docSig = prop.DecodeSignature(XmlDocSignatureProvider.Instance, ctx); }
            catch { docSig = null; }
        }

        var (retInfo, paramInfos) = primaryMethod.HasValue
            ? ReadParamMetadata(md, primaryMethod.Value)
            : (null, new Dictionary<int, ParamInfo>());

        var propNullCtx = (primaryMethod.HasValue ? ConstantAndAttributeReader.ReadNullableContext(md, primaryMethod.Value.GetCustomAttributes()) : null) ?? t.NullableContext;
        var propNullFlags = ConstantAndAttributeReader.ReadNullableFlags(md, prop.GetCustomAttributes()) ?? retInfo?.NullFlags;
        var rawRet = TypeSigRenderer.Render(dispSig.ReturnType, EffectiveFlags(propNullFlags, propNullCtx));
        var returnType = FormatReturnRefPrefix(rawRet, retInfo);
        var returnSimple = TypeNameHelper.ExtractSimpleTypeName(TypeSigRenderer.Render(dispSig.ReturnType));

        var isInit = setter.HasValue && IsInitOnlySetter(setter.Value);
        var accessorText = BuildPropertyAccessors(getterAcc, setterAcc, bestAcc, isInit);

        var sb = new StringBuilder();
        sb.Append(bestAcc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append(returnType).Append(' ');

        var paramSimpleList = new List<string>();
        var typeRefs = new TypeRefCollector();
        typeRefs.Add(refSig.ReturnType, TypeRefRoles.Return);

        string docId;
        if (dispSig.ParameterTypes.Length > 0)
        {
            var propDisplayName = string.Equals(name, t.DefaultMemberName, StringComparison.Ordinal) ? "this" : name;
            sb.Append(propDisplayName).Append('[');
            AppendParameters(sb, t.Asm, dispSig.ParameterTypes, paramInfos, propNullCtx, ctx,
                onParam: (i, cleanPt) =>
                {
                    paramSimpleList.Add(TypeNameHelper.ExtractSimpleTypeName(cleanPt));
                    typeRefs.Add(refSig.ParameterTypes[i], TypeRefRoles.Param);
                });
            sb.Append(']');
            docId = docSig.HasValue
                ? $"P:{t.DocTypePrefix}.{name}({string.Join(",", docSig.Value.ParameterTypes)})"
                : $"P:{t.DocTypePrefix}.{name}";
        }
        else
        {
            sb.Append(name);
            docId = $"P:{t.DocTypePrefix}.{name}";
        }

        sb.Append(" { ").Append(accessorText).Append(" }");
        var doc = t.Asm.XmlDocs.Find(docId);

        return new ExtractedMember(
            Kind: MemberKinds.Property,
            Name: name,
            Accessibility: bestAcc,
            Modifiers: modifiers,
            ReturnType: returnType,
            ReturnSimple: returnSimple,
            Signature: sb.ToString(),
            ParamTypes: string.Join("|", paramSimpleList),
            TypeRefs: typeRefs.Items,
            ExtTargetType: null,
            ExtTargetSimple: null,
            ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: FormatParamsDoc(doc),
            ReturnsDoc: doc?.Returns,
            DocId: docId,
            InheritDoc: InheritDocOf(doc));
    }

    private static ExtractedMember? ExtractEvent(TypeContext t, EventDefinition ev, EventAccessors accessors)
    {
        var md = t.Md;
        MethodDefinition? adder = accessors.Adder.IsNil ? null : md.GetMethodDefinition(accessors.Adder);
        MethodDefinition? remover = accessors.Remover.IsNil ? null : md.GetMethodDefinition(accessors.Remover);
        var adderAcc = adder.HasValue ? GetMethodAccessibility(adder.Value.Attributes) : null;
        var removerAcc = remover.HasValue ? GetMethodAccessibility(remover.Value.Attributes) : null;
        var acc = PickHigherAccessibility(adderAcc, removerAcc);

        if (!t.Asm.IncludeNonPublic && !Accessibilities.IsPublicApi(acc))
            return null;

        var name = md.GetString(ev.Name);
        var primary = adder ?? remover;
        var modifiers = primary.HasValue ? GetMethodModifiers(primary.Value.Attributes, t.IsInterface) : string.Empty;
        var handlerSig = (ev.Type.IsNil ? null : TypeSigProvider.FromEntity(md, ev.Type, t.GenericCtx))
                         ?? new NamedTypeSig("EventHandler", "System.EventHandler", IsValueType: false);
        var evNullFlags = ConstantAndAttributeReader.ReadNullableFlags(md, ev.GetCustomAttributes());
        var handlerType = TypeSigRenderer.Render(handlerSig, EffectiveFlags(evNullFlags, t.NullableContext));
        var handlerSimple = TypeNameHelper.ExtractSimpleTypeName(TypeSigRenderer.Render(handlerSig));

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append("event ").Append(handlerType).Append(' ').Append(name);

        var docId = $"E:{t.DocTypePrefix}.{name}";
        var doc = t.Asm.XmlDocs.Find(docId);

        var typeRefs = new TypeRefCollector();
        typeRefs.Add(
            ev.Type.IsNil
                ? new[] { ("System.EventHandler", "EventHandler") }
                : TypeRefCollectorProvider.CollectFromEntity(md, ev.Type, t.GenericCtx),
            TypeRefRoles.Return);

        return new ExtractedMember(
            Kind: MemberKinds.Event,
            Name: name,
            Accessibility: acc,
            Modifiers: modifiers,
            ReturnType: handlerType,
            ReturnSimple: handlerSimple,
            Signature: sb.ToString(),
            ParamTypes: string.Empty,
            TypeRefs: typeRefs.Items,
            ExtTargetType: null,
            ExtTargetSimple: null,
            ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: null,
            ReturnsDoc: null,
            DocId: docId,
            InheritDoc: InheritDocOf(doc));
    }

    private static ExtractedMember? ExtractField(TypeContext t, FieldDefinition field)
    {
        var md = t.Md;
        var name = md.GetString(field.Name);
        if (name == "value__" || name.StartsWith('<')) return null;

        var acc = GetFieldAccessibility(field.Attributes);
        if (!t.Asm.IncludeNonPublic && !Accessibilities.IsPublicApi(acc))
            return null;

        TypeSig fieldSig;
        IReadOnlyList<(string FullName, string SimpleName)> collectedRefs;
        try
        {
            fieldSig = field.DecodeSignature(TypeSigProvider.Instance, t.GenericCtx);
            collectedRefs = field.DecodeSignature(TypeRefCollectorProvider.Instance, t.GenericCtx);
        }
        catch
        {
            return null;
        }

        var fieldAttrs = field.GetCustomAttributes();
        var rawFieldType = TypeSigRenderer.Render(fieldSig);
        var fieldType = TypeSigRenderer.Render(fieldSig, EffectiveFlags(ConstantAndAttributeReader.ReadNullableFlags(md, fieldAttrs), t.NullableContext));

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

        var docId = $"F:{t.DocTypePrefix}.{name}";
        var doc = t.Asm.XmlDocs.Find(docId);

        var typeRefs = new TypeRefCollector();
        typeRefs.Add(collectedRefs, TypeRefRoles.Return);
        var returnSimple = TypeNameHelper.ExtractSimpleTypeName(rawFieldType);

        if (t.Kind == TypeKinds.Enum && isLiteral)
        {
            return new ExtractedMember(
                Kind: MemberKinds.EnumValue,
                Name: name,
                Accessibility: Accessibilities.Public,
                Modifiers: "const",
                ReturnType: fieldType,
                ReturnSimple: returnSimple,
                Signature: constVal != null ? $"{name} = {constVal}" : name,
                ParamTypes: string.Empty,
                TypeRefs: typeRefs.Items,
                ExtTargetType: null,
                ExtTargetSimple: null,
                ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
                EnumValue: constVal,
                Summary: doc?.Summary,
                ParamsDoc: null,
                ReturnsDoc: null,
                DocId: docId,
            InheritDoc: InheritDocOf(doc));
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
            Kind: MemberKinds.Field,
            Name: name,
            Accessibility: acc,
            Modifiers: modText,
            ReturnType: fieldType,
            ReturnSimple: returnSimple,
            Signature: sb.ToString(),
            ParamTypes: string.Empty,
            TypeRefs: typeRefs.Items,
            ExtTargetType: null,
            ExtTargetSimple: null,
            ExtensionTargets: Array.Empty<ExtensionTargetRef>(),
            EnumValue: constVal,
            Summary: doc?.Summary,
            ParamsDoc: null,
            ReturnsDoc: null,
            DocId: docId,
            InheritDoc: InheritDocOf(doc));
    }

    private static ExtractedMember? ExtractMethod(TypeContext t, MethodDefinition method)
    {
        var md = t.Md;
        var name = md.GetString(method.Name);
        if (name == ".cctor" || name.StartsWith('<')) return null;

        var isCtor = name == ".ctor";
        var isSpecial = (method.Attributes & MethodAttributes.SpecialName) != 0;
        if (isSpecial && !isCtor && !name.StartsWith("op_", StringComparison.Ordinal))
            return null;

        var acc = GetMethodAccessibility(method.Attributes);
        if (!t.Asm.IncludeNonPublic && !Accessibilities.IsPublicApi(acc))
            return null;

        var methodGp = TypeNameHelper.ReadGenericParamNames(md, method.GetGenericParameters());
        var fullCtx = methodGp.Count > 0
            ? t.GenericCtx with
            {
                MethodParams = methodGp,
                ValueTypeGenericParams = TypeNameHelper.ReadValueTypeGenericParams(md, method.GetGenericParameters(), t.GenericCtx.ValueTypeGenericParams),
            }
            : t.GenericCtx;

        MethodSignature<TypeSig> dispSig;
        MethodSignature<IReadOnlyList<(string FullName, string SimpleName)>> refSig;
        try
        {
            dispSig = method.DecodeSignature(TypeSigProvider.Instance, fullCtx);
            refSig = method.DecodeSignature(TypeRefCollectorProvider.Instance, fullCtx);
        }
        catch
        {
            return null;
        }

        var methodAttrs = method.GetCustomAttributes();
        var methodNullCtx = ConstantAndAttributeReader.ReadNullableContext(md, methodAttrs) ?? t.NullableContext;
        var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
        var isExt = isStatic && t.IsExtensionContainer && dispSig.ParameterTypes.Length > 0 &&
                    ConstantAndAttributeReader.HasAttribute(md, methodAttrs, "System.Runtime.CompilerServices", "ExtensionAttribute");

        MethodSignature<string>? docSig = null;
        if (t.Asm.XmlDocs.Count > 0 || isExt)
        {
            try { docSig = method.DecodeSignature(XmlDocSignatureProvider.Instance, fullCtx); }
            catch { docSig = null; }
        }

        var (retInfo, paramInfos) = ReadParamMetadata(md, method);
        var modifiers = isCtor ? string.Empty : GetMethodModifiers(method.Attributes, t.IsInterface);
        var methodConstraints = methodGp.Count > 0 ? ReadGenericConstraints(md, method.GetGenericParameters(), fullCtx) : string.Empty;

        var returnType = isCtor
            ? t.SimpleName
            : FormatReturnRefPrefix(
                TypeSigRenderer.Render(dispSig.ReturnType, EffectiveFlags(retInfo?.NullFlags, methodNullCtx)),
                retInfo);

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        if (isCtor)
        {
            sb.Append(t.SimpleName);
        }
        else
        {
            sb.Append(returnType).Append(' ').Append(name);
            if (methodGp.Count > 0)
                sb.Append('<').Append(string.Join(", ", methodGp)).Append('>');
        }

        var typeRefs = new TypeRefCollector();
        if (isCtor)
            typeRefs.Add(new[] { (t.BareFullName, t.SimpleName) }, TypeRefRoles.Return);
        else
            typeRefs.Add(refSig.ReturnType, TypeRefRoles.Return);

        var paramSimpleList = new List<string>(dispSig.ParameterTypes.Length);
        sb.Append('(');
        AppendParameters(sb, t.Asm, dispSig.ParameterTypes, paramInfos, methodNullCtx, fullCtx, isExtension: isExt,
            onParam: (i, cleanPt) =>
            {
                paramSimpleList.Add(TypeNameHelper.ExtractSimpleTypeName(cleanPt));
                typeRefs.Add(refSig.ParameterTypes[i], TypeRefRoles.Param);
            });
        sb.Append(')');
        if (!string.IsNullOrEmpty(methodConstraints))
            sb.Append(' ').Append(methodConstraints);

        IReadOnlyList<ExtensionTargetRef> extTargets = isExt && docSig.HasValue && docSig.Value.ParameterTypes.Length > 0
            ? TypeNameHelper.ResolveExtensionTargets(md, method, docSig.Value.ParameterTypes[0], fullCtx)
            : Array.Empty<ExtensionTargetRef>();

        var docMethodName = isCtor ? "#ctor" : name;
        if (methodGp.Count > 0) docMethodName += $"``{methodGp.Count}";
        var docId = docSig.HasValue && docSig.Value.ParameterTypes.Length > 0
            ? $"M:{t.DocTypePrefix}.{docMethodName}({string.Join(",", docSig.Value.ParameterTypes)})"
            : $"M:{t.DocTypePrefix}.{docMethodName}";
        if (docSig.HasValue && name is "op_Implicit" or "op_Explicit")
            docId += "~" + docSig.Value.ReturnType;

        var doc = t.Asm.XmlDocs.Count > 0
            ? t.Asm.XmlDocs.Find(docId, XmlDocReader.BuildMethodFallbackKey(docId))
            : null;

        return new ExtractedMember(
            Kind: isCtor ? MemberKinds.Constructor : MemberKinds.Method,
            Name: isCtor ? t.SimpleName : name,
            Accessibility: acc,
            Modifiers: modifiers,
            ReturnType: returnType,
            ReturnSimple: isCtor ? t.SimpleName : TypeNameHelper.ExtractSimpleTypeName(TypeSigRenderer.Render(dispSig.ReturnType)),
            Signature: sb.ToString(),
            ParamTypes: string.Join("|", paramSimpleList),
            TypeRefs: typeRefs.Items,
            ExtTargetType: JoinExtensionTargets(extTargets, e => e.TargetFull),
            ExtTargetSimple: JoinExtensionTargets(extTargets, e => e.TargetSimple),
            ExtensionTargets: extTargets,
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: FormatParamsDoc(doc),
            ReturnsDoc: doc?.Returns,
            DocId: docId,
            InheritDoc: InheritDocOf(doc));
    }

    private static string? JoinExtensionTargets(IReadOnlyList<ExtensionTargetRef> targets, Func<ExtensionTargetRef, string> selector)
        => targets.Count switch
        {
            0 => null,
            1 => selector(targets[0]),
            _ => string.Join(" & ", targets.Select(selector)),
        };

    // ---- Signature helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Appends a C# parameter list body (without the surrounding brackets) shared by methods, delegates, and indexers:
    /// <c>this</c>/<c>params</c>/<c>ref</c>/<c>out</c>/<c>in</c> prefixes, nullability, names, and default values.
    /// <paramref name="onParam"/> receives each parameter index and its clean display type.
    /// </summary>
    private static void AppendParameters(
        StringBuilder sb,
        AssemblyContext asm,
        IReadOnlyList<TypeSig> parameterTypes,
        Dictionary<int, ParamInfo> paramInfos,
        byte nullableContext,
        GenericContext ctx,
        bool isExtension = false,
        Action<int, string>? onParam = null)
    {
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            paramInfos.TryGetValue(i + 1, out var pi);
            var rawPt = TypeSigRenderer.Render(parameterTypes[i], EffectiveFlags(pi?.NullFlags, nullableContext));

            if (i == 0 && isExtension) sb.Append("this ");
            if (pi?.IsParams == true) sb.Append("params ");

            var cleanPt = FormatByRefPrefix(rawPt, pi, sb);
            onParam?.Invoke(i, cleanPt);

            sb.Append(cleanPt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
            if (pi?.DefaultValue != null)
            {
                var defText = ConstantAndAttributeReader.FormatDefaultValue(cleanPt, pi.DefaultValue, pi.IsIntegerConst, asm.Enums, ctx);
                sb.Append(" = ").Append(defText);
            }
        }
    }

    /// <summary>Per-position <c>NullableAttribute</c> flags win; otherwise the enclosing nullable context applies uniformly.</summary>
    private static NullableFlags EffectiveFlags(NullableFlags? attributeFlags, byte nullableContext)
        => attributeFlags ?? NullableFlags.Uniform(nullableContext);

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
        NullableFlags? NullFlags,
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
            var nullFlag = ConstantAndAttributeReader.ReadNullableFlags(md, attrs);
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

    private static bool IsInitOnlySetter(MethodDefinition setter)
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
        MethodAttributes.Public => Accessibilities.Public,
        MethodAttributes.Family or MethodAttributes.FamORAssem => Accessibilities.Protected,
        MethodAttributes.Assembly or MethodAttributes.FamANDAssem => Accessibilities.Internal,
        _ => Accessibilities.Private,
    };

    private static string GetFieldAccessibility(FieldAttributes attrs) => (attrs & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => Accessibilities.Public,
        FieldAttributes.Family or FieldAttributes.FamORAssem => Accessibilities.Protected,
        FieldAttributes.Assembly or FieldAttributes.FamANDAssem => Accessibilities.Internal,
        _ => Accessibilities.Private,
    };

    private static string PickHigherAccessibility(string? a, string? b)
    {
        static int Rank(string? acc) => acc switch
        {
            Accessibilities.Public => 4,
            Accessibilities.Protected => 3,
            Accessibilities.Internal => 2,
            Accessibilities.Private => 1,
            _ => 0,
        };
        return Rank(a) >= Rank(b) ? (a ?? Accessibilities.Private) : (b ?? Accessibilities.Private);
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

    /// <summary>Collects de-duplicated (type, role) references for the by-type reverse lookup table.</summary>
    private sealed class TypeRefCollector
    {
        private readonly List<MemberTypeRef> _items = new();
        private readonly HashSet<(string TypeFull, string Role)> _seen = new(TypeRefComparer.Instance);

        public IReadOnlyList<MemberTypeRef> Items => _items;

        public void Add(IReadOnlyList<(string FullName, string SimpleName)> refs, string role)
        {
            foreach (var (full, simple) in refs)
            {
                if (!string.IsNullOrEmpty(simple) && _seen.Add((full, role)))
                    _items.Add(new MemberTypeRef(full, simple, role));
            }
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
