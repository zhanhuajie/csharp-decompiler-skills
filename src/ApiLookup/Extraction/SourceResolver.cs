using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ApiLookup;

public sealed record ResolvedAssembly(
    string Path,
    string Group,
    string? XmlPath = null,
    long FileSize = 0,
    long MtimeUtc = 0);

/// <param name="StatFingerprint">Fingerprint of the discovered candidate files (see <see cref="DiscoveredSources"/>).</param>
public sealed record ResolvedSources(
    IReadOnlyList<ResolvedAssembly> Assemblies,
    string SourceKey,
    string Fingerprint,
    IReadOnlyList<string> Warnings,
    string? StatFingerprint = null);

/// <summary>A binary that may be a managed assembly; only file-system metadata has been read so far.</summary>
public sealed record SourceCandidate(
    string FullPath,
    string Group,
    string? XmlPath,
    long FileSize,
    long MtimeUtc,
    bool IsDirect,
    long XmlFileSize = -1,
    long XmlMtimeUtc = 0);

/// <summary>
/// Result of the cheap discovery phase: candidate binaries with size/mtime (no file contents opened).
/// <see cref="StatFingerprint"/> hashes every candidate's path, size, mtime and XML companion, so an up-to-date index
/// can be reused without opening thousands of PE headers (<see cref="SourceResolver.Probe"/>).
/// </summary>
public sealed record DiscoveredSources(
    IReadOnlyList<SourceCandidate> Candidates,
    string SourceKey,
    string StatFingerprint,
    IReadOnlyList<string> Warnings);

public static partial class SourceResolver
{
    [GeneratedRegex(@"\$\(([^)]+)\)")]
    private static partial Regex MsBuildMacroRegex();

    private const int ParallelThreshold = 8;

    /// <summary>Discovers and probes sources in one step (equivalent to <c>Probe(Discover(...))</c>).</summary>
    public static ResolvedSources Resolve(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> dirPaths,
        IReadOnlyList<string> dllPaths,
        bool recursive = true,
        IReadOnlyList<string>? includePatterns = null,
        IReadOnlyList<string>? excludePatterns = null,
        bool includeNonPublic = false,
        string? targetFramework = null)
        => Probe(Discover(projectPaths, dirPaths, dllPaths, recursive, includePatterns, excludePatterns, includeNonPublic, targetFramework));

