using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

// ── Argument parsing ─────────────────────────────────────────────────────────
// Usage: dotnet script scripts/TypeScanner.csx -- <SymbolName>
//            [--project <path>] [--dll <path>] [--list-refs] [--namespace <prefix>]
if (Args.Count == 0 || Args[0].StartsWith("-"))
{
    Console.Error.WriteLine("Usage: dotnet script scripts/TypeScanner.csx -- <SymbolName> [--project <path>] [--dll <path>] [--list-refs] [--namespace <prefix>]");
    Environment.Exit(1);
}

string symbolName = Args[0];
string projectArg = null;
string dllArg = null;
string nsFilter = null;
bool listRefs = false;

for (int i = 1; i < Args.Count; i++)
{
    switch (Args[i])
    {
        case "--project": projectArg = Args[++i]; break;
        case "--dll": dllArg = Args[++i]; break;
        case "--namespace": nsFilter = Args[++i]; break;
        case "--list-refs": listRefs = true; break;
        default:
            Console.Error.WriteLine($"Unknown option: {Args[i]}");
            Environment.Exit(1);
            break;
    }
}

if (projectArg == null && dllArg == null)
{
    Console.Error.WriteLine("Error: No search source provided. Use --project <path> or --dll <path>.");
    Environment.Exit(1);
}

// ── Helper: NuGet global cache path ──────────────────────────────────────────
string GetNuGetCachePath()
{
    var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (!string.IsNullOrEmpty(env)) return env;
    var home = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? string.Empty;
    return Path.Combine(home, ".nuget", "packages");
}

// ── Helper: resolve DLLs from obj/project.assets.json ────────────────────────
(string assetsPath, List<string> dlls, Dictionary<string, List<string>> groups)
    ResolveFromAssets(string csprojPath)
{
    var objDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(csprojPath)), "obj");
    var assetsFile = Path.Combine(objDir, "project.assets.json");
    if (!File.Exists(assetsFile))
        return (string.Empty, new List<string>(), new Dictionary<string, List<string>>());

    var cacheFile = Path.Combine(objDir, ".csharp_lookup_dlls.cache");
    long mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(assetsFile)).ToUnixTimeMilliseconds();

    // Try cache hit
    if (File.Exists(cacheFile))
    {
        try
        {
            var cached = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cacheFile));
            if (cached.TryGetProperty("mtime", out var mp) && mp.GetInt64() == mtime)
            {
                var cdlls = cached.GetProperty("dlls").EnumerateArray().Select(x => x.GetString()).ToList();
                var cgroups = new Dictionary<string, List<string>>();
                foreach (var kv in cached.GetProperty("groups").EnumerateObject())
                    cgroups[kv.Name] = kv.Value.EnumerateArray().Select(x => x.GetString()).ToList();
                return (assetsFile, cdlls, cgroups);
            }
        }
        catch { /* stale or corrupt cache — regenerate */ }
    }

    var nugetCache = GetNuGetCachePath();
    var dllList = new List<string>();
    var pkgGroups = new Dictionary<string, List<string>>();
    var seenDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    try
    {
        var data = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(assetsFile));
        var libraries = data.GetProperty("libraries");
        var targets = data.GetProperty("targets");

        foreach (var tfm in targets.EnumerateObject())
            foreach (var pkg in tfm.Value.EnumerateObject())
            {
                var pkgId = pkg.Name;
                var pkgInfo = pkg.Value;

                if (!pkgInfo.TryGetProperty("type", out var t) || t.GetString() != "package") continue;
                if (!libraries.TryGetProperty(pkgId, out var lib)) continue;
                if (!lib.TryGetProperty("path", out var bp)) continue;

                var pkgRoot = Path.Combine(nugetCache, bp.GetString());
                var dllRelatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (pkgInfo.TryGetProperty("compile", out var cp))
                    foreach (var kv in cp.EnumerateObject())
                        if (kv.Name.EndsWith(".dll")) dllRelatives.Add(kv.Name);

                if (pkgInfo.TryGetProperty("runtime", out var rp))
                    foreach (var kv in rp.EnumerateObject())
                        if (kv.Name.EndsWith(".dll")) dllRelatives.Add(kv.Name);

                if (dllRelatives.Count == 0) continue;

                if (!pkgGroups.ContainsKey(pkgId)) pkgGroups[pkgId] = new List<string>();

                foreach (var rel in dllRelatives)
                {
                    var full = Path.GetFullPath(Path.Combine(pkgRoot, rel));
                    if (!File.Exists(full)) continue;
                    if (!pkgGroups[pkgId].Contains(full)) pkgGroups[pkgId].Add(full);
                    if (seenDlls.Add(full)) dllList.Add(full);
                }
            }

        // Write cache
        try
        {
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(new { mtime, dlls = dllList, groups = pkgGroups }));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Warning: Could not write cache: {ex.Message}"); }

        return (assetsFile, dllList, pkgGroups);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error parsing assets file: {ex.Message}");
        return (assetsFile, new List<string>(), new Dictionary<string, List<string>>());
    }
}

