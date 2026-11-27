using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ApiLookup;

public sealed record ExtractedAssembly(
    string Name,
    string Path,
    string Version,
    string? XmlPath,
    long FileSize,
    long MtimeUtc,
    IReadOnlyList<ExtractedType> Types);

public sealed record ExtractedType(
    string FullName,          // Display full name: Namespace.Outer.Inner<T>
    string ClrName,           // Metadata full name: Namespace.Outer+Inner`1
    string Namespace,
    string Name,              // Display name with generic params: List<T>
    string SimpleName,        // Bare name without generic arity: List
    string Kind,              // class | struct | interface | enum | delegate
    string Accessibility,     // public | protected | internal | private
    string Modifiers,         // static | abstract | sealed | ""
    string? BaseType,         // Full base type name (e.g. Autodesk.ProcessPower.PnP3dObjects.Part)
    string? BaseSimple,       // Simple base type name (e.g. Part)
    string Interfaces,        // Comma-separated display interface names
    string GenericParams,     // Comma-separated generic param names
    bool IsExtensionContainer,
    string? Summary,
    string? Remarks,
    string DocId,
    IReadOnlyList<ExtractedMember> Members);

public sealed record ExtractedMember(
    string Kind,              // ctor | method | property | field | event | enum
    string Name,
    string Accessibility,
    string Modifiers,         // static | virtual | override | abstract | const | readonly
    string ReturnType,        // Display return/field/property type
    string ReturnSimple,      // Normalized bare return type name for reverse lookup
    string Signature,         // Complete C# declaration line
    string ParamTypes,        // Pipe-separated normalized param type simple names for reverse lookup
    string? ExtTargetType,    // Full receiver type (if extension method)
    string? ExtTargetSimple,  // Simple receiver type (if extension method)
    string? EnumValue,        // Constant value for enum fields
    string? Summary,
    string? ParamsDoc,        // Formatted param docs
    string? ReturnsDoc,
    string DocId);

