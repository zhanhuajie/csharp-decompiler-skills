namespace ApiLookup;

/// <summary>Parses and validates command-line arguments against <see cref="CommandCatalog"/>.</summary>
internal static class CliParser
{
    private static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
        { "type", "member", "class", "struct", "interface", "enum", "delegate", "method", "property", "ctor", "field", "event" };

    private static readonly HashSet<string> ValidRoles = new(StringComparer.OrdinalIgnoreCase)
        { TypeRefRoles.Both, TypeRefRoles.Return, TypeRefRoles.Param };

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

    public static CliOptions Parse(string[] args)
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
            Validate(opts);

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
        if (CommandCatalog.TryGet(word, out _))
            return word.ToLowerInvariant();

        if (LooksLikeCommandWord(word))
        {
            var suggestion = SuggestClosest(word, CommandCatalog.AllNames);
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

    private static void Validate(CliOptions opts)
    {
        if (!CommandCatalog.TryGet(opts.Command, out var spec))
            return;

        foreach (var (option, typed) in opts.SpecifiedOptions)
        {
            if (!spec.AllowedOptions.Contains(option))
                throw new ArgumentException($"Option '{typed}' is not valid for '{opts.Command}'. Run 'apilookup --help' for usage.");
        }

        switch (spec.Arity)
        {
            case PositionalArity.None when opts.Positional.Count > 0:
                throw new ArgumentException($"'{opts.Command}' does not take positional arguments (got: {string.Join(" ", opts.Positional)}).");
            case PositionalArity.ExactlyOne when opts.Positional.Count > 1:
                throw new ArgumentException(
                    $"'{opts.Command}' takes a single {spec.ArgumentName} argument (got: {string.Join(" ", opts.Positional)}). Quote names containing spaces.");
            case PositionalArity.ExactlyOne or PositionalArity.OneOrMore when opts.Positional.Count == 0:
                throw new ArgumentException($"{spec.Name} requires a {spec.ArgumentName} argument.");
        }

        if (spec.Name == "index")
            ValidateIndexManagement(opts);
    }

    private static void ValidateIndexManagement(CliOptions opts)
    {
        var management = CommandCatalog.IndexManagementOptions
            .Where(opts.SpecifiedOptions.ContainsKey)
            .Select(o => opts.SpecifiedOptions[o])
            .ToList();
        if (management.Count > 1)
            throw new ArgumentException($"Options {string.Join(", ", management)} cannot be combined.");
        if (management.Count == 0)
            return;

        var sources = CommandCatalog.SourceOptions
            .Where(opts.SpecifiedOptions.ContainsKey)
            .Select(o => opts.SpecifiedOptions[o])
            .ToList();
        if (sources.Count > 0)
            throw new ArgumentException($"'{management[0]}' cannot be combined with {string.Join(", ", sources)}.");
    }
}
