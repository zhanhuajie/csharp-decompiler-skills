using System.Text;

namespace ApiLookup;

public sealed class CliOptions
{
    public string Command { get; set; } = string.Empty;
    public List<string> Positional { get; } = new();

    // Sources / Index selection
    public string? IndexName { get; set; }
    public string? DbPath { get; set; }
    public List<string> Projects { get; } = new();
    public List<string> Dirs { get; } = new();
    public List<string> Dlls { get; } = new();
    public List<string> IncludePatterns { get; } = new();
    public List<string> ExcludePatterns { get; } = new();
    public bool Recursive { get; set; } = true;
    public bool IncludeNonPublic { get; set; }
    public bool ForceRebuild { get; set; }

    // Index management
    public bool ListIndexes { get; set; }
    public string? DropIndex { get; set; }

    // Query filters
    public string? KindFilter { get; set; }
    public string? NamespaceFilter { get; set; }
    public string? AssemblyFilter { get; set; }
    public string? MemberFilter { get; set; }
    public string Role { get; set; } = "both";
    public int Limit { get; set; } = 30;
    public bool IncludeInherited { get; set; }
    public bool IncludeExtensions { get; set; }
    public bool ShowFullDocs { get; set; }
    public bool ListRefs { get; set; }
    public bool Json { get; set; }
    public bool ShowHelp { get; set; }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        CliOptions opts;
        try
        {
            opts = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        if (opts.ShowHelp || string.IsNullOrEmpty(opts.Command))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return Run(opts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(CliOptions opts)
    {
        switch (opts.Command)
        {
            case "index":
                return RunIndex(opts);
            case "search":
                return RunSearch(opts);
            case "show":
                return RunShow(opts);
            case "hierarchy":
                return RunHierarchy(opts);
            case "by-type":
                return RunByType(opts);
            case "refs":
                return RunRefs(opts);
            default:
                Console.Error.WriteLine($"Unknown command: {opts.Command}. Run 'apilookup --help' for usage.");
                return 1;
        }
    }

    private static int RunIndex(CliOptions opts)
    {
        if (opts.ListIndexes)
        {
            OutputFormatter.WriteIndexList(IndexStore.ListIndexes(), opts.Json);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(opts.DropIndex))
        {
            var dropped = IndexStore.DropNamedIndex(opts.DropIndex);
            Console.WriteLine(dropped
                ? $"Dropped index '{opts.DropIndex}'."
                : $"Index '{opts.DropIndex}' not found.");
            return dropped ? 0 : 1;
        }

        if (!HasExplicitSources(opts))
            throw new ArgumentException("index requires at least one source (--dir, --project, or --dll).");

        var (store, stats) = OpenOrBuildIndex(opts, forceRebuild: opts.ForceRebuild);
        using (store)
        {
            OutputFormatter.WriteIndexStats(stats, opts.Json);
        }
        return 0;
    }

    private static int RunSearch(CliOptions opts)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("search requires a <query> argument.");

        var query = string.Join(' ', opts.Positional);
        using var store = OpenIndexForQuery(opts);
        if (opts.ListRefs)
            OutputFormatter.WriteAssemblies(store.ListAssemblies(), json: false);

        var hits = store.Search(query, opts.KindFilter, opts.NamespaceFilter, opts.AssemblyFilter, opts.Limit);
        OutputFormatter.WriteSearchHits(query, hits, opts.Json);
        return 0;
    }

    private static int RunShow(CliOptions opts)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("show requires a <TypeName> argument.");

        var typeQuery = opts.Positional[0];
        using var store = OpenIndexForQuery(opts);

        var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Type '{typeQuery}' not found. Try 'apilookup search \"{typeQuery}\"' to find matching symbols.");
            return 1;
        }

        if (matches.Count > 1)
        {
            // If exact full name match narrows to 1, pick it; otherwise show disambiguation list
            var exactFull = matches.Where(m => m.FullName.Equals(typeQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactFull.Count == 1)
                matches = exactFull;
            else
            {
                OutputFormatter.WriteAmbiguousTypes(typeQuery, matches, opts.Json);
                return 0;
            }
        }

        var target = matches[0];
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

        OutputFormatter.WriteTypeDetail(target, ownMembers, inheritedGroups, extMethods, opts.ShowFullDocs, opts.Json);
        return 0;
    }

    private static int RunHierarchy(CliOptions opts)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("hierarchy requires a <TypeName> argument.");

        var typeQuery = opts.Positional[0];
        using var store = OpenIndexForQuery(opts);

