using System.Reflection;
using System.Text;

namespace ApiLookup;

public static class Program
{
    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
        { "index", "search", "show", "hierarchy", "by-type", "refs" };

    private static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
        { "type", "member", "class", "struct", "interface", "enum", "delegate", "method", "property", "ctor", "field", "event" };

    private static readonly HashSet<string> ValidRoles = new(StringComparer.OrdinalIgnoreCase)
        { "both", "return", "param" };

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        return Run(args, Console.Out, Console.Error);
    }

    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;

        CliOptions opts;
        try
        {
            opts = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            errWriter.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        if (opts.ShowVersion)
        {
            var ver = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "0.2.0";
            var plusIdx = ver.IndexOf('+');
            if (plusIdx >= 0) ver = ver[..plusIdx];
            outWriter.WriteLine($"apilookup {ver}");
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
            errWriter.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Execute(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        switch (opts.Command)
        {
            case "index":
                return RunIndex(opts, outWriter, errWriter);
            case "search":
                return RunSearch(opts, outWriter, errWriter);
            case "show":
                return RunShow(opts, outWriter, errWriter);
            case "hierarchy":
                return RunHierarchy(opts, outWriter, errWriter);
            case "by-type":
                return RunByType(opts, outWriter, errWriter);
            case "refs":
                return RunRefs(opts, outWriter, errWriter);
            default:
                errWriter.WriteLine($"Unknown command: {opts.Command}. Run 'apilookup --help' for usage.");
                return 1;
        }
    }

    private static int RunIndex(CliOptions opts, TextWriter outWriter, TextWriter errWriter)
    {
        if (opts.ListIndexes)
        {
            OutputFormatter.WriteIndexList(IndexStore.ListIndexes(), opts.Json, outWriter);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(opts.DropIndex))
        {
            var dropped = IndexStore.DropNamedIndex(opts.DropIndex);
            OutputFormatter.WriteDropIndex(new DropIndexResult(opts.DropIndex, dropped), opts.Json, outWriter);
            return dropped ? 0 : 1;
        }

        if (opts.PruneAuto)
        {
            var pruned = IndexStore.PruneAutoIndexes();
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

        var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter);
        if (matches.Count == 0)
        {
            errWriter.WriteLine($"Type '{typeQuery}' not found. Try 'apilookup search \"{typeQuery}\"' to find matching symbols.");
            return 1;
        }

        if (matches.Count > 1)
        {
            var exactFull = matches.Where(m => m.FullName.Equals(typeQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactFull.Count == 1)
                matches = exactFull;
            else
            {
                OutputFormatter.WriteAmbiguousTypes(typeQuery, matches, opts.Json, outWriter);
                return 2;
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

        var matches = store.ResolveType(typeQuery, opts.NamespaceFilter, opts.AssemblyFilter);
        if (matches.Count == 0)
        {
            errWriter.WriteLine($"Type '{typeQuery}' not found.");
            return 1;
        }
        if (matches.Count > 1)
        {
            var exactFull = matches.Where(m => m.FullName.Equals(typeQuery, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactFull.Count == 1) matches = exactFull;
            else
            {
                OutputFormatter.WriteAmbiguousTypes(typeQuery, matches, opts.Json, outWriter);
                return 2;
            }
        }

        var limit = opts.Limit ?? 100;
        var result = store.GetHierarchy(matches[0], limit);
        OutputFormatter.WriteHierarchy(result, opts.Json, outWriter);
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
            var dbPath = IndexStore.ResolveDbPath(opts.IndexName, opts.DbPath, sourceKey: null);
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

        var dbPath = IndexStore.ResolveDbPath(opts.IndexName, opts.DbPath, resolved.SourceKey);
        var descParts = new List<string>();
        descParts.AddRange(opts.Dirs.Select(d => "dir:" + Path.GetFullPath(d)));
        descParts.AddRange(opts.Projects.Select(p => "project:" + Path.GetFullPath(p)));
        descParts.AddRange(opts.Dlls.Select(d => "dll:" + Path.GetFullPath(d)));

        var extractWarnings = new List<string>();
        var result = IndexStore.EnsureBuilt(
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
            switch (arg)
            {
                case "-h":
                case "--help":
                    opts.ShowHelp = true;
                    break;
                case "-v":
                case "--version":
                    opts.ShowVersion = true;
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
                case "--tfm":
                    opts.TargetFramework = NextValue(arg);
                    break;
                case "--list":
                    opts.ListIndexes = true;
                    break;
                case "--drop":
                    opts.DropIndex = NextValue(arg);
                    break;
                case "--prune":
                    opts.PruneAuto = true;
                    break;
                case "-k":
                case "--kind":
                {
                    var kind = NextValue(arg).Trim();
                    if (!ValidKinds.Contains(kind))
                        throw new ArgumentException($"Invalid --kind '{kind}'. Allowed values: {string.Join(", ", ValidKinds)}.");
                    opts.KindFilter = kind;
                    break;
                }
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
                {
                    var role = NextValue(arg).Trim();
                    if (!ValidRoles.Contains(role))
                        throw new ArgumentException($"Invalid --role '{role}'. Allowed values: both, return, param.");
                    opts.Role = role.ToLowerInvariant();
                    break;
                }
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
                case "--verbose":
                    opts.Verbose = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Unknown option: {arg}");
                    if (string.IsNullOrEmpty(opts.Command))
                    {
                        if (KnownCommands.Contains(arg))
                        {
                            opts.Command = arg.ToLowerInvariant();
                        }
                        else
                        {
                            // Backward-compatible shorthand: `apilookup <SymbolName> --project ...` -> search
                            opts.Command = "search";
                            opts.Positional.Add(arg);
                        }
                    }
                    else
                    {
                        opts.Positional.Add(arg);
                    }
                    break;
            }
            i++;
        }

        if (string.IsNullOrEmpty(opts.Command) && (opts.ListIndexes || opts.DropIndex != null || opts.PruneAuto))
            opts.Command = "index";

        return opts;
    }

    private static void PrintHelp(TextWriter w)
    {
        w.WriteLine(@"apilookup - Offline .NET API Metadata & XML-Doc Indexer (System.Reflection.Metadata + SQLite FTS5)

Commands:
  index                        Build/refresh an index (--list / --drop <name> / --prune)
  search <query>               Full-text & CamelCase/CJK search across types, members, and XML docs
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
  --tfm <tfm>                  Target framework to pick from multi-TFM project.assets.json (e.g. net8.0)
  --include <glob>             Only index DLL filenames matching glob (repeatable, e.g. ""PnP*.dll"")
  --exclude <glob>             Skip DLL filenames matching glob (repeatable, e.g. ""DevExpress.*"")
  --no-recursive               Do not recurse subdirectories for --dir
  --include-nonpublic          Also index internal/private types and members
  --force                      Force rebuild even if cached fingerprint matches
  --prune                      Delete auto-cached (auto-*.db) indexes when running 'index'

Query Options:
  -k, --kind <kind>            Filter search: type|member|class|struct|interface|enum|delegate|method|property|ctor|field|event
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
  --verbose                    Print detailed metadata/XML warnings to stderr
  -v, --version                Print version and exit

Exit Codes:
  0  Success (or help/version printed)
  1  Error (invalid arguments, missing source/index, or type not found)
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
