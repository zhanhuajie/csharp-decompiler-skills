namespace ApiLookup;

/// <summary>
/// Command implementations. Argument shape (option applicability, positional arity) is already validated by
/// <see cref="CliParser"/>, so handlers only orchestrate source resolution, index access, and output.
/// </summary>
internal static class CommandHandlers
{
    public static int RunIndex(CommandContext ctx)
    {
        var opts = ctx.Options;
        if (opts.ListIndexes)
        {
            OutputFormatter.WriteIndexList(IndexCatalog.ListIndexes(), opts.Json, ctx.Out);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(opts.DropIndex))
        {
            var dropped = IndexCatalog.DropNamedIndex(opts.DropIndex);
            OutputFormatter.WriteDropIndex(new DropIndexResult(opts.DropIndex, dropped), opts.Json, ctx.Out);
            return dropped ? 0 : 1;
        }

        if (opts.PruneAuto)
        {
            OutputFormatter.WritePruneResult(IndexCatalog.PruneAutoIndexes(), opts.Json, ctx.Out);
            return 0;
        }

        if (!HasExplicitSources(opts))
            throw new ArgumentException("index requires at least one source (--dir, --project, or --dll).");

        var (store, stats) = OpenOrBuildIndex(ctx);
        using (store)
        {
            OutputFormatter.WriteIndexStats(stats, opts.Json, ctx.Out);
        }
        return 0;
    }

    public static int RunSearch(CommandContext ctx)
    {
        var opts = ctx.Options;
        var query = string.Join(' ', opts.Positional);
        using var store = OpenIndexForQuery(ctx);
        var hits = OutputFormatter.ToSearchHits(
            store.Search(query, opts.KindFilter, opts.NamespaceFilter, opts.AssemblyFilter, opts.Limit ?? 30));

        if (opts.ListRefs)
            OutputFormatter.WriteSearchHitsWithRefs(query, store.ListAssemblies(), hits, opts.Json, ctx.Out);
        else
            OutputFormatter.WriteSearchHits(query, hits, opts.Json, ctx.Out);
        return 0;
    }

    public static int RunShow(CommandContext ctx)
    {
        var opts = ctx.Options;
        using var store = OpenIndexForQuery(ctx);

        var exitCode = TryResolveSingleType(store, ctx, out var target);
        if (exitCode != 0 || target == null)
            return exitCode;

        var ownMembers = store.GetMembers(target.Id, opts.MemberFilter);

        var baseChain = (opts.IncludeInherited || opts.IncludeExtensions)
            ? store.GetBaseTypeChain(target)
            : new List<TypeRow>();

        var inheritedGroups = new List<(TypeRow, IReadOnlyList<MemberRow>)>();
        if (opts.IncludeInherited)
        {
            foreach (var b in baseChain)
            {
                var baseMembers = store.GetMembers(b.Id, opts.MemberFilter)
                    .Where(m => m.Kind != MemberKinds.Constructor)
                    .ToList();
                if (baseMembers.Count > 0)
                    inheritedGroups.Add((b, baseMembers));
            }
        }

        var extMethods = opts.IncludeExtensions
            ? store.GetExtensionMethods(target, baseChain, opts.MemberFilter)
            : Array.Empty<MemberRow>();

        var memberFiltered = !string.IsNullOrWhiteSpace(opts.MemberFilter);
        OutputFormatter.WriteTypeDetail(
            target, ownMembers, inheritedGroups, extMethods,
            opts.ShowFullDocs, opts.Json, memberFiltered, ctx.Out);
        return 0;
    }

    public static int RunHierarchy(CommandContext ctx)
    {
        using var store = OpenIndexForQuery(ctx);

        var exitCode = TryResolveSingleType(store, ctx, out var target);
        if (exitCode != 0 || target == null)
            return exitCode;

        var result = store.GetHierarchy(target, ctx.Options.Limit ?? 100);
        OutputFormatter.WriteHierarchy(result, ctx.Options.Json, ctx.Out);
        return 0;
    }

    public static int RunByType(CommandContext ctx)
    {
        var opts = ctx.Options;
        var typeName = opts.Positional[0];
        using var store = OpenIndexForQuery(ctx);

        var members = store.FindByType(typeName, opts.Role, opts.NamespaceFilter, opts.AssemblyFilter, opts.Limit ?? 40);
        OutputFormatter.WriteByType(typeName, opts.Role, members, opts.Json, ctx.Out);
        return 0;
    }

    public static int RunRefs(CommandContext ctx)
    {
        using var store = OpenIndexForQuery(ctx);
        OutputFormatter.WriteAssemblies(store.ListAssemblies(), ctx.Options.Json, ctx.Out);
        return 0;
    }

    public static int RunLocate(CommandContext ctx)
    {
        var opts = ctx.Options;
        var typeQuery = opts.Positional[0];
        var limit = opts.Limit ?? 100;

        if (HasExplicitSources(opts))
        {
            var resolved = ResolveSources(ctx);

            IReadOnlyList<ResolvedRefGroup>? refGroups = null;
            if (opts.ListRefs)
            {
                refGroups = resolved.Assemblies
                    .GroupBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ResolvedRefGroup(g.First().Group, g.Count()))
                    .ToList();
            }

            var scanWarnings = new List<string>();
            var hits = TypeLocator.Locate(
                resolved.Assemblies,
                typeQuery,
                opts.NamespaceFilter,
                opts.AssemblyFilter,
                opts.IncludeNonPublic,
                limit,
                scanWarnings);

            if (opts.Verbose)
            {
                foreach (var w in scanWarnings)
                    ctx.Err.WriteLine($"Warning: {w}");
            }

            OutputFormatter.WriteLocateHits(typeQuery, resolved.Assemblies.Count, refGroups, hits, opts.Json, ctx.Out);
            return 0;
        }