        var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Type '{typeQuery}' not found.");
            return 1;
        }
        if (matches.Count > 1)
        {
            var exactFull = matches.Where(m => m.FullName.Equals(typeQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactFull.Count == 1) matches = exactFull;
            else
            {
                OutputFormatter.WriteAmbiguousTypes(typeQuery, matches, opts.Json);
                return 0;
            }
        }

        var limit = opts.Limit == 30 ? 100 : opts.Limit;
        var result = store.GetHierarchy(matches[0], limit);
        OutputFormatter.WriteHierarchy(result, opts.Json);
        return 0;
    }

    private static int RunByType(CliOptions opts)
    {
        if (opts.Positional.Count == 0)
            throw new ArgumentException("by-type requires a <TypeName> argument.");

        var typeName = opts.Positional[0];
        using var store = OpenIndexForQuery(opts);

        var limit = opts.Limit == 30 ? 40 : opts.Limit;
        var members = store.FindByType(typeName, opts.Role, opts.NamespaceFilter, opts.AssemblyFilter, limit);
        OutputFormatter.WriteByType(typeName, opts.Role, members, opts.Json);
        return 0;
    }

    private static int RunRefs(CliOptions opts)
    {
        using var store = OpenIndexForQuery(opts);
        OutputFormatter.WriteAssemblies(store.ListAssemblies(), opts.Json);
        return 0;
    }

    private static IndexStore OpenIndexForQuery(CliOptions opts)
    {
        if (HasExplicitSources(opts))
        {
            var (store, stats) = OpenOrBuildIndex(opts, forceRebuild: opts.ForceRebuild);
            if (stats.Rebuilt && !opts.Json)
            {
                Console.Error.WriteLine(
                    $"[apilookup] Indexed {stats.AssemblyCount} assemblies ({stats.TypeCount:N0} types, {stats.MemberCount:N0} members, {stats.XmlDocCount} XML docs) in {stats.ElapsedMs} ms.");
            }
            return store;
        }

        if (!string.IsNullOrWhiteSpace(opts.IndexName) || !string.IsNullOrWhiteSpace(opts.DbPath))
        {
            var dbPath = IndexStore.ResolveDbPath(opts.IndexName, opts.DbPath, sourceKey: null);
            return IndexStore.OpenReadOnly(dbPath);
        }

        throw new ArgumentException("No source or index specified. Pass --index <name>, --dir <path>, --project <csproj>, or --dll <path>.");
    }

    private static (IndexStore Store, IndexStats Stats) OpenOrBuildIndex(CliOptions opts, bool forceRebuild)
    {
        var resolved = SourceResolver.Resolve(
            opts.Projects,
            opts.Dirs,
            opts.Dlls,
            opts.Recursive,
            opts.IncludePatterns,
            opts.ExcludePatterns);
        foreach (var w in resolved.Warnings)
            Console.Error.WriteLine($"Warning: {w}");

        if (resolved.Assemblies.Count == 0)
            throw new InvalidOperationException("No managed .NET assemblies found in the specified sources.");

        var dbPath = IndexStore.ResolveDbPath(opts.IndexName, opts.DbPath, resolved.SourceKey);
        var descParts = new List<string>();
        descParts.AddRange(opts.Dirs.Select(d => "dir:" + Path.GetFullPath(d)));
        descParts.AddRange(opts.Projects.Select(p => "project:" + Path.GetFullPath(p)));
        descParts.AddRange(opts.Dlls.Select(d => "dll:" + Path.GetFullPath(d)));

        return IndexStore.EnsureBuilt(
            dbPath,
            opts.IndexName,
            resolved,
            string.Join("; ", descParts),
            opts.IncludeNonPublic,
            forceRebuild);
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

        var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "index", "search", "show", "hierarchy", "by-type", "refs" };

        var i = 0;
        if (!args[0].StartsWith('-'))
        {
            if (knownCommands.Contains(args[0]))
            {
                opts.Command = args[0].ToLowerInvariant();
                i = 1;
            }
            else
            {
                // Backward-compatible shorthand: `apilookup <SymbolName> --project ...` -> search
                opts.Command = "search";
            }
        }

        string NextValue(string flag)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Option '{flag}' requires a value.");
            return args[++i];
        }

        while (i < args.Length)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    opts.ShowHelp = true;
                    break;
                case "-n":
                case "--name":
                case "-i":
                case "--index":
                    opts.IndexName = NextValue(arg);
                    break;
                case "--db":
                    opts.DbPath = NextValue(arg);
                    break;
                case "-p":
                case "--project":
                    opts.Projects.Add(NextValue(arg));
                    break;
                case "-d":
                case "--dir":
                    opts.Dirs.Add(NextValue(arg));
                    break;
                case "--dll":
                    opts.Dlls.Add(NextValue(arg));
                    break;
                case "--include":
                    opts.IncludePatterns.Add(NextValue(arg));
                    break;
                case "--exclude":
                    opts.ExcludePatterns.Add(NextValue(arg));
                    break;
                case "--no-recursive":
                    opts.Recursive = false;
                    break;
                case "--include-nonpublic":
                    opts.IncludeNonPublic = true;
                    break;
                case "--force":
                    opts.ForceRebuild = true;
                    break;
                case "--list":
                    opts.ListIndexes = true;
                    break;
                case "--drop":
                    opts.DropIndex = NextValue(arg);
                    break;
                case "-k":
                case "--kind":
                    opts.KindFilter = NextValue(arg);
                    break;
                case "--namespace":
                case "--ns":
                    opts.NamespaceFilter = NextValue(arg);
                    break;
                case "--assembly":
                case "--asm":
                    opts.AssemblyFilter = NextValue(arg);
                    break;
                case "-m":
                case "--member":
                    opts.MemberFilter = NextValue(arg);
                    break;
                case "--role":
                    opts.Role = NextValue(arg);
                    break;
                case "-l":
                case "--limit":
                    if (!int.TryParse(NextValue(arg), out var lim) || lim <= 0)
                        throw new ArgumentException($"Option '{arg}' requires a positive integer.");
                    opts.Limit = lim;
                    break;
                case "--inherited":
                    opts.IncludeInherited = true;
                    break;
                case "--ext":
                case "--extensions":
                    opts.IncludeExtensions = true;
                    break;
                case "--docs":
                    opts.ShowFullDocs = true;
                    break;
                case "--list-refs":
                    opts.ListRefs = true;
                    break;
                case "--json":
                    opts.Json = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option: {arg}");
                    opts.Positional.Add(arg);
                    break;
            }
            i++;
        }

        return opts;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"apilookup - Offline .NET API Metadata & XML-Doc Indexer (System.Reflection.Metadata + SQLite FTS5)

