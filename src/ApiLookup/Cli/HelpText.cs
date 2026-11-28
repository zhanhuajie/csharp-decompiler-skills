using System.Reflection;
using System.Runtime.InteropServices;

namespace ApiLookup;

/// <summary>Version and build provenance information embedded in the assembly.</summary>
internal static class BuildInfo
{
    public static void WriteVersion(TextWriter w, bool verbose)
    {
        var asm = typeof(BuildInfo).Assembly;
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
    public static string? GetSourceHash()
        => typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SourceHash")?.Value;
}

internal static class HelpText
{
    public static void Write(TextWriter w)
    {
        w.WriteLine(@"apilookup - Offline .NET API Metadata & XML-Doc Indexer (System.Reflection.Metadata + SQLite FTS5)

Commands:
  index                        Build/refresh an index (--list / --drop <name> / --prune)
  search <query>               Full-text & CamelCase/CJK search across types, members, and XML docs
  show <TypeName>              Print clean C# API skeleton (members, XML docs, inherited, extensions)
  hierarchy <TypeName>         Show base class/interface chain, interfaces, and derived/implementing types
  by-type <TypeName>           Find methods/properties/ctors returning or accepting <TypeName>
  locate <TypeName>            Fast zero-cache PE scan (--dir/--project/--dll) to find a type's DLL, namespace & forwarders
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