        if (HasNamedIndex(opts))
        {
            using var store = OpenNamedIndex(opts);
            var assemblies = store.ListAssemblies();
            var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter)
                .Take(limit)
                .Select(t => new LocateHit(t.FullName, t.ClrName, t.Namespace, t.SimpleName, t.Accessibility, t.AssemblyName, t.AssemblyPath, t.AssemblyName))
                .ToList();
            IReadOnlyList<ResolvedRefGroup>? refGroups = opts.ListRefs
                ? assemblies.Select(a => new ResolvedRefGroup(a.Name, 1)).ToList()
                : null;
            OutputFormatter.WriteLocateHits(typeQuery, assemblies.Count, refGroups, matches, opts.Json, ctx.Out);
            return 0;
        }

        throw new ArgumentException("No source or index specified. Pass --dir <path>, --project <csproj>, --dll <path>, or --index <name>.");
    }

    // ---- Shared helpers -------------------------------------------------------------------------------

    private static int TryResolveSingleType(IndexStore store, CommandContext ctx, out TypeRow? resolved)
    {
        var opts = ctx.Options;
        var typeQuery = opts.Positional[0];
        resolved = null;

        var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter);
        if (matches.Count == 0)
        {
            var msg = $"Type '{typeQuery}' not found. Try 'apilookup search \"{typeQuery}\"' to find matching symbols.";
            OutputFormatter.WriteError(msg, 1, opts.Json, ctx.Out, ctx.Err);
            return 1;
        }

        if (matches.Count > 1)
        {
            var exactFull = matches.Where(m =>
                m.FullName.Equals(typeQuery, StringComparison.OrdinalIgnoreCase) ||
                m.ClrName.Equals(typeQuery, StringComparison.OrdinalIgnoreCase)).ToList();

            if (exactFull.Count == 1)
            {
                resolved = exactFull[0];
                return 0;
            }

            OutputFormatter.WriteAmbiguousTypes(typeQuery, matches, opts.Json, ctx.Out);
            return 2;
        }

        resolved = matches[0];
        return 0;
    }

    private static IndexStore OpenIndexForQuery(CommandContext ctx)
    {
        var opts = ctx.Options;
        if (HasExplicitSources(opts))
        {
            var (store, stats) = OpenOrBuildIndex(ctx);
            if (stats.Rebuilt && !opts.Json)
            {
                ctx.Err.WriteLine(
                    $"[apilookup] Indexed {stats.AssemblyCount} assemblies ({stats.TypeCount:N0} types, {stats.MemberCount:N0} members, {stats.XmlDocCount} XML docs) in {stats.ElapsedMs} ms.");
            }
            return store;
        }

        if (HasNamedIndex(opts))
            return OpenNamedIndex(opts);

        throw new ArgumentException("No source or index specified. Pass --index <name>, --dir <path>, --project <csproj>, or --dll <path>.");
    }

    private static (IndexStore Store, IndexStats Stats) OpenOrBuildIndex(CommandContext ctx)
    {
        var opts = ctx.Options;
        var resolved = ResolveSources(ctx);

        var dbPath = IndexCatalog.ResolveDbPath(opts.IndexName, opts.DbPath, resolved.SourceKey);
        var descParts = new List<string>();
        descParts.AddRange(opts.Dirs.Select(d => "dir:" + Path.GetFullPath(d)));
        descParts.AddRange(opts.Projects.Select(p => "project:" + Path.GetFullPath(p)));
        descParts.AddRange(opts.Dlls.Select(d => "dll:" + Path.GetFullPath(d)));

        var extractWarnings = new List<string>();
        var result = IndexBuilder.EnsureBuilt(
            dbPath,
            opts.IndexName,
            resolved,
            string.Join("; ", descParts),
            opts.IncludeNonPublic,
            opts.ForceRebuild,
            extractWarnings);

        if (opts.Verbose)
        {
            foreach (var w in extractWarnings)
                ctx.Err.WriteLine($"Warning: {w}");
        }
        else if (extractWarnings.Count > 0 && !opts.Json)
        {
            ctx.Err.WriteLine($"[apilookup] {extractWarnings.Count} extraction/XML warning(s) (pass --verbose to inspect).");
        }

        return result;
    }

    /// <summary>Resolves --dir/--project/--dll into assemblies, printing resolver warnings to stderr.</summary>
    private static ResolvedSources ResolveSources(CommandContext ctx)
    {
        var opts = ctx.Options;
        var resolved = SourceResolver.Resolve(
            opts.Projects,
            opts.Dirs,
            opts.Dlls,
            opts.Recursive,
            opts.IncludePatterns,
            opts.ExcludePatterns,
            opts.IncludeNonPublic,
            opts.TargetFramework);

        if (!opts.Json || opts.Verbose)
        {
            foreach (var w in resolved.Warnings)
                ctx.Err.WriteLine($"Warning: {w}");
        }

        if (resolved.Assemblies.Count == 0)
            throw new InvalidOperationException("No managed .NET assemblies found in the specified sources.");

        return resolved;
    }

    private static IndexStore OpenNamedIndex(CliOptions opts)
        => IndexStore.OpenReadOnly(IndexCatalog.ResolveDbPath(opts.IndexName, opts.DbPath, sourceKey: null));

    private static bool HasExplicitSources(CliOptions opts)
        => opts.Dirs.Count > 0 || opts.Projects.Count > 0 || opts.Dlls.Count > 0;

    private static bool HasNamedIndex(CliOptions opts)
        => !string.IsNullOrWhiteSpace(opts.IndexName) || !string.IsNullOrWhiteSpace(opts.DbPath);
}