Commands:
  index                        Build/refresh an index (or --list / --drop <name>)
  search <query>               Full-text & CamelCase search across types, members, and XML docs
  show <TypeName>              Print clean C# API skeleton (members, XML docs, inherited, extensions)
  hierarchy <TypeName>         Show base class chain, interfaces, and derived/implementing types
  by-type <TypeName>           Find methods/properties/ctors returning or accepting <TypeName>
  refs                         List indexed assemblies and XML doc availability

Source & Index Options (usable on any command; auto-cached by file fingerprint):
  -i, --index <name>           Use or build a named index (alias: --name)
  --db <path>                  Explicit SQLite database file path
  -d, --dir <path>             Scan directory for managed .NET DLLs (repeatable, recursive)
  -p, --project <csproj>       Resolve NuGet (project.assets.json) + HintPath DLLs (repeatable)
  --dll <path>                 Index specific assembly file(s) (repeatable)
  --include <glob>             Only index DLL filenames matching glob (repeatable, e.g. ""PnP*.dll"")
  --exclude <glob>             Skip DLL filenames matching glob (repeatable, e.g. ""DevExpress.*"")
  --no-recursive               Do not recurse subdirectories for --dir
  --include-nonpublic          Also index internal/private types and members
  --force                      Force rebuild even if cached fingerprint matches

Query Options:
  -k, --kind <kind>            Filter search: type|member|class|struct|interface|enum|method|property|ctor|field|event
  --ns, --namespace <prefix>   Filter by namespace prefix (e.g. Autodesk.ProcessPower)
  --asm, --assembly <part>     Filter by assembly name substring (e.g. PnP3d)
  -m, --member <substring>     Filter members in 'show' (e.g. --member Port)
  --inherited                  Include inherited members from base types in 'show'
  --ext                        Include extension methods targeting this type in 'show'
  --docs                       Include full @param / @returns / Remarks XML docs in 'show'
  --role <return|param|both>   Filter direction in 'by-type' (default: both)
  -l, --limit <n>              Max results (default: 30 for search, 40 for by-type, 100 for hierarchy)
  --list-refs                  Print indexed assemblies before search results
  --json                       Emit structured JSON

Examples:
  apilookup index --name acad2025 --dir ""C:\Program Files\Autodesk\AutoCAD 2025""
  apilookup index --name revit2026 --dir ""C:\Program Files\Autodesk\Revit 2026""
  apilookup search ""offscreen device"" --index acad2025
  apilookup show Equipment --index acad2025 --inherited --ext
  apilookup show Wall --index revit2026 --member Create --docs
  apilookup hierarchy Part --index acad2025
  apilookup by-type DataLinksManager3d --index acad2025
  apilookup search Asset --project C:\repo\MyApp\MyApp.csproj");
    }
}
