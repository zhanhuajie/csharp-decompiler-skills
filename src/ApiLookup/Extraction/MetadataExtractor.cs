using System.Globalization;
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
            var enumLookup = BuildEnumLookup(md);

            var types = new List<ExtractedType>();
            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                var rawName = md.GetString(td.Name);
                if (ShouldSkipType(md, td, rawName, includeNonPublic))
                    continue;

                var extracted = ExtractType(md, td, xmlDocs, enumLookup, includeNonPublic);
                if (extracted != null)
                    types.Add(extracted);
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

    private sealed class EnumLookup
    {
        private readonly Dictionary<string, Dictionary<string, string>> _byFull = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>?> _bySimple = new(StringComparer.Ordinal);

        public void Register(string fullName, string simpleName, Dictionary<string, string> values)
        {
            if (values.Count == 0) return;
            _byFull[fullName] = values;
            if (!_bySimple.TryAdd(simpleName, values))
                _bySimple[simpleName] = null; // Ambiguous simple name
        }

        public string? ResolveMemberName(string typeDisplayName, string numericValue)
        {
            var bare = StripGenerics(typeDisplayName).TrimEnd('?', '&', '*');
            if (_byFull.TryGetValue(bare, out var fullMap) && fullMap.TryGetValue(numericValue, out var name))
                return name;

            var simple = ExtractSimpleTypeName(bare);
            if (_bySimple.TryGetValue(simple, out var simpleMap) && simpleMap != null && simpleMap.TryGetValue(numericValue, out name))
                return name;

            return null;
        }
    }

    private static EnumLookup BuildEnumLookup(MetadataReader md)
    {
        var lookup = new EnumLookup();
        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            if (td.BaseType.Kind != HandleKind.TypeReference) continue;
            var baseTr = md.GetTypeReference((TypeReferenceHandle)td.BaseType);
            if (md.GetString(baseTr.Name) != "Enum" || md.GetString(baseTr.Namespace) != "System")
                continue;

            var (_, _, displayFull, bareFull, _, simpleName) = BuildTypeNames(md, td);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if ((f.Attributes & FieldAttributes.Literal) == 0) continue;
                var ch = f.GetDefaultValue();
                if (ch.IsNil) continue;
                var val = ReadConstantValue(md, md.GetConstant(ch));
                if (val != null)
                    map.TryAdd(val, md.GetString(f.Name));
            }
            lookup.Register(bareFull, simpleName, map);
        }
        return lookup;
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

    private static ExtractedType? ExtractType(
        MetadataReader md,
        TypeDefinition td,
        XmlDocBook xmlDocs,
        EnumLookup enumLookup,
        bool includeNonPublic)
    {
        var (ns, clrName, displayFullName, bareFullName, displayName, simpleName) = BuildTypeNames(md, td);

        var genericParamNames = ReadGenericParamNames(md, td.GetGenericParameters());
        var genericCtx = new GenericContext(genericParamNames, Array.Empty<string>());

        var (baseFull, baseBare, baseSimple) = ResolveBaseType(md, td, genericCtx);
        var kind = DetermineTypeKind(td, baseFull);
        var visibility = GetEffectiveVisibility(md, td);
        var modifiers = DetermineTypeModifiers(td, kind);
        var (interfaces, interfaceRefs) = ResolveInterfaces(md, td, genericCtx);
        var genericConstraints = ReadGenericConstraints(md, td.GetGenericParameters(), genericCtx);
        var isExtContainer = HasAttribute(md, td.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute");
        var defaultMemberName = ReadDefaultMemberName(md, td) ?? "Item";

        var docId = "T:" + clrName.Replace('+', '.');
        var typeDoc = xmlDocs.Find(docId);

        var delegateSignature = kind == "delegate"
            ? ExtractDelegateSignature(md, td, visibility, displayName, genericConstraints, genericCtx)
            : null;

        var members = ExtractMembers(
            md, td, clrName, simpleName, kind, genericCtx, isExtContainer,
            defaultMemberName, xmlDocs, enumLookup, includeNonPublic);

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

    internal static (string ns, string clrName, string displayFull, string bareFull, string displayName, string simpleName)
        BuildTypeNames(MetadataReader md, TypeDefinition td)
    {
        var chain = new List<TypeDefinition>();
        var current = td;
        while (true)
        {
            chain.Add(current);
            if (!current.IsNested) break;
            current = md.GetTypeDefinition(current.GetDeclaringType());
        }
        chain.Reverse();

        var ns = md.GetString(chain[0].Namespace);
        var clrParts = new List<string>(chain.Count);
        var dispParts = new List<string>(chain.Count);
        var bareParts = new List<string>(chain.Count);
        var totalOuterParams = 0;

        for (var i = 0; i < chain.Count; i++)
        {
            var cur = chain[i];
            var raw = md.GetString(cur.Name);
            clrParts.Add(raw);

            var bare = DisplaySignatureProvider.StripGenericArity(raw);
            bareParts.Add(bare);

            var allGp = ReadGenericParamNames(md, cur.GetGenericParameters());
            var ownGp = allGp.Count > totalOuterParams
                ? allGp.Skip(totalOuterParams).ToList()
                : new List<string>();
            totalOuterParams = allGp.Count;

            dispParts.Add(ownGp.Count > 0 ? $"{bare}<{string.Join(", ", ownGp)}>" : bare);
        }

        var clrJoined = string.Join("+", clrParts);
        var dispJoined = string.Join(".", dispParts);
        var bareJoined = string.Join(".", bareParts);
        var clrName = string.IsNullOrEmpty(ns) ? clrJoined : $"{ns}.{clrJoined}";
        var displayFull = string.IsNullOrEmpty(ns) ? dispJoined : $"{ns}.{dispJoined}";
        var bareFull = string.IsNullOrEmpty(ns) ? bareJoined : $"{ns}.{bareJoined}";
        var displayName = dispParts[^1];
        var simpleName = bareParts[^1];

        return (ns, clrName, displayFull, bareFull, displayName, simpleName);
    }

    private static List<string> ReadGenericParamNames(MetadataReader md, GenericParameterHandleCollection handles)
    {
        var list = new List<string>(handles.Count);
        foreach (var h in handles)
            list.Add(md.GetString(md.GetGenericParameter(h).Name));
        return list;
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
                var typeName = ResolveEntityDisplayName(md, constraint.Type, ctx);
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
        GenericContext genericCtx)
    {
        foreach (var mh in td.GetMethods())
        {
            var method = md.GetMethodDefinition(mh);
            if (md.GetString(method.Name) != "Invoke") continue;

            MethodSignature<string> sig;
            try { sig = method.DecodeSignature(DisplaySignatureProvider.Instance, genericCtx); }
            catch { return null; }

            var paramInfos = ReadParamMetadata(md, method);
            var sb = new StringBuilder();
            sb.Append(visibility).Append(" delegate ").Append(sig.ReturnType).Append(' ').Append(displayName).Append('(');
            for (var i = 0; i < sig.ParameterTypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var rawPt = sig.ParameterTypes[i];
                paramInfos.TryGetValue(i + 1, out var pi);

                if (pi?.IsParams == true) sb.Append("params ");
                var cleanPt = FormatByRefPrefix(rawPt, pi, sb);
                sb.Append(cleanPt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
                if (pi?.DefaultValue != null)
                    sb.Append(" = ").Append(pi.DefaultValue);
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
        var full = ResolveEntityFullName(md, td.BaseType, ctx);
        if (string.IsNullOrEmpty(full)) return (null, null, null);
        var bare = StripGenerics(full);
        var simple = ExtractSimpleTypeName(bare);
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
            var disp = ResolveEntityDisplayName(md, impl.Interface, ctx);
            if (!string.IsNullOrEmpty(disp))
                displayNames.Add(disp);

            var full = ResolveEntityFullName(md, impl.Interface, ctx);
            if (!string.IsNullOrEmpty(full))
            {
                var bare = StripGenerics(full);
                var simple = ExtractSimpleTypeName(bare);
                if (!string.IsNullOrEmpty(bare) && seenRefs.Add(bare))
                    refs.Add(new InterfaceRef(bare, simple));
            }
        }
        return (displayNames, refs);
    }

    private static string ResolveEntityFullName(MetadataReader md, EntityHandle handle, GenericContext ctx)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullNameDisplaySignatureProvider.Instance.GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => FullNameDisplaySignatureProvider.Instance.GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => FullNameDisplaySignatureProvider.Instance.GetTypeFromSpecification(md, ctx, (TypeSpecificationHandle)handle, 0),
            _ => string.Empty,
        };
    }

    private static string ResolveEntityDisplayName(MetadataReader md, EntityHandle handle, GenericContext ctx)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => DisplaySignatureProvider.Instance.GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => DisplaySignatureProvider.Instance.GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => DisplaySignatureProvider.Instance.GetTypeFromSpecification(md, ctx, (TypeSpecificationHandle)handle, 0),
            _ => string.Empty,
        };
    }

    private static string DetermineTypeKind(TypeDefinition td, string? baseFull)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0) return "interface";
        if (baseFull == "System.Enum") return "enum";
        if (baseFull == "System.ValueType") return "struct";
        if (baseFull is "System.MulticastDelegate" or "System.Delegate") return "delegate";
        return "class";
    }

    private static string DetermineTypeModifiers(TypeDefinition td, string kind)
    {
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
        string clrTypeName,
        string simpleTypeName,
        string typeKind,
        GenericContext typeGenericCtx,
        bool isExtContainer,
        string defaultMemberName,
        XmlDocBook xmlDocs,
        EnumLookup enumLookup,
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
                md, prop, accessors, docTypePrefix, typeGenericCtx, isInterfaceType, defaultMemberName, xmlDocs, includeNonPublic);
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
                md, ev, accessors, docTypePrefix, typeGenericCtx, isInterfaceType, xmlDocs, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        foreach (var fh in td.GetFields())
        {
            var field = md.GetFieldDefinition(fh);
            var extracted = ExtractField(md, field, typeKind, docTypePrefix, typeGenericCtx, xmlDocs, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        foreach (var mh in td.GetMethods())
        {
            if (accessorMethods.Contains(mh)) continue;
            var method = md.GetMethodDefinition(mh);
            var extracted = ExtractMethod(
                md, method, simpleTypeName, docTypePrefix, typeGenericCtx, isInterfaceType, isExtContainer, xmlDocs, enumLookup, includeNonPublic);
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
        XmlDocBook xmlDocs,
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
        try
        {
            dispSig = prop.DecodeSignature(DisplaySignatureProvider.Instance, ctx);
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

        var returnType = dispSig.ReturnType;
        var returnSimple = ExtractSimpleTypeName(returnType);

        var isInit = setter.HasValue && IsInitOnlySetter(md, setter.Value);
        var accessorText = BuildPropertyAccessors(getterAcc, setterAcc, bestAcc, isInit);
        var paramNames = primaryMethod.HasValue ? ReadParamMetadata(md, primaryMethod.Value) : new Dictionary<int, ParamInfo>();

        var sb = new StringBuilder();
        sb.Append(bestAcc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append(returnType).Append(' ');

        var paramSimpleList = new List<string>();
        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeSimple, string Role)>(TypeRefComparer.Instance);
        AddTypeRefs(typeRefs, seenRefs, returnType, "return", ctx);

        string docId;
        if (dispSig.ParameterTypes.Length > 0)
        {
            var propDisplayName = string.Equals(name, defaultMemberName, StringComparison.Ordinal) ? "this" : name;
            sb.Append(propDisplayName).Append('[');
            for (var i = 0; i < dispSig.ParameterTypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var pt = dispSig.ParameterTypes[i];
                paramSimpleList.Add(ExtractSimpleTypeName(pt));
                AddTypeRefs(typeRefs, seenRefs, pt, "param", ctx);
                paramNames.TryGetValue(i + 1, out var pi);
                sb.Append(pt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
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
        XmlDocBook xmlDocs,
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
        var handlerType = ev.Type.IsNil ? "EventHandler" : ResolveEntityDisplayName(md, ev.Type, ctx);
        var handlerSimple = ExtractSimpleTypeName(handlerType);

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append("event ").Append(handlerType).Append(' ').Append(name);

        var docId = $"E:{docTypePrefix}.{name}";
        var doc = xmlDocs.Find(docId);

        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeSimple, string Role)>(TypeRefComparer.Instance);
        AddTypeRefs(typeRefs, seenRefs, handlerType, "return", ctx);

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
        XmlDocBook xmlDocs,
        bool includeNonPublic)
    {
        var name = md.GetString(field.Name);
        if (name == "value__" || name.StartsWith('<')) return null;

        var acc = GetFieldAccessibility(field.Attributes);
        if (!includeNonPublic && acc is not ("public" or "protected"))
            return null;

        string fieldType;
        try { fieldType = field.DecodeSignature(DisplaySignatureProvider.Instance, ctx); }
        catch { return null; }

        var isLiteral = (field.Attributes & FieldAttributes.Literal) != 0;
        var isStatic = (field.Attributes & FieldAttributes.Static) != 0;
        var isInitOnly = (field.Attributes & FieldAttributes.InitOnly) != 0;

        string? constVal = null;
        var constHandle = field.GetDefaultValue();
        if (!constHandle.IsNil)
            constVal = ReadConstantValue(md, md.GetConstant(constHandle));

        var docId = $"F:{docTypePrefix}.{name}";
        var doc = xmlDocs.Find(docId);

        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeSimple, string Role)>(TypeRefComparer.Instance);
        AddTypeRefs(typeRefs, seenRefs, fieldType, "return", ctx);

        if (typeKind == "enum" && isLiteral)
        {
            var sig = constVal != null ? $"{name} = {constVal}" : name;
            return new ExtractedMember(
                Kind: "enum",
                Name: name,
                Accessibility: "public",
                Modifiers: "const",
                ReturnType: fieldType,
                ReturnSimple: ExtractSimpleTypeName(fieldType),
                Signature: sig,
                ParamTypes: string.Empty,
                TypeRefs: typeRefs,
                ExtTargetType: null,
                ExtTargetSimple: null,
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
            ReturnSimple: ExtractSimpleTypeName(fieldType),
            Signature: sb.ToString(),
            ParamTypes: string.Empty,
            TypeRefs: typeRefs,
            ExtTargetType: null,
            ExtTargetSimple: null,
            EnumValue: constVal,
            Summary: doc?.Summary,
            ParamsDoc: null,
            ReturnsDoc: null,
            DocId: docId);
    }

    private static ExtractedMember? ExtractMethod(
        MetadataReader md,
        MethodDefinition method,
        string simpleTypeName,
        string docTypePrefix,
        GenericContext typeCtx,
        bool isInterfaceType,
        bool isExtContainer,
        XmlDocBook xmlDocs,
        EnumLookup enumLookup,
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

        var methodGp = ReadGenericParamNames(md, method.GetGenericParameters());
        var fullCtx = methodGp.Count > 0 ? typeCtx with { MethodParams = methodGp } : typeCtx;

        MethodSignature<string> dispSig;
        try
        {
            dispSig = method.DecodeSignature(DisplaySignatureProvider.Instance, fullCtx);
        }
        catch
        {
            return null;
        }

        var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
        var isExt = isStatic && isExtContainer && dispSig.ParameterTypes.Length > 0 &&
                    HasAttribute(md, method.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute");

        MethodSignature<string>? docSig = null;
        if (xmlDocs.Count > 0 || isExt)
        {
            try { docSig = method.DecodeSignature(XmlDocSignatureProvider.Instance, fullCtx); }
            catch { docSig = null; }
        }

        var paramInfos = ReadParamMetadata(md, method);
        var modifiers = isCtor ? string.Empty : GetMethodModifiers(method.Attributes, isInterfaceType);
        var methodConstraints = methodGp.Count > 0 ? ReadGenericConstraints(md, method.GetGenericParameters(), fullCtx) : string.Empty;

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        if (isCtor)
        {
            sb.Append(simpleTypeName);
        }
        else
        {
            sb.Append(dispSig.ReturnType).Append(' ').Append(name);
            if (methodGp.Count > 0)
                sb.Append('<').Append(string.Join(", ", methodGp)).Append('>');
        }

        var returnType = isCtor ? simpleTypeName : dispSig.ReturnType;
        var typeRefs = new List<MemberTypeRef>();
        var seenRefs = new HashSet<(string TypeSimple, string Role)>(TypeRefComparer.Instance);
        AddTypeRefs(typeRefs, seenRefs, returnType, "return", fullCtx);

        sb.Append('(');
        var paramSimpleList = new List<string>(dispSig.ParameterTypes.Length);
        for (var i = 0; i < dispSig.ParameterTypes.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var rawPt = dispSig.ParameterTypes[i];
            paramInfos.TryGetValue(i + 1, out var pi);

            if (i == 0 && isExt) sb.Append("this ");
            if (pi?.IsParams == true) sb.Append("params ");

            var cleanPt = FormatByRefPrefix(rawPt, pi, sb);
            paramSimpleList.Add(ExtractSimpleTypeName(cleanPt));
            AddTypeRefs(typeRefs, seenRefs, cleanPt, "param", fullCtx);

            sb.Append(cleanPt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
            if (pi?.DefaultValue != null)
            {
                var defText = FormatDefaultValue(cleanPt, pi.DefaultValue, enumLookup);
                sb.Append(" = ").Append(defText);
            }
        }
        sb.Append(')');
        if (!string.IsNullOrEmpty(methodConstraints))
            sb.Append(' ').Append(methodConstraints);

        string? extTargetFull = null;
        string? extTargetSimple = null;
        if (isExt && docSig.HasValue && docSig.Value.ParameterTypes.Length > 0)
        {
            extTargetFull = NormalizeDocTargetType(docSig.Value.ParameterTypes[0]);
            extTargetSimple = ExtractSimpleTypeName(extTargetFull);
        }

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
            ReturnSimple: ExtractSimpleTypeName(returnType),
            Signature: sb.ToString(),
            ParamTypes: string.Join("|", paramSimpleList),
            TypeRefs: typeRefs,
            ExtTargetType: extTargetFull,
            ExtTargetSimple: extTargetSimple,
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: FormatParamsDoc(doc),
            ReturnsDoc: doc?.Returns,
            DocId: docId);
    }

    private static string FormatByRefPrefix(string rawPt, ParamInfo? pi, StringBuilder sb)
    {
        if (!rawPt.EndsWith('&')) return rawPt;
        var cleanPt = rawPt[..^1];
        if (pi?.IsOut == true && pi.IsIn != true) sb.Append("out ");
        else if (pi?.IsIn == true && pi.IsOut != true) sb.Append("in ");
        else sb.Append("ref ");
        return cleanPt;
    }

    private static string FormatDefaultValue(string paramType, string rawDefault, EnumLookup enumLookup)
    {
        var memberName = enumLookup.ResolveMemberName(paramType, rawDefault);
        return memberName != null ? $"{paramType.TrimEnd('?')}.{memberName}" : rawDefault;
    }

    private sealed record ParamInfo(string Name, bool IsOut, bool IsIn, bool IsParams, string? DefaultValue);

    private static Dictionary<int, ParamInfo> ReadParamMetadata(MetadataReader md, MethodDefinition method)
    {
        var map = new Dictionary<int, ParamInfo>();
        foreach (var ph in method.GetParameters())
        {
            var p = md.GetParameter(ph);
            if (p.SequenceNumber == 0) continue;

            var pName = md.GetString(p.Name);
            var isOut = (p.Attributes & ParameterAttributes.Out) != 0;
            var isIn = (p.Attributes & ParameterAttributes.In) != 0;
            var isParams = HasAttribute(md, p.GetCustomAttributes(), "System", "ParamArrayAttribute");

            string? defVal = null;
            if ((p.Attributes & ParameterAttributes.HasDefault) != 0)
            {
                var ch = p.GetDefaultValue();
                if (!ch.IsNil) defVal = ReadConstantValue(md, md.GetConstant(ch));
            }
            else if ((p.Attributes & ParameterAttributes.Optional) != 0)
            {
                defVal = "default";
            }

            map[p.SequenceNumber] = new ParamInfo(pName, isOut, isIn, isParams, defVal);
        }
        return map;
    }

    private static string? ReadConstantValue(MetadataReader md, Constant constant)
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

    private static string? ReadDefaultMemberName(MetadataReader md, TypeDefinition td)
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

    private static bool HasAttribute(
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

    private static bool IsAttributeType(MetadataReader md, CustomAttribute ca, string targetNs, string targetName)
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

    private static string NormalizeDocTargetType(string docType)
    {
        var s = docType.TrimEnd('@', '*', '[', ']');
        var brace = s.IndexOf('{');
        if (brace >= 0) s = s[..brace];
        return DisplaySignatureProvider.StripGenericArity(s);
    }

    public static string StripGenerics(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || !fullName.Contains('<'))
            return fullName;

        var sb = new StringBuilder(fullName.Length);
        var depth = 0;
        foreach (var ch in fullName)
        {
            if (ch == '<')
                depth++;
            else if (ch == '>')
            {
                if (depth > 0) depth--;
            }
            else if (depth == 0)
                sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string ExtractSimpleTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return string.Empty;
        var s = typeName.Trim().TrimEnd('?', '&', '*');

        while (s.EndsWith(']'))
        {
            var open = s.LastIndexOf('[');
            if (open < 0) break;
            s = s[..open].TrimEnd('?', '&', '*');
        }

        s = StripGenerics(s).TrimEnd('?', '&', '*');
        var brace = s.IndexOf('{');
        if (brace >= 0) s = s[..brace];
        s = DisplaySignatureProvider.StripGenericArity(s);

        var lastSep = s.LastIndexOfAny(new[] { '.', '+' });
        var simple = lastSep >= 0 ? s[(lastSep + 1)..] : s;
        return DisplaySignatureProvider.NormalizePrimitiveAlias(simple);
    }

    public static List<string> ExtractReferencedSimpleTypeNames(string displayType, GenericContext? ctx = null)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(ReadOnlySpan<char> span)
        {
            var trimmed = span.Trim();
            if (trimmed.IsEmpty) return;

            var str = trimmed.ToString();
            var outerSimple = ExtractSimpleTypeName(str);
            if (!string.IsNullOrEmpty(outerSimple) &&
                !string.Equals(outerSimple, "void", StringComparison.OrdinalIgnoreCase) &&
                !IsGenericParamName(outerSimple, ctx) &&
                seen.Add(outerSimple))
            {
                results.Add(outerSimple);
            }

            var depth = 0;
            var ltStart = -1;
            for (var i = 0; i < str.Length; i++)
            {
                var ch = str[i];
                if (ch == '<')
                {
                    if (depth == 0) ltStart = i;
                    depth++;
                }
                else if (ch == '>')
                {
                    depth--;
                    if (depth == 0 && ltStart >= 0)
                    {
                        var inner = str.AsSpan(ltStart + 1, i - ltStart - 1);
                        SplitTopLevelArgs(inner, Collect);
                        ltStart = -1;
                    }
                }
            }
        }

        Collect(displayType.AsSpan());
        return results;
    }

    private delegate void SpanAction(ReadOnlySpan<char> span);

    private static void SplitTopLevelArgs(ReadOnlySpan<char> inner, SpanAction onArg)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') { if (depth > 0) depth--; }
            else if (ch == ',' && depth == 0)
            {
                onArg(inner[start..i]);
                start = i + 1;
            }
        }
        if (start < inner.Length)
            onArg(inner[start..]);
    }

    private static bool IsGenericParamName(string name, GenericContext? ctx)
    {
        if (ctx == null) return false;
        for (var i = 0; i < ctx.TypeParams.Count; i++)
            if (string.Equals(ctx.TypeParams[i], name, StringComparison.Ordinal)) return true;
        for (var i = 0; i < ctx.MethodParams.Count; i++)
            if (string.Equals(ctx.MethodParams[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void AddTypeRefs(
        List<MemberTypeRef> list,
        HashSet<(string TypeSimple, string Role)> seen,
        string displayType,
        string role,
        GenericContext ctx)
    {
        foreach (var simple in ExtractReferencedSimpleTypeNames(displayType, ctx))
        {
            if (seen.Add((simple, role)))
                list.Add(new MemberTypeRef(simple, role));
        }
    }

    private sealed class TypeRefComparer : IEqualityComparer<(string TypeSimple, string Role)>
    {
        public static readonly TypeRefComparer Instance = new();

        public bool Equals((string TypeSimple, string Role) x, (string TypeSimple, string Role) y)
            => string.Equals(x.TypeSimple, y.TypeSimple, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Role, y.Role, StringComparison.Ordinal);

        public int GetHashCode((string TypeSimple, string Role) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeSimple),
                StringComparer.Ordinal.GetHashCode(obj.Role));
    }
}
