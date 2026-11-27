using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ApiLookup;

public static class Program
{
    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
        { "index", "search", "show", "hierarchy", "by-type", "refs", "locate", "scan" };

    private static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
        { "type", "member", "class", "struct", "interface", "enum", "delegate", "method", "property", "ctor", "field", "event" };

    private static readonly HashSet<string> ValidRoles = new(StringComparer.OrdinalIgnoreCase)
        { "both", "return", "param" };

    /// <summary>Maps every accepted flag spelling to its canonical option name.</summary>
    private static readonly Dictionary<string, string> OptionAliases = new(StringComparer.Ordinal)
    {
        ["-h"] = "help", ["--help"] = "help",
        ["-v"] = "version", ["--version"] = "version",
        ["-i"] = "index", ["--index"] = "index", ["-n"] = "index", ["--name"] = "index",
        ["--db"] = "db",
        ["-p"] = "project", ["--project"] = "project",
        ["-d"] = "dir", ["--dir"] = "dir",
        ["--dll"] = "dll",
        ["--include"] = "include",
        ["--exclude"] = "exclude",
        ["--no-recursive"] = "no-recursive",
        ["--include-nonpublic"] = "include-nonpublic",
        ["--force"] = "force",
        ["--tfm"] = "tfm",
        ["--list"] = "list",
        ["--drop"] = "drop",
        ["--prune"] = "prune",
        ["-k"] = "kind", ["--kind"] = "kind",
        ["--namespace"] = "namespace", ["--ns"] = "namespace",
        ["--assembly"] = "assembly", ["--asm"] = "assembly",
        ["-m"] = "member", ["--member"] = "member",
        ["--role"] = "role",
        ["-l"] = "limit", ["--limit"] = "limit",
        ["--inherited"] = "inherited",
        ["--ext"] = "ext", ["--extensions"] = "ext",
        ["--docs"] = "docs",
        ["--list-refs"] = "list-refs",
        ["--json"] = "json",
        ["--verbose"] = "verbose",
    };

    private static readonly string[] GlobalOptions = { "help", "version", "json", "verbose" };

    private static readonly string[] SourceOptions =
        { "index", "db", "project", "dir", "dll", "include", "exclude", "no-recursive", "include-nonpublic", "force", "tfm" };

    private static readonly string[] IndexManagementOptions = { "list", "drop", "prune" };

    /// <summary>Options each command accepts; anything else is rejected instead of being silently ignored.</summary>
    private static readonly Dictionary<string, HashSet<string>> CommandOptions = new(StringComparer.Ordinal)
    {
        ["index"] = OptionSet(SourceOptions, IndexManagementOptions),
        ["search"] = OptionSet(SourceOptions, new[] { "namespace", "assembly", "kind", "limit", "list-refs" }),
        ["show"] = OptionSet(SourceOptions, new[] { "namespace", "assembly", "member", "inherited", "ext", "docs" }),
        ["hierarchy"] = OptionSet(SourceOptions, new[] { "namespace", "assembly", "limit" }),
        ["by-type"] = OptionSet(SourceOptions, new[] { "namespace", "assembly", "role", "limit" }),
        ["refs"] = OptionSet(SourceOptions),
        ["locate"] = OptionSet(SourceOptions, new[] { "namespace", "assembly", "limit", "list-refs" }),
    };

    /// <summary>Commands that take exactly one &lt;TypeName&gt; positional argument.</summary>
    private static readonly HashSet<string> SingleTypeArgCommands = new(StringComparer.Ordinal)
        { "show", "hierarchy", "by-type", "locate", "scan" };