public static class MetadataExtractor
{
    public static ExtractedAssembly? Extract(string assemblyPath, bool includeNonPublic = false)
    {
        if (!File.Exists(assemblyPath)) return null;

        try
        {
            var fi = new FileInfo(assemblyPath);
            using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var peReader = new PEReader(fs);
            if (!peReader.HasMetadata) return null;

            var md = peReader.GetMetadataReader();
            if (!md.IsAssembly) return null;

            var asmDef = md.GetAssemblyDefinition();
            var asmName = md.GetString(asmDef.Name);
            var asmVersion = asmDef.Version.ToString();
            var xmlPath = XmlDocReader.FindXmlDocPath(assemblyPath);
            var xmlDocs = XmlDocReader.Load(xmlPath);

            var types = new List<ExtractedType>();
            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                var rawName = md.GetString(td.Name);
                if (ShouldSkipType(md, td, rawName, includeNonPublic))
                    continue;

                var extracted = ExtractType(md, handle, td, xmlDocs, includeNonPublic);
                if (extracted != null)
                    types.Add(extracted);
            }

            return new ExtractedAssembly(
                asmName,
                Path.GetFullPath(assemblyPath),
                asmVersion,
                xmlPath,
                fi.Length,
                fi.LastWriteTimeUtc.Ticks,
                types);
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldSkipType(MetadataReader md, TypeDefinition td, string rawName, bool includeNonPublic)
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

    private static string GetEffectiveVisibility(MetadataReader md, TypeDefinition td)
    {
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        if (!td.IsNested)
        {
            return vis == TypeAttributes.Public ? "public" : "internal";
        }

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
        TypeDefinitionHandle handle,
        TypeDefinition td,
        XmlDocBook xmlDocs,
        bool includeNonPublic)
    {
        var (ns, clrName, displayFullName, displayName, simpleName) = BuildTypeNames(md, td);

        var genericParamNames = ReadGenericParamNames(md, td.GetGenericParameters());
        var genericCtx = new GenericContext(genericParamNames, Array.Empty<string>());

        var (baseFull, baseSimple) = ResolveBaseType(md, td, genericCtx);
        var kind = DetermineTypeKind(td, baseFull);
        var visibility = GetEffectiveVisibility(md, td);
        var modifiers = DetermineTypeModifiers(td, kind);
        var interfaces = ResolveInterfaces(md, td, genericCtx);
        var isExtContainer = HasAttribute(md, td.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute");

        var docId = "T:" + clrName.Replace('+', '.');
        var typeDoc = xmlDocs.Find(docId);

        var members = ExtractMembers(md, td, clrName, simpleName, kind, genericCtx, isExtContainer, xmlDocs, includeNonPublic);

        return new ExtractedType(
            FullName: displayFullName,
            ClrName: clrName,
            Namespace: ns,
            Name: displayName,
            SimpleName: simpleName,
            Kind: kind,
            Accessibility: visibility,
            Modifiers: modifiers,
            BaseType: baseFull,
            BaseSimple: baseSimple,
            Interfaces: string.Join(", ", interfaces),
            GenericParams: string.Join(", ", genericParamNames),
            IsExtensionContainer: isExtContainer,
            Summary: typeDoc?.Summary,
            Remarks: typeDoc?.Remarks,
            DocId: docId,
            Members: members);
    }

    private static (string ns, string clrName, string displayFull, string displayName, string simpleName)
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
        var clrParts = new List<string>();
        var dispParts = new List<string>();
        var totalOuterParams = 0;

        for (var i = 0; i < chain.Count; i++)
        {
            var cur = chain[i];
            var raw = md.GetString(cur.Name);
            clrParts.Add(raw);

            var bare = DisplaySignatureProvider.StripGenericArity(raw);
            var allGp = ReadGenericParamNames(md, cur.GetGenericParameters());
            var ownGp = allGp.Count > totalOuterParams
                ? allGp.Skip(totalOuterParams).ToList()
                : new List<string>();
            totalOuterParams = allGp.Count;

            dispParts.Add(ownGp.Count > 0 ? $"{bare}<{string.Join(", ", ownGp)}>" : bare);
        }

        var clrJoined = string.Join("+", clrParts);
        var dispJoined = string.Join(".", dispParts);
        var clrName = string.IsNullOrEmpty(ns) ? clrJoined : $"{ns}.{clrJoined}";
        var displayFull = string.IsNullOrEmpty(ns) ? dispJoined : $"{ns}.{dispJoined}";
        var displayName = dispParts[^1];
        var simpleName = DisplaySignatureProvider.StripGenericArity(md.GetString(td.Name));

        return (ns, clrName, displayFull, displayName, simpleName);
    }

    private static List<string> ReadGenericParamNames(MetadataReader md, GenericParameterHandleCollection handles)
    {
        var list = new List<string>(handles.Count);
        foreach (var h in handles)
            list.Add(md.GetString(md.GetGenericParameter(h).Name));
        return list;
    }

    private static (string? full, string? simple) ResolveBaseType(MetadataReader md, TypeDefinition td, GenericContext ctx)
    {
        if (td.BaseType.IsNil) return (null, null);
        var full = ResolveEntityFullName(md, td.BaseType, ctx);
        if (string.IsNullOrEmpty(full)) return (null, null);
        var simple = ExtractSimpleTypeName(full);
        return (full, simple);
    }

    private static List<string> ResolveInterfaces(MetadataReader md, TypeDefinition td, GenericContext ctx)
    {
        var list = new List<string>();
        foreach (var ih in td.GetInterfaceImplementations())
        {
            var impl = md.GetInterfaceImplementation(ih);
            var name = ResolveEntityDisplayName(md, impl.Interface, ctx);
            if (!string.IsNullOrEmpty(name))
                list.Add(name);
        }
        return list;
    }

    private static string ResolveEntityFullName(MetadataReader md, EntityHandle handle, GenericContext ctx)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var td = md.GetTypeDefinition((TypeDefinitionHandle)handle);
                return BuildTypeNames(md, td).displayFull;
            }
            case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)handle);
                var name = DisplaySignatureProvider.StripGenericArity(md.GetString(tr.Name));
                if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                    return ResolveEntityFullName(md, tr.ResolutionScope, ctx) + "." + name;
                var ns = md.GetString(tr.Namespace);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            case HandleKind.TypeSpecification:
            {
                var ts = md.GetTypeSpecification((TypeSpecificationHandle)handle);
                return ts.DecodeSignature(XmlDocSignatureProvider.Instance, ctx)
                    .Replace('{', '<').Replace('}', '>');
            }
            default:
                return string.Empty;
        }
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
        XmlDocBook xmlDocs,
        bool includeNonPublic)
    {
        var members = new List<ExtractedMember>();
        var docTypePrefix = clrTypeName.Replace('+', '.');

        // Collect property & event accessor method handles so we don't duplicate them as methods
        var accessorMethods = new HashSet<MethodDefinitionHandle>();

        // 1. Properties
        foreach (var ph in td.GetProperties())
        {
            var prop = md.GetPropertyDefinition(ph);
            var accessors = prop.GetAccessors();
            if (!accessors.Getter.IsNil) accessorMethods.Add(accessors.Getter);
            if (!accessors.Setter.IsNil) accessorMethods.Add(accessors.Setter);

            var extracted = ExtractProperty(md, prop, accessors, docTypePrefix, typeGenericCtx, xmlDocs, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        // 2. Events
        foreach (var eh in td.GetEvents())
        {
            var ev = md.GetEventDefinition(eh);
            var accessors = ev.GetAccessors();
            if (!accessors.Adder.IsNil) accessorMethods.Add(accessors.Adder);
            if (!accessors.Remover.IsNil) accessorMethods.Add(accessors.Remover);
            if (!accessors.Raiser.IsNil) accessorMethods.Add(accessors.Raiser);

            var extracted = ExtractEvent(md, ev, accessors, docTypePrefix, typeGenericCtx, xmlDocs, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        // 3. Fields / Enum values
        foreach (var fh in td.GetFields())
        {
            var field = md.GetFieldDefinition(fh);
            var extracted = ExtractField(md, field, typeKind, docTypePrefix, typeGenericCtx, xmlDocs, includeNonPublic);
            if (extracted != null) members.Add(extracted);
        }

        // 4. Methods & Constructors
        foreach (var mh in td.GetMethods())
        {
            if (accessorMethods.Contains(mh)) continue;
            var method = md.GetMethodDefinition(mh);
            var extracted = ExtractMethod(md, method, simpleTypeName, docTypePrefix, typeGenericCtx, isExtContainer, xmlDocs, includeNonPublic);
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
        var modifiers = primaryMethod.HasValue ? GetMethodModifiers(primaryMethod.Value.Attributes) : string.Empty;

        MethodSignature<string> dispSig;
        MethodSignature<string> docSig;
        try
        {
            dispSig = prop.DecodeSignature(DisplaySignatureProvider.Instance, ctx);
            docSig = prop.DecodeSignature(XmlDocSignatureProvider.Instance, ctx);
        }
        catch
        {
            return null;
        }

        var returnType = dispSig.ReturnType;
        var returnSimple = ExtractSimpleTypeName(returnType);

        var accessorText = BuildPropertyAccessors(getterAcc, setterAcc, bestAcc);
        var paramNames = primaryMethod.HasValue ? ReadParamMetadata(md, primaryMethod.Value) : new Dictionary<int, ParamInfo>();

        var sb = new StringBuilder();
        sb.Append(bestAcc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append(returnType).Append(' ');

        var paramSimpleList = new List<string>();
        string docId;
        if (dispSig.ParameterTypes.Length > 0)
        {
            sb.Append("this[");
            for (var i = 0; i < dispSig.ParameterTypes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var pt = dispSig.ParameterTypes[i];
                paramSimpleList.Add(ExtractSimpleTypeName(pt));
                paramNames.TryGetValue(i + 1, out var pi);
                sb.Append(pt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
            }
            sb.Append(']');
            docId = $"P:{docTypePrefix}.{name}({string.Join(",", docSig.ParameterTypes)})";
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
        XmlDocBook xmlDocs,
        bool includeNonPublic)
    {
        MethodDefinition? adder = accessors.Adder.IsNil ? null : md.GetMethodDefinition(accessors.Adder);
        var acc = adder.HasValue ? GetMethodAccessibility(adder.Value.Attributes) : "public";
        if (!includeNonPublic && acc is not ("public" or "protected"))
            return null;

        var name = md.GetString(ev.Name);
        var modifiers = adder.HasValue ? GetMethodModifiers(adder.Value.Attributes) : string.Empty;
        var handlerType = ev.Type.IsNil ? "EventHandler" : ResolveEntityDisplayName(md, ev.Type, ctx);
        var handlerSimple = ExtractSimpleTypeName(handlerType);

        var sb = new StringBuilder();
        sb.Append(acc).Append(' ');
        if (!string.IsNullOrEmpty(modifiers)) sb.Append(modifiers).Append(' ');
        sb.Append("event ").Append(handlerType).Append(' ').Append(name);

        var docId = $"E:{docTypePrefix}.{name}";
        var doc = xmlDocs.Find(docId);

        return new ExtractedMember(
            Kind: "event",
            Name: name,
            Accessibility: acc,
            Modifiers: modifiers,
            ReturnType: handlerType,
            ReturnSimple: handlerSimple,
            Signature: sb.ToString(),
            ParamTypes: string.Empty,
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
        bool isExtContainer,
        XmlDocBook xmlDocs,
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
        MethodSignature<string> docSig;
        try
        {
            dispSig = method.DecodeSignature(DisplaySignatureProvider.Instance, fullCtx);
            docSig = method.DecodeSignature(XmlDocSignatureProvider.Instance, fullCtx);
        }
        catch
        {
            return null;
        }

        var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
        var isExt = isStatic && isExtContainer && dispSig.ParameterTypes.Length > 0 &&
                    HasAttribute(md, method.GetCustomAttributes(), "System.Runtime.CompilerServices", "ExtensionAttribute");

        var paramInfos = ReadParamMetadata(md, method);
        var modifiers = isCtor ? string.Empty : GetMethodModifiers(method.Attributes);

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

        sb.Append('(');
        var paramSimpleList = new List<string>(dispSig.ParameterTypes.Length);
        for (var i = 0; i < dispSig.ParameterTypes.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var rawPt = dispSig.ParameterTypes[i];
            paramInfos.TryGetValue(i + 1, out var pi);

            if (i == 0 && isExt) sb.Append("this ");
            if (pi?.IsParams == true) sb.Append("params ");

            var cleanPt = rawPt;
            if (rawPt.EndsWith('&'))
            {
                cleanPt = rawPt[..^1];
                if (pi?.IsOut == true && pi.IsIn != true) sb.Append("out ");
                else if (pi?.IsIn == true && pi.IsOut != true) sb.Append("in ");
                else sb.Append("ref ");
            }

            paramSimpleList.Add(ExtractSimpleTypeName(cleanPt));
            sb.Append(cleanPt).Append(' ').Append(string.IsNullOrEmpty(pi?.Name) ? $"arg{i}" : pi.Name);
            if (pi?.DefaultValue != null)
                sb.Append(" = ").Append(pi.DefaultValue);
        }
        sb.Append(')');

        string? extTargetFull = null;
        string? extTargetSimple = null;
        if (isExt && docSig.ParameterTypes.Length > 0)
        {
            extTargetFull = NormalizeDocTargetType(docSig.ParameterTypes[0]);
            extTargetSimple = ExtractSimpleTypeName(extTargetFull);
        }

        var docMethodName = isCtor ? "#ctor" : name;
        if (methodGp.Count > 0) docMethodName += $"``{methodGp.Count}";
        var docId = docSig.ParameterTypes.Length > 0
            ? $"M:{docTypePrefix}.{docMethodName}({string.Join(",", docSig.ParameterTypes)})"
            : $"M:{docTypePrefix}.{docMethodName}";

        var fbKey = XmlDocReader.BuildMethodFallbackKey(docId);
        var doc = xmlDocs.Find(docId, fbKey);

        var returnType = isCtor ? simpleTypeName : dispSig.ReturnType;
        return new ExtractedMember(
            Kind: isCtor ? "ctor" : "method",
            Name: isCtor ? simpleTypeName : name,
            Accessibility: acc,
            Modifiers: modifiers,
            ReturnType: returnType,
            ReturnSimple: ExtractSimpleTypeName(returnType),
            Signature: sb.ToString(),
            ParamTypes: string.Join("|", paramSimpleList),
            ExtTargetType: extTargetFull,
            ExtTargetSimple: extTargetSimple,
            EnumValue: null,
            Summary: doc?.Summary,
            ParamsDoc: FormatParamsDoc(doc),
            ReturnsDoc: doc?.Returns,
            DocId: docId);
    }

    private sealed record ParamInfo(string Name, bool IsOut, bool IsIn, bool IsParams, string? DefaultValue);

    private static Dictionary<int, ParamInfo> ReadParamMetadata(MetadataReader md, MethodDefinition method)
    {
        var map = new Dictionary<int, ParamInfo>();
        foreach (var ph in method.GetParameters())
        {
            var p = md.GetParameter(ph);
            if (p.SequenceNumber == 0) continue; // return param

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
                ConstantTypeCode.Char => $"'{br.ReadChar()}'",
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
                ConstantTypeCode.String => $"\"{br.ReadUTF16(br.Length)}\"",
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAttribute(
        MetadataReader md,
        CustomAttributeHandleCollection attrs,
        string targetNs,
        string targetName)
    {
        foreach (var ah in attrs)
        {
            var ca = md.GetCustomAttribute(ah);
            EntityHandle typeHandle = default;
            if (ca.Constructor.Kind == HandleKind.MemberReference)
                typeHandle = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
            else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
                typeHandle = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType();

            if (typeHandle.Kind == HandleKind.TypeReference)
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)typeHandle);
                if (md.GetString(tr.Name) == targetName && md.GetString(tr.Namespace) == targetNs)
                    return true;
            }
            else if (typeHandle.Kind == HandleKind.TypeDefinition)
            {
                var td = md.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                if (md.GetString(td.Name) == targetName && md.GetString(td.Namespace) == targetNs)
                    return true;
            }
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

    private static string BuildPropertyAccessors(string? getterAcc, string? setterAcc, string bestAcc)
    {
        var parts = new List<string>();
        if (getterAcc != null)
            parts.Add(getterAcc == bestAcc ? "get;" : $"{getterAcc} get;");
        if (setterAcc != null)
            parts.Add(setterAcc == bestAcc ? "set;" : $"{setterAcc} set;");
        return string.Join(" ", parts);
    }

    private static string GetMethodModifiers(MethodAttributes attrs)
    {
        if ((attrs & MethodAttributes.Static) != 0) return "static";
        if ((attrs & MethodAttributes.Abstract) != 0) return "abstract";
        if ((attrs & MethodAttributes.Virtual) != 0)
        {
            if ((attrs & MethodAttributes.NewSlot) != 0) return "virtual";
            return "override";
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

    public static string ExtractSimpleTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return string.Empty;
        var s = typeName.Trim().TrimEnd('?', '&', '*');
        while (s.EndsWith("[]", StringComparison.Ordinal) || s.EndsWith("[,]", StringComparison.Ordinal))
        {
            var idx = s.LastIndexOf('[');
            s = idx >= 0 ? s[..idx] : s;
        }
        var lt = s.IndexOf('<');
        if (lt >= 0) s = s[..lt];
        var brace = s.IndexOf('{');
        if (brace >= 0) s = s[..brace];
        s = DisplaySignatureProvider.StripGenericArity(s);
        var lastSep = s.LastIndexOfAny(new[] { '.', '+' });
        return lastSep >= 0 ? s[(lastSep + 1)..] : s;
    }
}