    /// <summary>
    /// Enumerates candidate binaries from <c>--dll</c>, <c>--dir</c>, and <c>--project</c> sources using only
    /// directory listings and file metadata. No assembly is opened.
    /// </summary>
    public static DiscoveredSources Discover(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> dirPaths,
        IReadOnlyList<string> dllPaths,
        bool recursive = true,
        IReadOnlyList<string>? includePatterns = null,
        IReadOnlyList<string>? excludePatterns = null,
        bool includeNonPublic = false,
        string? targetFramework = null)
    {
        var candidates = new List<SourceCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var keyParts = new List<string>
        {
            includeNonPublic ? "vis:all" : "vis:pub",
        };
        if (!string.IsNullOrWhiteSpace(targetFramework))
            keyParts.Add("tfm:" + targetFramework.Trim().ToLowerInvariant());

        var includes = includePatterns ?? Array.Empty<string>();
        var excludes = excludePatterns ?? Array.Empty<string>();
        foreach (var inc in includes) keyParts.Add("inc:" + inc.ToLowerInvariant());
        foreach (var exc in excludes) keyParts.Add("exc:" + exc.ToLowerInvariant());

        bool MatchesFilters(string fullPath)
        {
            var fileName = Path.GetFileName(fullPath);
            if (includes.Count > 0 && !includes.Any(p => MatchesGlob(fileName, p)))
                return false;
            if (excludes.Count > 0 && excludes.Any(p => MatchesGlob(fileName, p)))
                return false;
            return true;
        }

        bool EnqueueCandidate(string fullPath, string group, string? prefoundXml = null, FileInfo? info = null)
        {
            if (!fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;
            if (fullPath.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) ||
                fullPath.EndsWith(".vshost.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!MatchesFilters(fullPath)) return false;

            info ??= new FileInfo(fullPath);
            if (!info.Exists) return false;
            if (!seen.Add(fullPath)) return false;

            candidates.Add(new SourceCandidate(fullPath, group, prefoundXml, info.Length, info.LastWriteTimeUtc.Ticks, IsDirect: false));
            return true;
        }

        var xmlCache = new DirectoryXmlCache();

        var directCount = 0;
        foreach (var raw in dllPaths)
        {
            var full = Path.GetFullPath(raw);
            keyParts.Add("dll:" + full.ToLowerInvariant());
            var fi = new FileInfo(full);
            if (!fi.Exists)
            {
                warnings.Add($"DLL not found: {full}");
                continue;
            }
            // Explicit --dll paths bypass extension and include/exclude filters.
            if (seen.Add(full))
            {
                candidates.Add(new SourceCandidate(full, "Direct DLL", null, fi.Length, fi.LastWriteTimeUtc.Ticks, IsDirect: true));
                directCount++;
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

            try
            {
                foreach (var file in EnumerateBinariesSafe(full, recursive, xmlCache))
                    EnqueueCandidate(file.FullName, full, info: file);
            }
            catch (Exception ex)
            {
                warnings.Add($"Error scanning directory {full}: {ex.Message}");
            }
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

            var projectCandidateCount = 0;
            var projectDir = Path.GetDirectoryName(full)!;
            var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
            if (File.Exists(assetsPath))
            {
                foreach (var (dll, group, xmlPath) in ResolveFromAssets(assetsPath, projectDir, targetFramework, warnings))
                {
                    if (EnqueueCandidate(dll, group, prefoundXml: xmlPath)) projectCandidateCount++;
                }
            }
            else
            {
                warnings.Add($"project.assets.json not found for {full} (run 'dotnet restore' for NuGet packages).");
            }

            foreach (var hintDll in GetCsprojHintPaths(full, warnings))
            {
                if (EnqueueCandidate(hintDll, "HintPath")) projectCandidateCount++;
            }

            if (projectCandidateCount == 0 && directCount == 0)
                warnings.Add($"No managed DLL references found for project: {full}");
        }

        // XML companion lookup reuses cached directory *.xml listings from the scan above (or scans lazily per dir).
        var withXml = new SourceCandidate[candidates.Count];
        void FillXml(int i)
        {
            var c = candidates[i];
            if (c.XmlPath != null)
            {
                var xfi = new FileInfo(c.XmlPath);
                withXml[i] = xfi.Exists ? c with { XmlFileSize = xfi.Length, XmlMtimeUtc = xfi.LastWriteTimeUtc.Ticks } : c;
                return;
            }

            var xmlFileName = Path.GetFileName(Path.ChangeExtension(c.FullPath, ".xml"));
            withXml[i] = xmlCache.TryFind(Path.GetDirectoryName(c.FullPath), xmlFileName, out var xmlMeta)
                ? c with { XmlPath = xmlMeta.FullPath, XmlFileSize = xmlMeta.Size, XmlMtimeUtc = xmlMeta.MtimeUtc }
                : c;
        }
        if (candidates.Count <= ParallelThreshold)
            for (var i = 0; i < candidates.Count; i++) FillXml(i);
        else
            Parallel.For(0, candidates.Count, FillXml);

        Array.Sort(withXml, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
        keyParts.Sort(StringComparer.OrdinalIgnoreCase);

        var statBuilder = new StringBuilder();
        foreach (var c in withXml)
        {
            statBuilder.Append(c.FullPath.ToLowerInvariant())
                .Append('|').Append(c.FileSize)
                .Append('|').Append(c.MtimeUtc)
                .Append('|').Append(c.Group);
            AppendXmlStat(statBuilder, c.XmlPath, c.XmlFileSize, c.XmlMtimeUtc);
            statBuilder.Append('\n');
        }

        return new DiscoveredSources(
            withXml,
            ComputeSha256(string.Join("\n", keyParts))[..16],
            ComputeSha256(statBuilder.ToString()),
            warnings);
    }

    /// <summary>
    /// Opens each candidate's PE header to keep only managed assemblies, and computes the content fingerprint used to
    /// decide whether an existing index must be rebuilt.
    /// </summary>
    public static ResolvedSources Probe(DiscoveredSources discovered)
    {
        var candidates = discovered.Candidates;
        var probed = new (ResolvedAssembly Asm, long XmlSize, long XmlMtime)?[candidates.Count];
        void ProbeAt(int i)
        {
            var c = candidates[i];
            if (HasManagedMetadata(c.FullPath))
                probed[i] = (new ResolvedAssembly(c.FullPath, c.Group, c.XmlPath, c.FileSize, c.MtimeUtc), c.XmlFileSize, c.XmlMtimeUtc);
        }
        if (candidates.Count <= ParallelThreshold)
            for (var i = 0; i < candidates.Count; i++) ProbeAt(i);
        else
            Parallel.For(0, candidates.Count, ProbeAt);

        var warnings = new List<string>(discovered.Warnings);
        var probedList = new List<(ResolvedAssembly Asm, long XmlSize, long XmlMtime)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (probed[i] is { } item)
                probedList.Add(item);
            else if (candidates[i].IsDirect)
                warnings.Add($"Skipping non-managed binary (no CLR metadata): {candidates[i].FullPath}");
        }
        probedList.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Asm.Path, b.Asm.Path));

        var fpBuilder = new StringBuilder();
        foreach (var item in probedList)
        {
            fpBuilder.Append(item.Asm.Path.ToLowerInvariant())
                .Append('|').Append(item.Asm.FileSize)
                .Append('|').Append(item.Asm.MtimeUtc);
            AppendXmlStat(fpBuilder, item.Asm.XmlPath, item.XmlSize, item.XmlMtime);
            fpBuilder.Append('\n');
        }

        var results = probedList.ConvertAll(p => p.Asm);
        return new ResolvedSources(results, discovered.SourceKey, ComputeSha256(fpBuilder.ToString()), warnings, discovered.StatFingerprint);
    }

    private static void AppendXmlStat(StringBuilder sb, string? xmlPath, long xmlFileSize, long xmlMtimeUtc)
    {
        if (xmlPath == null) return;
        if (xmlFileSize >= 0)
        {
            sb.Append("|xml:").Append(xmlFileSize).Append(':').Append(xmlMtimeUtc);
            return;
        }
        var xfi = new FileInfo(xmlPath);
        if (xfi.Exists)
            sb.Append("|xml:").Append(xfi.Length).Append(':').Append(xfi.LastWriteTimeUtc.Ticks);
    }

    private readonly record struct XmlFileMeta(string FullPath, long Size, long MtimeUtc);

    /// <summary>
    /// Per-directory index of companion XML docs (root dir + <see cref="XmlDocReader.LocaleSubdirs"/>) populated
    /// during the single directory-listing pass, avoiding thousands of per-file <c>File.Exists</c> / <c>FileInfo</c> syscalls.
    /// </summary>
    private sealed class DirectoryXmlCache
    {
        private static readonly StringComparer FileNameComparer =
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        private static readonly HashSet<string> LocaleSubdirSet =
            new(XmlDocReader.LocaleSubdirs, FileNameComparer);

        private readonly ConcurrentDictionary<string, Dictionary<string, XmlFileMeta>> _byDir =
            new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        public static bool IsLocaleXmlDir(string name) => LocaleSubdirSet.Contains(name);

        public static string NormalizeDirKey(string dirPath)
        {
            var root = Path.GetPathRoot(dirPath);
            if (!string.IsNullOrEmpty(root) && dirPath.Length <= root.Length)
                return dirPath;
            return Path.TrimEndingDirectorySeparator(dirPath);
        }

        public void RecordDirectory(
            string dirPath,
            Dictionary<string, XmlFileMeta> rootXmls,
            Dictionary<string, DirectoryInfo>? existingLocaleDirs)
        {
            if (existingLocaleDirs is { Count: > 0 })
            {
                // Preserve exact LocaleSubdirs precedence and never overwrite a root XML doc.
                foreach (var sub in XmlDocReader.LocaleSubdirs)
                {
                    if (!existingLocaleDirs.TryGetValue(sub, out var subDir)) continue;
                    TryAddXmlFiles(subDir, rootXmls);
                }
            }
            _byDir[NormalizeDirKey(dirPath)] = rootXmls;
        }

        public bool TryFind(string? dirPath, string xmlFileName, out XmlFileMeta meta)
        {
            if (string.IsNullOrEmpty(dirPath))
            {
                meta = default;
                return false;
            }
            var map = _byDir.GetOrAdd(NormalizeDirKey(dirPath), ScanDirectory);
            return map.TryGetValue(xmlFileName, out meta);
        }

        private static Dictionary<string, XmlFileMeta> ScanDirectory(string dirPath)
        {
            var map = new Dictionary<string, XmlFileMeta>(FileNameComparer);
            TryAddXmlFiles(new DirectoryInfo(dirPath), map);
            foreach (var sub in XmlDocReader.LocaleSubdirs)
            {
                var subDir = new DirectoryInfo(Path.Combine(dirPath, sub));
                if (!subDir.Exists) continue;
                TryAddXmlFiles(subDir, map);
            }
            return map;
        }

        private static void TryAddXmlFiles(DirectoryInfo dir, Dictionary<string, XmlFileMeta> map)
        {
            FileInfo[] xmls;
            try { xmls = dir.GetFiles("*.xml"); }
            catch { return; }

            foreach (var f in xmls)
                map.TryAdd(f.Name, new XmlFileMeta(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks));
        }
    }

    /// <summary>
    /// Lists <c>*.dll</c>/<c>*.exe</c> files in a single directory-listing pass per folder while caching companion
    /// <c>*.xml</c> metadata for that folder and its existing locale subfolders.
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateBinariesSafe(string root, bool recursive, DirectoryXmlCache xmlCache)
    {
        var fileNameComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch { continue; }

            var binaries = new List<FileInfo>();
            var rootXmls = new Dictionary<string, XmlFileMeta>(fileNameComparer);
            Dictionary<string, DirectoryInfo>? existingLocaleDirs = null;
            List<DirectoryInfo>? childDirs = recursive ? new List<DirectoryInfo>() : null;

            foreach (var entry in entries)
            {
                if (entry is FileInfo fi)
                {
                    if (fi.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        fi.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        binaries.Add(fi);
                    }
                    else if (fi.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        rootXmls.TryAdd(fi.Name, new XmlFileMeta(fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks));
                    }
                }
                else if (entry is DirectoryInfo sub)
                {
                    if (DirectoryXmlCache.IsLocaleXmlDir(sub.Name))
                        (existingLocaleDirs ??= new Dictionary<string, DirectoryInfo>(fileNameComparer))[sub.Name] = sub;

                    if (childDirs != null && !IsLikelyLocaleDir(sub.Name))
                        childDirs.Add(sub);
                }
            }

            xmlCache.RecordDirectory(dir.FullName, rootXmls, existingLocaleDirs);

            if (childDirs != null)
            {
                foreach (var sub in childDirs)
                    stack.Push(sub);
            }

            foreach (var b in binaries)
                yield return b;
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

    private sealed record PackageDllCandidate(string FullPath, string? XmlPath, bool IsRuntime);

    /// <summary>
    /// Package roots in restore priority order. <c>project.assets.json</c> records the exact folders NuGet restored into
    /// (<c>globalPackagesFolder</c> from NuGet.config / <c>NUGET_PACKAGES</c>, plus fallback folders such as the SDK's
    /// offline cache), so prefer them over guessing; fall back to the default global packages folder.
    /// </summary>
    internal static IReadOnlyList<string> GetPackageFolders(JsonElement assetsRoot)
    {
        var folders = new List<string>();
        if (assetsRoot.TryGetProperty("packageFolders", out var pf) && pf.ValueKind == JsonValueKind.Object)
        {
            foreach (var folder in pf.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(folder.Name))
                    folders.Add(NormalizeSeparators(folder.Name));
            }
        }

        var defaultRoot = GetNuGetCachePath();
        if (!folders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), Path.TrimEndingDirectorySeparator(defaultRoot), StringComparison.OrdinalIgnoreCase)))
            folders.Add(defaultRoot);
        return folders;
    }

    /// <summary>First package root that actually contains <paramref name="relativePackagePath"/> (e.g. <c>newtonsoft.json/13.0.3</c>).</summary>
    private static string ResolvePackageRoot(IReadOnlyList<string> packageRoots, string relativePackagePath)
    {
        var rel = NormalizeSeparators(relativePackagePath);
        foreach (var root in packageRoots)
        {
            var candidate = Path.Combine(root, rel);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return Path.Combine(packageRoots[0], rel);
    }

    private static IEnumerable<(string dll, string group, string? xmlPath)> ResolveFromAssets(
        string assetsFile,
        string projectDir,
        string? targetFramework,
        List<string> warnings)
    {
        var output = new List<(string dll, string group, string? xmlPath)>();

        try
        {
            using var stream = File.OpenRead(assetsFile);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (!root.TryGetProperty("libraries", out var libraries) ||
                !root.TryGetProperty("targets", out var targets))
                return output;

            var packageRoots = GetPackageFolders(root);

            var tfmEntries = targets.EnumerateObject().ToList();
            if (tfmEntries.Count == 0) return output;

            List<JsonProperty> selectedTfms;
            if (!string.IsNullOrWhiteSpace(targetFramework))
            {
                selectedTfms = tfmEntries
                    .Where(t => MatchesTfm(t.Name, targetFramework.Trim()))
                    .ToList();
                if (selectedTfms.Count == 0)
                {
                    var available = string.Join(", ", tfmEntries.Select(t => t.Name));
                    warnings.Add($"Target framework '{targetFramework}' not found in {assetsFile} (available: {available}).");
                    return output;
                }
            }
            else if (tfmEntries.Count > 1)
            {
                var best = tfmEntries
                    .OrderByDescending(t => ParseTfmScore(t.Name))
                    .First();
                var available = string.Join(", ", tfmEntries.Select(t => t.Name));
                warnings.Add($"Multiple target frameworks found ({available}); selected '{best.Name}'. Pass --tfm to override.");
                selectedTfms = new List<JsonProperty> { best };
            }
            else
            {
                selectedTfms = tfmEntries;
            }

            foreach (var tfm in selectedTfms)
            {
                var preferredShortTfm = ExtractCoreShortTfm(tfm.Name);
                foreach (var pkg in tfm.Value.EnumerateObject())
                {
                    var pkgId = pkg.Name;
                    var pkgInfo = pkg.Value;

                    if (!pkgInfo.TryGetProperty("type", out var t)) continue;
                    var pkgType = t.GetString();

                    if (pkgType == "project")
                    {
                        if (TryResolveProjectReferenceDll(libraries, pkgId, projectDir, preferredShortTfm, out var projDll, out var projXml))
                            output.Add((projDll, pkgId, projXml));
                        continue;
                    }

                    if (pkgType != "package") continue;
                    if (!libraries.TryGetProperty(pkgId, out var lib)) continue;
                    if (!lib.TryGetProperty("path", out var bp)) continue;

                    var pkgPath = bp.GetString();
                    if (string.IsNullOrEmpty(pkgPath)) continue;
                    var pkgRoot = ResolvePackageRoot(packageRoots, pkgPath);

                    // Deduplicate compile (ref/) vs runtime (lib/) by DLL filename within the same package.
                    var byFileName = new Dictionary<string, PackageDllCandidate>(StringComparer.OrdinalIgnoreCase);

                    void ConsiderAssets(string propName, bool isRuntime)
                    {
                        if (!pkgInfo.TryGetProperty(propName, out var section)) return;
                        foreach (var kv in section.EnumerateObject())
                        {
                            if (!kv.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                            var relAsset = NormalizeSeparators(kv.Name);
                            var full = Path.GetFullPath(Path.Combine(pkgRoot, relAsset));
                            if (!File.Exists(full)) continue;
                            var xml = XmlDocReader.FindXmlDocPath(full);
                            var candidate = new PackageDllCandidate(full, xml, isRuntime);
                            var fileName = Path.GetFileName(relAsset);

                            if (!byFileName.TryGetValue(fileName, out var existing) || IsBetterCandidate(candidate, existing))
                                byFileName[fileName] = candidate;
                        }
                    }

                    ConsiderAssets("compile", isRuntime: false);
                    ConsiderAssets("runtime", isRuntime: true);

                    foreach (var cand in byFileName.Values)
                        output.Add((cand.FullPath, pkgId, cand.XmlPath));
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing {assetsFile}: {ex.Message}");
        }

        return output;
    }

    private static bool IsBetterCandidate(PackageDllCandidate next, PackageDllCandidate current)
    {
        var nextHasXml = next.XmlPath != null;
        var currHasXml = current.XmlPath != null;
        if (nextHasXml != currHasXml) return nextHasXml;
        if (next.IsRuntime != current.IsRuntime) return next.IsRuntime;
        return next.FullPath.Length < current.FullPath.Length;
    }

    private static bool TryResolveProjectReferenceDll(
        JsonElement libraries,
        string pkgId,
        string projectDir,
        string preferredShortTfm,
        out string dllPath,
        out string? xmlPath)
    {
        dllPath = string.Empty;
        xmlPath = null;
        if (!libraries.TryGetProperty(pkgId, out var lib)) return false;

        string? relProj = null;
        if (lib.TryGetProperty("msbuildProject", out var mp)) relProj = mp.GetString();
        else if (lib.TryGetProperty("path", out var lp)) relProj = lp.GetString();

        if (string.IsNullOrEmpty(relProj)) return false;
        var csproj = Path.GetFullPath(Path.Combine(projectDir, NormalizeSeparators(relProj)));
        var refDir = File.Exists(csproj) ? Path.GetDirectoryName(csproj) : (Directory.Exists(csproj) ? csproj : null);
        if (string.IsNullOrEmpty(refDir)) return false;

        var binDir = Path.Combine(refDir, "bin");
        if (!Directory.Exists(binDir)) return false;

        var asmName = ReadCsprojAssemblyName(csproj) ?? Path.GetFileNameWithoutExtension(csproj);
        var targetFileName = asmName + ".dll";

        var candidates = Directory.EnumerateFiles(binDir, "*.dll", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetFileName(f), targetFileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => PathContainsTfmSegment(f, preferredShortTfm) ? 1 : 0)
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (candidates.Count == 0) return false;

        dllPath = candidates[0];
        xmlPath = XmlDocReader.FindXmlDocPath(dllPath);
        return true;
    }

    private static string? ReadCsprojAssemblyName(string csprojPath)
    {
        if (!File.Exists(csprojPath)) return null;
        try
        {
            var doc = XDocument.Load(csprojPath);
            var el = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName" && !string.IsNullOrWhiteSpace(e.Value));
            var val = el?.Value.Trim();
            if (!string.IsNullOrEmpty(val) && !MsBuildMacroRegex().IsMatch(val))
                return val;
        }
        catch
        {
            // Fall back to project filename
        }
        return null;
    }

    private static bool PathContainsTfmSegment(string fullPath, string shortTfm)
    {
        if (string.IsNullOrEmpty(shortTfm)) return false;
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals(shortTfm, StringComparison.OrdinalIgnoreCase) ||
                              p.StartsWith(shortTfm + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractCoreShortTfm(string tfmKey)
    {
        var s = ToShortTfm(tfmKey);
        var slash = s.IndexOf('/');
        return slash >= 0 ? s[..slash] : s;
    }

    internal static bool MatchesTfm(string tfmKey, string requested)
    {
        var shortForm = ToShortTfm(tfmKey);
        return tfmKey.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
               tfmKey.StartsWith(requested + "/", StringComparison.OrdinalIgnoreCase) ||
               tfmKey.StartsWith(requested + "-", StringComparison.OrdinalIgnoreCase) ||
               shortForm.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
               shortForm.StartsWith(requested + "-", StringComparison.OrdinalIgnoreCase) ||
               shortForm.StartsWith(requested + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static (int Family, Version Version, int NoRidBonus, int NegLength) ParseTfmScore(string tfmKey)
    {
        var shortTfm = ToShortTfm(tfmKey);
        var slash = shortTfm.IndexOf('/');
        var hasRid = slash >= 0 || shortTfm.Contains('-');
        var core = slash >= 0 ? shortTfm[..slash] : shortTfm;
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];

        var family = 0;
        var ver = new Version(0, 0);

        if (core.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            family = 2;
            Version.TryParse(core["netstandard".Length..], out ver);
        }
        else if (core.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            family = 3;
            Version.TryParse(core["netcoreapp".Length..], out ver);
        }
        else if (core.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var rest = core[3..];
            if (rest.Contains('.'))
            {
                family = 3;
                Version.TryParse(rest, out ver);
            }
            else if (rest.Length >= 2 && rest.All(char.IsDigit))
            {
                family = 1;
                var major = rest[0] - '0';
                var minor = rest[1] - '0';
                var build = rest.Length > 2 && int.TryParse(rest[2..], out var b) ? b : 0;
                ver = new Version(major, minor, build);
            }
        }

        return (family, ver ?? new Version(0, 0), hasRid ? 0 : 1, -tfmKey.Length);
    }

    private static string ToShortTfm(string tfmKey)
    {
        var slash = tfmKey.IndexOf('/');
        var ridSuffix = slash >= 0 ? tfmKey[slash..] : string.Empty;
        var main = slash >= 0 ? tfmKey[..slash] : tfmKey;

        if (main.StartsWith(".NETCoreApp,Version=v", StringComparison.OrdinalIgnoreCase))
            return "net" + main[".NETCoreApp,Version=v".Length..] + ridSuffix;
        if (main.StartsWith(".NETStandard,Version=v", StringComparison.OrdinalIgnoreCase))
            return "netstandard" + main[".NETStandard,Version=v".Length..] + ridSuffix;
        if (main.StartsWith(".NETFramework,Version=v", StringComparison.OrdinalIgnoreCase))
        {
            var v = main[".NETFramework,Version=v".Length..].Replace(".", "");
            return "net" + v + ridSuffix;
        }
        return tfmKey;
    }

    private static IEnumerable<string> GetCsprojHintPaths(string csprojPath, List<string> warnings)
    {
        var refs = new List<string>();
        try
        {
            XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
            var doc = XDocument.Load(csprojPath);
            var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
            var dirWithSep = EnsureTrailingSeparator(dir);
            var solutionDir = FindSolutionDir(dir);

            var props = CollectMsBuildProperties(dir, doc, dirWithSep, solutionDir);

            var hintPaths = doc.Descendants(msbuild + "HintPath")
                .Concat(doc.Descendants("HintPath"))
                .Select(e => e.Value?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var rawHp in hintPaths)
            {
                var hp = ExpandProperties(rawHp!, props);
                if (MsBuildMacroRegex().IsMatch(hp))
                {
                    warnings.Add($"Skipped HintPath with unresolved MSBuild macro: {rawHp}");
                    continue;
                }
                hp = NormalizeSeparators(hp);
                var full = Path.IsPathRooted(hp) ? Path.GetFullPath(hp) : Path.GetFullPath(Path.Combine(dir, hp));
                if (File.Exists(full)) refs.Add(full);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing {csprojPath}: {ex.Message}");
        }
        return refs;
    }

    private static Dictionary<string, string> CollectMsBuildProperties(
        string projectDir,
        XDocument csprojDoc,
        string projectDirWithSep,
        string? solutionDirWithSep)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProjectDir"] = projectDirWithSep,
            ["MSBuildProjectDirectory"] = projectDirWithSep,
            ["MSBuildThisFileDirectory"] = projectDirWithSep,
        };
        if (solutionDirWithSep != null)
            props["SolutionDir"] = solutionDirWithSep;

        // Collect ancestor Directory.Build.props from outermost to innermost
        var buildPropsFiles = new List<string>();
        var cur = new DirectoryInfo(projectDir);
        while (cur != null)
        {
            var candidate = Path.Combine(cur.FullName, "Directory.Build.props");
            if (File.Exists(candidate))
                buildPropsFiles.Add(candidate);
            cur = cur.Parent;
        }
        buildPropsFiles.Reverse();

        foreach (var bpFile in buildPropsFiles)
        {
            try
            {
                var bpDir = EnsureTrailingSeparator(Path.GetDirectoryName(bpFile)!);
                props["MSBuildThisFileDirectory"] = bpDir;
                LoadPropertiesFromXml(XDocument.Load(bpFile), props);
            }
            catch
            {
                // Ignore malformed ancestor Directory.Build.props
            }
        }

        props["MSBuildThisFileDirectory"] = projectDirWithSep;
        LoadPropertiesFromXml(csprojDoc, props);
        return props;
    }

    private static void LoadPropertiesFromXml(XDocument doc, Dictionary<string, string> props)
    {
        var propGroups = doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup");
        // Apply unconditional properties first, then conditional properties as fallbacks if unset
        foreach (var pg in propGroups)
        {
            var groupConditional = pg.Attribute("Condition") != null;
            foreach (var el in pg.Elements())
            {
                var key = el.Name.LocalName;
                if (el.HasElements) continue;
                var val = el.Value?.Trim();
                if (string.IsNullOrEmpty(val)) continue;

                var isConditional = groupConditional || el.Attribute("Condition") != null;
                if (!isConditional || !props.ContainsKey(key))
                    props[key] = ExpandProperties(val, props);
            }
        }
    }

    private static string ExpandProperties(string input, IReadOnlyDictionary<string, string> props)
    {
        var current = input;
        for (var pass = 0; pass < 3; pass++)
        {
            var next = MsBuildMacroRegex().Replace(current, match =>
            {
                var name = match.Groups[1].Value.Trim();
                if (props.TryGetValue(name, out var val))
                    return val;
                var env = Environment.GetEnvironmentVariable(name);
                return !string.IsNullOrEmpty(env) ? env : match.Value;
            });
            if (next == current) break;
            current = next;
        }
        return current;
    }

    private static string EnsureTrailingSeparator(string dir)
        => dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

    private static string NormalizeSeparators(string path)
        => Path.DirectorySeparatorChar == '/' ? path.Replace('\\', '/') : path.Replace('/', '\\');

    private static string? FindSolutionDir(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            if (current.EnumerateFiles("*.sln").Any() || current.EnumerateFiles("*.slnx").Any())
                return EnsureTrailingSeparator(current.FullName);
            current = current.Parent;
        }
        return null;
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