    private static HashSet<string> OptionSet(params string[][] groups)
    {
        var set = new HashSet<string>(GlobalOptions, StringComparer.Ordinal);
        foreach (var g in groups) set.UnionWith(g);
        return set;
    }

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        return Run(args, Console.Out, Console.Error);
    }

    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;
        var wantsJson = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

        CliOptions opts;
        try
        {
            opts = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            OutputFormatter.WriteError(ex.Message, 1, wantsJson, outWriter, errWriter);
            return 1;
        }

        if (opts.ShowVersion)
        {
            WriteVersion(outWriter, opts.Verbose);
            return 0;
        }

        if (opts.ShowHelp || string.IsNullOrEmpty(opts.Command))
        {
            PrintHelp(outWriter);
            return 0;
        }

        try
        {
            return Execute(opts, outWriter, errWriter);
        }
        catch (Exception ex)
        {
            OutputFormatter.WriteError(ex.Message, 1, opts.Json, outWriter, errWriter);
            if (opts.Verbose)
                errWriter.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void WriteVersion(TextWriter w, bool verbose)
    {
        var asm = typeof(Program).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "unknown";
        var plusIdx = info.IndexOf('+');
        w.WriteLine($"apilookup {(plusIdx >= 0 ? info[..plusIdx] : info)}");
        if (!verbose) return;

        w.WriteLine($"commit:      {(plusIdx >= 0 ? info[(plusIdx + 1)..] : "unknown")}");
        w.WriteLine($"source-hash: {GetSourceHash() ?? "unknown"}");
        w.WriteLine($"runtime:     {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})");
    }

    /// <summary>
    /// Line-ending-normalized hash of the tool's source files, embedded at build time (see ApiLookup.csproj).
    /// CI compares it between the committed prebuilt binary and a fresh build to detect stale binaries.
    /// </summary>
    internal static string? GetSourceHash()
        => typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SourceHash")?.Value;

    private static int Execute(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        return opts.Command switch
        {
            "index" => RunIndex(opts, outWriter, errWriter),
            "search" => RunSearch(opts, outWriter, errWriter),
            "show" => RunShow(opts, outWriter, errWriter),
            "hierarchy" => RunHierarchy(opts, outWriter, errWriter),
            "by-type" => RunByType(opts, outWriter, errWriter),
            "refs" => RunRefs(opts, outWriter, errWriter),
            "locate" or "scan" => RunLocate(opts, outWriter, errWriter),
            _ => FailWithMessage($"Unknown command: {opts.Command}. Run 'apilookup --help' for usage.", opts.Json, outWriter, errWriter),
        };
    }

    private static int FailWithMessage(string message, bool json, TextWriter outWriter, TextWriter errWriter)
    {
        OutputFormatter.WriteError(message, 1, json, outWriter, errWriter);
        return 1;
    }

    private static int RunIndex(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        if (opts.ListIndexes)
        {
            OutputFormatter.WriteIndexList(IndexCatalog.ListIndexes(), opts.Json, outWriter);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(opts.DropIndex))
        {
            var dropped = IndexCatalog.DropNamedIndex(opts.DropIndex);
            OutputFormatter.WriteDropIndex(new DropIndexResult(opts.DropIndex, dropped), opts.Json, outWriter);
            return dropped ? 0 : 1;
        }

        if (opts.PruneAuto)
        {
            var pruned = IndexCatalog.PruneAutoIndexes();
            OutputFormatter.WritePruneResult(pruned, opts.Json, outWriter);
            return 0;
        }

        if (!HasExplicitSources(opts))
            throw new ArgumentException("index requires at least one source (--dir, --project, or --dll).");

        var (store, stats) = OpenOrBuildIndex(opts, forceRebuild: opts.ForceRebuild, errWriter);
        using (store)
        {
            OutputFormatter.WriteIndexStats(stats, opts.Json, outWriter);
        }
        return 0;
    }

    private static int RunSearch(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("search requires a <query> argument.");

        var query = string.Join(' ', opts.Positional);
        using var store = OpenIndexForQuery(opts, errWriter);
        var hits = store.Search(query, opts.KindFilter, opts.NamespaceFilter, opts.AssemblyFilter, opts.Limit ?? 30);

        if (opts.ListRefs)
            OutputFormatter.WriteSearchHitsWithRefs(query, store.ListAssemblies(), hits, opts.Json, outWriter);
        else
            OutputFormatter.WriteSearchHits(query, hits, opts.Json, outWriter);
        return 0;
    }

    private static int RunShow(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("show requires a <TypeName> argument.");

        var typeQuery = opts.Positional[0];
        using var store = OpenIndexForQuery(opts, errWriter);

        var exitCode = TryResolveSingleType(store, typeQuery, opts, outWriter, errWriter, out var target);
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
                    .Where(m => m.Kind != "ctor")
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
            opts.ShowFullDocs, opts.Json, memberFiltered, outWriter);
        return 0;
    }

    private static int RunHierarchy(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("hierarchy requires a <TypeName> argument.");

        var typeQuery = opts.Positional[0];
        using var store = OpenIndexForQuery(opts, errWriter);

        var exitCode = TryResolveSingleType(store, typeQuery, opts, outWriter, errWriter, out var target);
        if (exitCode != 0 || target == null)
            return exitCode;

        var limit = opts.Limit ?? 100;
        var result = store.GetHierarchy(target, limit);
        OutputFormatter.WriteHierarchy(result, opts.Json, outWriter);
        return 0;
    }

    private static int TryResolveSingleType(
        IndexStore store,
        string typeQuery,
        CliOptions opts,
        TextWriter outWriter,
        TextWriter errWriter,
        out TypeRow? resolved)
    {
        resolved = null;
        var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter);
        if (matches.Count == 0)
        {
            var msg = $"Type '{typeQuery}' not found. Try 'apilookup search \"{typeQuery}\"' to find matching symbols.";
            OutputFormatter.WriteError(msg, 1, opts.Json, outWriter, errWriter);
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

            OutputFormatter.WriteAmbiguousTypes(typeQuery, matches, opts.Json, outWriter);
            return 2;
        }

        resolved = matches[0];
        return 0;
    }

    private static int RunByType(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("by-type requires a <TypeName> argument.");

        var typeName = opts.Positional[0];
        using var store = OpenIndexForQuery(opts, errWriter);

        var limit = opts.Limit ?? 40;
        var members = store.FindByType(typeName, opts.Role, opts.NamespaceFilter, opts.AssemblyFilter, limit);
        OutputFormatter.WriteByType(typeName, opts.Role, members, opts.Json, outWriter);
        return 0;
    }

    private static int RunRefs(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        using var store = OpenIndexForQuery(opts, errWriter);
        OutputFormatter.WriteAssemblies(store.ListAssemblies(), opts.Json, outWriter);
        return 0;
    }

    private static int RunLocate(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("locate requires a <TypeName> argument.");

        var typeQuery = opts.Positional[0];
        var limit = opts.Limit ?? 100;

        if (HasExplicitSources(opts))
        {
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
                    errWriter.WriteLine($"Warning: {w}");
            }

            if (resolved.Assemblies.Count == 0)
                throw new InvalidOperationException("No managed .NET assemblies found in the specified sources.");

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
                    errWriter.WriteLine($"Warning: {w}");
            }

            OutputFormatter.WriteLocateHits(typeQuery, resolved.Assemblies.Count, refGroups, hits, opts.Json, outWriter);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(opts.IndexName) || !string.IsNullOrWhiteSpace(opts.DbPath))
        {
            var dbPath = IndexCatalog.ResolveDbPath(opts.IndexName, opts.DbPath, sourceKey: null);
            using var store = IndexStore.OpenReadOnly(dbPath);
            var assemblies = store.ListAssemblies();
            var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter)
                .Take(limit)
                .Select(t => new LocateHit(t.FullName, t.ClrName, t.Namespace, t.SimpleName, t.Accessibility, t.AssemblyName, t.AssemblyPath, t.AssemblyName))
                .ToList();
            IReadOnlyList<ResolvedRefGroup>? refGroups = opts.ListRefs
                ? assemblies.Select(a => new ResolvedRefGroup(a.Name, 1)).ToList()
                : null;
            OutputFormatter.WriteLocateHits(typeQuery, assemblies.Count, refGroups, matches, opts.Json, outWriter);
            return 0;
        }

        throw new ArgumentException("No source or index specified. Pass --dir <path>, --project <csproj>, --dll <path>, or --index <name>.");
    }

    private static IndexStore OpenIndexForQuery(CliOptions opts, TextWriter errWriter)
    {
        if (HasExplicitSources(opts))
        {
            var (store, stats) = OpenOrBuildIndex(opts, forceRebuild: opts.ForceRebuild, errWriter);
            if (stats.Rebuilt && !opts.Json)
            {
                errWriter.WriteLine(
                    $"[apilookup] Indexed {stats.AssemblyCount} assemblies ({stats.TypeCount:N0} types, {stats.MemberCount:N0} members, {stats.XmlDocCount} XML docs) in {stats.ElapsedMs} ms.");
            }
            return store;
        }

        if (!string.IsNullOrWhiteSpace(opts.IndexName) || !string.IsNullOrWhiteSpace(opts.DbPath))
        {
            var dbPath = IndexCatalog.ResolveDbPath(opts.IndexName, opts.DbPath, sourceKey: null);
            return IndexStore.OpenReadOnly(dbPath);
        }

        throw new ArgumentException("No source or index specified. Pass --index <name>, --dir <path>, --project <csproj>, or --dll <path>.");
    }

    private static (IndexStore Store, IndexStats Stats) OpenOrBuildIndex(CliOptions opts, bool forceRebuild, TextWriter errWriter)
    {
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
                errWriter.WriteLine($"Warning: {w}");
        }

        if (resolved.Assemblies.Count == 0)
            throw new InvalidOperationException("No managed .NET assemblies found in the specified sources.");

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
            forceRebuild,
            extractWarnings);

        if (opts.Verbose)
        {
            foreach (var w in extractWarnings)
                errWriter.WriteLine($"Warning: {w}");
        }
        else if (extractWarnings.Count > 0 && !opts.Json)
        {
            errWriter.WriteLine($"[apilookup] {extractWarnings.Count} extraction/XML warning(s) (pass --verbose to inspect).");
        }

        return result;
    }

    private static bool HasExplicitSources(CliOptions opts)
        => opts.Dirs.Count > 0 || opts.Projects.Count > 0 || opts.Dlls.Count > 0;

    public static CliOptions ParseArgs(string[] args)
    {
        var opts = new CliOptions();
        if (args.Length == 0)
        {
            opts.ShowHelp = true;
            return opts;
        }

        var i = 0;

        string NextValue(string flag)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Option '{flag}' requires a value.");
            return args[++i];
        }

        while (i < args.Length)
        {
            var arg = args[i];
            if (OptionAliases.TryGetValue(arg, out var option))
            {
                opts.SpecifiedOptions.TryAdd(option, arg);
                ApplyOption(opts, option, arg, NextValue);
            }
            else if (arg.Length > 1 && arg.StartsWith('-'))
            {
                var hint = SuggestClosest(arg, OptionAliases.Keys.Where(k => k.StartsWith("--", StringComparison.Ordinal)));
                throw new ArgumentException(hint != null
                    ? $"Unknown option: {arg}. Did you mean '{hint}'?"
                    : $"Unknown option: {arg}");
            }
            else if (string.IsNullOrEmpty(opts.Command))
            {
                opts.Command = ResolveCommandWord(arg, opts);
            }
            else
            {
                opts.Positional.Add(arg);
            }
            i++;
        }

        if (string.IsNullOrEmpty(opts.Command) && (opts.ListIndexes || opts.DropIndex != null || opts.PruneAuto))
            opts.Command = "index";

        if (!opts.ShowHelp && !opts.ShowVersion && !string.IsNullOrEmpty(opts.Command))
            ValidateCommandUsage(opts);

        return opts;
    }

    private static void ApplyOption(CliOptions opts, string option, string flag, Func<string, string> nextValue)
    {
        switch (option)
        {
            case "help": opts.ShowHelp = true; break;
            case "version": opts.ShowVersion = true; break;
            case "index": opts.IndexName = nextValue(flag); break;
            case "db": opts.DbPath = nextValue(flag); break;
            case "project": opts.Projects.Add(nextValue(flag)); break;
            case "dir": opts.Dirs.Add(nextValue(flag)); break;
            case "dll": opts.Dlls.Add(nextValue(flag)); break;
            case "include": opts.IncludePatterns.Add(nextValue(flag)); break;
            case "exclude": opts.ExcludePatterns.Add(nextValue(flag)); break;
            case "no-recursive": opts.Recursive = false; break;
            case "include-nonpublic": opts.IncludeNonPublic = true; break;
            case "force": opts.ForceRebuild = true; break;
            case "tfm": opts.TargetFramework = nextValue(flag); break;
            case "list": opts.ListIndexes = true; break;
            case "drop": opts.DropIndex = nextValue(flag); break;
            case "prune": opts.PruneAuto = true; break;
            case "kind":
            {
                var kind = nextValue(flag).Trim();
                if (!ValidKinds.Contains(kind))
                    throw new ArgumentException($"Invalid --kind '{kind}'. Allowed values: {string.Join(", ", ValidKinds)}.");
                opts.KindFilter = kind;
                break;
            }
            case "namespace": opts.NamespaceFilter = nextValue(flag); break;
            case "assembly": opts.AssemblyFilter = nextValue(flag); break;
            case "member": opts.MemberFilter = nextValue(flag); break;
            case "role":
            {
                var role = nextValue(flag).Trim();
                if (!ValidRoles.Contains(role))
                    throw new ArgumentException($"Invalid --role '{role}'. Allowed values: both, return, param.");
                opts.Role = role.ToLowerInvariant();
                break;
            }
            case "limit":
                if (!int.TryParse(nextValue(flag), out var lim) || lim <= 0)
                    throw new ArgumentException($"Option '{flag}' requires a positive integer.");
                opts.Limit = lim;
                break;
            case "inherited": opts.IncludeInherited = true; break;
            case "ext": opts.IncludeExtensions = true; break;
            case "docs": opts.ShowFullDocs = true; break;
            case "list-refs": opts.ListRefs = true; break;
            case "json": opts.Json = true; break;
            case "verbose": opts.Verbose = true; break;
            default: throw new InvalidOperationException($"Unhandled option '{option}'.");
        }
    }

    /// <summary>
    /// Maps the first positional word to a command. Unknown words fall back to the backward-compatible
    /// `apilookup &lt;SymbolName&gt; ...` search shorthand, except when the word looks like a mistyped command
    /// (e.g. "serach"), which would otherwise silently return "No results" with exit code 0.
    /// </summary>
    private static string ResolveCommandWord(string word, CliOptions opts)
    {
        if (KnownCommands.Contains(word))
            return word.ToLowerInvariant();

        if (LooksLikeCommandWord(word))
        {
            var suggestion = SuggestClosest(word, KnownCommands);
            if (suggestion != null)
            {
                throw new ArgumentException(
                    $"Unknown command '{word}'. Did you mean '{suggestion}'? " +
                    $"(To search for the symbol '{word}', run: apilookup search {word} ...)");
            }
        }

        opts.Positional.Add(word);
        return "search";
    }

    private static bool LooksLikeCommandWord(string word)
        => word.Length >= 3 && word.All(c => c is (>= 'a' and <= 'z') or '-');

    /// <summary>Returns the closest candidate within a small edit distance (transpositions count as one edit).</summary>
    internal static string? SuggestClosest(string input, IEnumerable<string> candidates)
    {
        var maxDistance = input.Length <= 4 ? 1 : 2;
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var d = EditDistance(input.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }
        return bestDistance > 0 && bestDistance <= maxDistance ? best : null;
    }

    /// <summary>Optimal string alignment distance (Levenshtein + adjacent transpositions).</summary>
    internal static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }

    private static void ValidateCommandUsage(CliOptions opts)
    {
        var command = opts.Command == "scan" ? "locate" : opts.Command;
        if (!CommandOptions.TryGetValue(command, out var allowed))
            return;

        foreach (var (option, typed) in opts.SpecifiedOptions)
        {
            if (!allowed.Contains(option))
                throw new ArgumentException($"Option '{typed}' is not valid for '{opts.Command}'. Run 'apilookup --help' for usage.");
        }

        if (command is "index" or "refs" && opts.Positional.Count > 0)
            throw new ArgumentException($"'{opts.Command}' does not take positional arguments (got: {string.Join(" ", opts.Positional)}).");

        if (SingleTypeArgCommands.Contains(opts.Command) && opts.Positional.Count > 1)
            throw new ArgumentException(
                $"'{opts.Command}' takes a single <TypeName> argument (got: {string.Join(" ", opts.Positional)}). Quote names containing spaces.");

        if (command == "index")
        {
            var management = IndexManagementOptions
                .Where(opts.SpecifiedOptions.ContainsKey)
                .Select(o => opts.SpecifiedOptions[o])
                .ToList();
            if (management.Count > 1)
                throw new ArgumentException($"Options {string.Join(", ", management)} cannot be combined.");
            if (management.Count == 1)
            {
                var sources = SourceOptions
                    .Where(opts.SpecifiedOptions.ContainsKey)
                    .Select(o => opts.SpecifiedOptions[o])
                    .ToList();
                if (sources.Count > 0)
                    throw new ArgumentException($"'{management[0]}' cannot be combined with {string.Join(", ", sources)}.");
            }
        }
    }

    private static void PrintHelp(TextWriter w)
    {
        w.WriteLine(@"apilookup - Offline .NET API Metadata & XML-Doc Indexer (System.Reflection.Metadata + SQLite FTS5)

Commands:
  index                        Build/refresh an index (--list / --drop <name> / --prune)
  search <query>               Full-text & CamelCase/CJK search across types, members, and XML docs
  show <TypeName>              Print clean C# API skeleton (members, XML docs, inherited, extensions)
  hierarchy <TypeName>         Show base class/interface chain, interfaces, and derived/implementing types
  by-type <TypeName>           Find methods/properties/ctors returning or accepting <TypeName>
  locate <TypeName>            Fast zero-cache PE scan to locate a type's DLL & namespace (alias: scan)
  refs                         List indexed assemblies and XML doc availability

Source & Index Options (usable on any command; auto-cached by file fingerprint):
  -i, --index <name>           Use or build a named index (alias: --name)
  --db <path>                  Explicit SQLite database file path
  -d, --dir <path>             Scan directory for managed .NET DLLs/EXEs (repeatable, recursive)
  -p, --project <csproj>       Resolve NuGet (project.assets.json) + HintPath DLLs (repeatable)
  --dll <path>                 Index specific assembly file(s) (repeatable)
  --tfm <tfm>                  Target framework to pick from multi-TFM project.assets.json (e.g. net8.0)
  --include <glob>             Only index filenames matching glob (repeatable, e.g. ""PnP*.dll"")
  --exclude <glob>             Skip filenames matching glob (repeatable, e.g. ""DevExpress.*"")
  --no-recursive               Do not recurse subdirectories for --dir
  --include-nonpublic          Also index internal/private types and members
  --force                      Force rebuild even if cached fingerprint matches
  --prune                      Delete auto-cached (auto-*.db) indexes and stale temp build files when running 'index'

Query Options:
  -k, --kind <kind>            Filter search: type|member|class|struct|interface|enum|delegate|method|property|ctor|field|event
  --ns, --namespace <prefix>   Filter by namespace prefix (e.g. Autodesk.ProcessPower)
  --asm, --assembly <part>     Filter by assembly name substring (e.g. PnP3d)
  -m, --member <substring>     Filter members in 'show' (e.g. --member Port)
  --inherited                  Include inherited members from base classes/interfaces in 'show'
  --ext                        Include extension methods targeting this type in 'show'
  --docs                       Include full @param / @returns / Remarks XML docs in 'show'
  --role <return|param|both>   Filter direction in 'by-type' (default: both)
  -l, --limit <n>              Max results in search/by-type/hierarchy/locate (defaults: 30 / 40 / 100 / 100)
  --list-refs                  Print indexed/resolved assemblies before search or locate results
  --json                       Emit structured camelCase JSON (including error objects)
  --verbose                    Print detailed warnings and error stack traces to stderr
  -v, --version                Print version and exit (add --verbose for commit, source hash, runtime)

Options are validated per command: passing an option a command does not use is an error.

Environment:
  APILOOKUP_CACHE_DIR          Override default SQLite index cache directory

Exit Codes:
  0  Success (or help/version printed)
  1  Error (invalid arguments, unknown command/option, missing source/index, or type not found)
  2  Ambiguous type match in 'show' or 'hierarchy' (candidates written to stdout)

Examples:
  apilookup index --name acad2025 --dir ""C:\Program Files\Autodesk\AutoCAD 2025""
  apilookup index --name revit2026 --dir ""C:\Program Files\Autodesk\Revit 2026""
  apilookup index --prune
  apilookup search ""offscreen device"" --index acad2025
  apilookup show Equipment --index acad2025 --inherited --ext
  apilookup show Wall --index revit2026 --member Create --docs
  apilookup hierarchy Part --index acad2025
  apilookup by-type DataLinksManager3d --index acad2025
  apilookup search Asset --project C:\repo\MyApp\MyApp.csproj");
    }
}
