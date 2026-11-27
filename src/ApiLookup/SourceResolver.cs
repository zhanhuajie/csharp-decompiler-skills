using System.IO.Enumeration;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ApiLookup;

public sealed record ResolvedAssembly(string Path, string Group);

public sealed record ResolvedSources(
    IReadOnlyList<ResolvedAssembly> Assemblies,
    string SourceKey,
    string Fingerprint,
    IReadOnlyList<string> Warnings);

public static class SourceResolver
{
    public static ResolvedSources Resolve(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> dirPaths,
        IReadOnlyList<string> dllPaths,
        bool recursive = true,
        IReadOnlyList<string>? includePatterns = null,
        IReadOnlyList<string>? excludePatterns = null)
    {
        var results = new List<ResolvedAssembly>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var keyParts = new List<string>();

        var includes = includePatterns ?? Array.Empty<string>();
        var excludes = excludePatterns ?? Array.Empty<string>();
        foreach (var inc in includes) keyParts.Add("inc:" + inc.ToLowerInvariant());
        foreach (var exc in excludes) keyParts.Add("exc:" + exc.ToLowerInvariant());

        bool MatchesFilters(string fullPath, bool applyFilters)
        {
            if (!applyFilters) return true;
            var fileName = Path.GetFileName(fullPath);
            if (includes.Count > 0 && !includes.Any(p => MatchesGlob(fileName, p)))
                return false;
            if (excludes.Count > 0 && excludes.Any(p => MatchesGlob(fileName, p)))
                return false;
            return true;
        }

        void AddDll(string fullPath, string group, bool applyFilters = true)
        {
            if (!File.Exists(fullPath)) return;
            if (!fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return;
            if (fullPath.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                return;
            if (!MatchesFilters(fullPath, applyFilters)) return;
            if (!seen.Add(fullPath)) return;
            if (!HasManagedMetadata(fullPath)) return;
            results.Add(new ResolvedAssembly(fullPath, group));
        }

        foreach (var raw in dllPaths)
        {
            var full = Path.GetFullPath(raw);
            keyParts.Add("dll:" + full.ToLowerInvariant());
            if (!File.Exists(full))
            {
                warnings.Add($"DLL not found: {full}");
                continue;
            }
            if (seen.Add(full))
            {
                if (HasManagedMetadata(full))
                    results.Add(new ResolvedAssembly(full, "Direct DLL"));
                else
                    warnings.Add($"Skipping non-managed binary (no CLR metadata): {full}");
            }
        }

        foreach (var raw in dirPaths)
        {
            var full = Path.GetFullPath(raw);
            keyParts.Add($"dir:{(recursive ? "r" : "t")}:{full.ToLowerInvariant()}");
            if (!Directory.Exists(full))
            {
                warnings.Add($"Directory not found: {full}");
                continue;
            }

            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            IEnumerable<string> files;
            try
            {
                files = EnumerateDllsSafe(full, recursive);
            }
            catch (Exception ex)
            {
                warnings.Add($"Error scanning directory {full}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
                AddDll(file, full);
        }

        foreach (var raw in projectPaths)
        {
            var full = Path.GetFullPath(raw);
            keyParts.Add("proj:" + full.ToLowerInvariant());
            if (!File.Exists(full))
            {
                warnings.Add($"Project file not found: {full}");
                continue;
            }

            var before = results.Count;
            var assetsPath = Path.Combine(Path.GetDirectoryName(full)!, "obj", "project.assets.json");
            if (File.Exists(assetsPath))
            {
                foreach (var (dll, group) in ResolveFromAssets(assetsPath, warnings))
                    AddDll(dll, group);
            }
            else
            {
                warnings.Add($"project.assets.json not found for {full} (run 'dotnet restore' for NuGet packages).");
            }

            foreach (var hintDll in GetCsprojHintPaths(full, warnings))
                AddDll(hintDll, "HintPath");

            if (results.Count == before)
                warnings.Add($"No managed DLL references found for project: {full}");
        }

        results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        keyParts.Sort(StringComparer.OrdinalIgnoreCase);

        var sourceKey = ComputeSha256(string.Join("\n", keyParts))[..16];
        var fpBuilder = new StringBuilder();
        foreach (var asm in results)
        {
            var fi = new FileInfo(asm.Path);
            fpBuilder.Append(asm.Path.ToLowerInvariant())
                .Append('|').Append(fi.Length)
                .Append('|').Append(fi.LastWriteTimeUtc.Ticks);

            var xmlPath = XmlDocReader.FindXmlDocPath(asm.Path);
            if (xmlPath != null)
            {
                var xfi = new FileInfo(xmlPath);
                fpBuilder.Append("|xml:").Append(xfi.Length).Append(':').Append(xfi.LastWriteTimeUtc.Ticks);
            }
            fpBuilder.Append('\n');
        }
        var fingerprint = ComputeSha256(fpBuilder.ToString());

        return new ResolvedSources(results, sourceKey, fingerprint, warnings);
    }

    private static IEnumerable<string> EnumerateDllsSafe(string root, bool recursive)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.dll"); }
            catch { continue; }

            foreach (var f in files)
                yield return f;

            if (!recursive) continue;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                // Skip localized satellite folders like en-US, zh-CN, ja-JP when scanning big install dirs
                if (IsLikelyLocaleDir(name)) continue;
                stack.Push(sub);
            }
        }
    }

    private static bool IsLikelyLocaleDir(string name)
    {
        if (name.Length == 5 && name[2] == '-' &&
            char.IsLetter(name[0]) && char.IsLetter(name[1]) &&
            char.IsLetter(name[3]) && char.IsLetter(name[4]))
            return true;
        return name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasManagedMetadata(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var pe = new PEReader(fs);
            return pe.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    private static string GetNuGetCachePath()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(env)) return env;
        var home = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetEnvironmentVariable("HOME")
                ?? string.Empty;
        return Path.Combine(home, ".nuget", "packages");
    }

    private static IEnumerable<(string dll, string group)> ResolveFromAssets(string assetsFile, List<string> warnings)
    {
        var output = new List<(string dll, string group)>();
        var nugetCache = GetNuGetCachePath();

        try
        {
            using var stream = File.OpenRead(assetsFile);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("libraries", out var libraries) ||
                !root.TryGetProperty("targets", out var targets))
                return output;

            foreach (var tfm in targets.EnumerateObject())
            {
                foreach (var pkg in tfm.Value.EnumerateObject())
                {
                    var pkgId = pkg.Name;
                    var pkgInfo = pkg.Value;

                    if (!pkgInfo.TryGetProperty("type", out var t) || t.GetString() != "package") continue;
                    if (!libraries.TryGetProperty(pkgId, out var lib)) continue;
                    if (!lib.TryGetProperty("path", out var bp)) continue;

                    var pkgPath = bp.GetString();
                    if (string.IsNullOrEmpty(pkgPath)) continue;
                    var pkgRoot = Path.Combine(nugetCache, pkgPath);

                    var dllRelatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (pkgInfo.TryGetProperty("compile", out var cp))
                        foreach (var kv in cp.EnumerateObject())
                            if (kv.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                dllRelatives.Add(kv.Name);

                    if (pkgInfo.TryGetProperty("runtime", out var rp))
                        foreach (var kv in rp.EnumerateObject())
                            if (kv.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                dllRelatives.Add(kv.Name);

                    foreach (var rel in dllRelatives)
                    {
                        var full = Path.GetFullPath(Path.Combine(pkgRoot, rel));
                        if (File.Exists(full))
                            output.Add((full, pkgId));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing {assetsFile}: {ex.Message}");
        }

        return output;
    }

    private static IEnumerable<string> GetCsprojHintPaths(string csprojPath, List<string> warnings)
    {
        var refs = new List<string>();
        try
        {
            XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
            var doc = XDocument.Load(csprojPath);
            var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;

            var hintPaths = doc.Descendants(msbuild + "HintPath")
                .Concat(doc.Descendants("HintPath"))
                .Select(e => e.Value?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var hp in hintPaths)
            {
                if (hp!.Contains("$(")) continue; // Skip unresolved MSBuild variables
                var full = Path.IsPathRooted(hp) ? hp : Path.GetFullPath(Path.Combine(dir, hp));
                if (File.Exists(full)) refs.Add(full);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing {csprojPath}: {ex.Message}");
        }
        return refs;
    }

    public static bool MatchesGlob(string fileName, string pattern)
    {
        var p = pattern.Trim();
        if (!p.Contains('*') && !p.Contains('?'))
            return fileName.Contains(p, StringComparison.OrdinalIgnoreCase);
        return FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true);
    }

    public static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