// ── Helper: resolve HintPath references from .csproj ─────────────────────────
List<string> GetCsprojHintPaths(string csprojPath)
{
    var refs = new List<string>();
    if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath)) return refs;
    try
    {
        XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
        var doc = XDocument.Load(csprojPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath));

        // Match both namespace-qualified and bare HintPath elements
        var hintPaths = doc.Descendants(msbuild + "HintPath")
                           .Concat(doc.Descendants("HintPath"))
                           .Select(e => e.Value?.Trim())
                           .Where(v => !string.IsNullOrEmpty(v))
                           .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var hp in hintPaths)
        {
            var full = Path.IsPathRooted(hp) ? hp : Path.GetFullPath(Path.Combine(dir, hp));
            if (!refs.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
                refs.Add(full);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine($"Error parsing csproj: {ex.Message}"); }
    return refs;
}

// ── Helper: scan a single DLL for a type by simple name ──────────────────────
void ScanDll(string dll, string target, string nsf,
             List<(string fullName, string dll)> results,
             HashSet<string> seenKeys)
{
    if (!File.Exists(dll)) return;
    try
    {
        using var fs = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var peReader = new PEReader(fs);
        if (!peReader.HasMetadata) return;

        var mdReader = peReader.GetMetadataReader();
        foreach (var handle in mdReader.TypeDefinitions)
        {
            var td = mdReader.GetTypeDefinition(handle);
            var name = mdReader.GetString(td.Name);
            if (name != target) continue;

            var ns = mdReader.GetString(td.Namespace);
            if (!string.IsNullOrEmpty(nsf) &&
                !ns.StartsWith(nsf, StringComparison.OrdinalIgnoreCase)) continue;

            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            if (seenKeys.Add($"{fullName}|{dll}"))
                results.Add((fullName, dll));
        }
    }
    catch { /* safely ignore unreadable or non-PE files */ }
}

// ── Collect DLLs to search ───────────────────────────────────────────────────
var dllsToCheck = new List<string>();
var assetGroups = new Dictionary<string, List<string>>();

if (!string.IsNullOrEmpty(dllArg))
    dllsToCheck.Add(Path.GetFullPath(dllArg));

if (!string.IsNullOrEmpty(projectArg))
{
    var resolvedProjectPath = Path.GetFullPath(projectArg);
    if (!File.Exists(resolvedProjectPath))
    {
        Console.Error.WriteLine($"Error: Project file not found: '{resolvedProjectPath}'.");
        Console.Error.WriteLine("Tip: Relative paths are resolved from the script's working directory, not the caller's.");
        Console.Error.WriteLine("     Use an absolute path for --project to avoid this issue.");
        Console.Error.WriteLine($"     Example: --project \"{Path.GetFullPath(projectArg)}\"");
        Environment.Exit(1);
    }
    var (_, assetDlls, groups) = ResolveFromAssets(resolvedProjectPath);
    assetGroups = groups;
    dllsToCheck.AddRange(assetDlls);
    dllsToCheck.AddRange(GetCsprojHintPaths(resolvedProjectPath));
}

// Deduplicate while preserving order
var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
dllsToCheck = dllsToCheck.Where(d => seenPaths.Add(d)).ToList();

if (!string.IsNullOrEmpty(projectArg) && dllsToCheck.Count == 0)
{
    var resolved = Path.GetFullPath(projectArg);
    Console.Error.WriteLine($"Warning: No DLL references found in project '{resolved}'.");
    Console.Error.WriteLine("Possible causes:");
    Console.Error.WriteLine("  1. Run 'dotnet restore' to generate obj/project.assets.json.");
    Console.Error.WriteLine($"  2. Expected assets file: {Path.Combine(Path.GetDirectoryName(resolved), "obj", "project.assets.json")}");
    Environment.Exit(1);
}

if (dllsToCheck.Count == 0)
{
    Console.Error.WriteLine("Error: No DLL references found to search.");
    Environment.Exit(1);
}

// ── Optional: list resolved references ───────────────────────────────────────
if (listRefs)
{
    Console.WriteLine("\nResolved DLL references:");
    foreach (var (pkgId, pkgDlls) in assetGroups)
        Console.WriteLine($"  {pkgId} ({pkgDlls.Count} dlls)");

    var localDlls = dllsToCheck
        .Where(d => !assetGroups.Values.Any(g => g.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))))
        .ToList();
    if (localDlls.Count > 0)
        Console.WriteLine($"  Local/Explicit References ({localDlls.Count} dlls)");
    Console.WriteLine();
}

// ── Scan ─────────────────────────────────────────────────────────────────────
Console.WriteLine($"Searching for symbol '{symbolName}' in {dllsToCheck.Count} references using fast metadata scanner...");

var matches = new List<(string fullName, string dll)>();
var seenMatch = new HashSet<string>();
foreach (var dll in dllsToCheck)
    ScanDll(dll, symbolName, nsFilter, matches, seenMatch);

if (matches.Count == 0)
{
    Console.WriteLine($"Symbol '{symbolName}' not found in the provided references.");
}
else
{
    foreach (var (fullName, dll) in matches)
    {
        Console.WriteLine($"\n[FOUND] {fullName}");
        Console.WriteLine($"Assembly: {dll}");
    }
}