using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ApiLookup;

/// <summary>
/// Fast zero-cache type locator that scans PE type definitions in parallel without building a SQLite index.
/// Replaces the legacy TypeScanner.csx script.
/// </summary>
public static class TypeLocator
{
    public static IReadOnlyList<LocateHit> Locate(
        IReadOnlyList<ResolvedAssembly> assemblies,
        string typeQuery,
        string? namespacePrefix = null,
        string? assemblyFilter = null,
        bool includeNonPublic = false,
        int limit = 100,
        List<string>? warnings = null)
    {
        var q = typeQuery.Trim();
        if (string.IsNullOrEmpty(q) || assemblies.Count == 0 || limit <= 0)
            return Array.Empty<LocateHit>();

        var qBare = MetadataExtractor.StripGenerics(q);
        var isQualified = qBare.Contains('.') || qBare.Contains('+');
        var lastSep = qBare.LastIndexOfAny(new[] { '.', '+' });
        var targetLeaf = lastSep >= 0 ? qBare[(lastSep + 1)..] : qBare;
        var nsTrim = string.IsNullOrWhiteSpace(namespacePrefix) ? null : namespacePrefix.Trim().TrimEnd('.');
        var asmSub = string.IsNullOrWhiteSpace(assemblyFilter) ? null : assemblyFilter.Trim();

        var found = new ConcurrentBag<LocateHit>();
        var scanWarnings = new ConcurrentBag<string>();

        Parallel.ForEach(assemblies, asm =>
        {
            if (!File.Exists(asm.Path)) return;
            try
            {
                using var fs = new FileStream(asm.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) return;

                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) return;

                var asmName = md.GetString(md.GetAssemblyDefinition().Name);
                if (asmSub != null && !asmName.Contains(asmSub, StringComparison.OrdinalIgnoreCase))
                    return;

                foreach (var handle in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(handle);
                    var rawName = md.GetString(td.Name);
                    var bareLeaf = DisplaySignatureProvider.StripGenericArity(rawName);
                    if (!bareLeaf.Equals(targetLeaf, StringComparison.OrdinalIgnoreCase) &&
                        !rawName.Equals(targetLeaf, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (MetadataExtractor.ShouldSkipType(md, td, rawName, includeNonPublic))
                        continue;

                    var (ns, clrName, displayFull, bareFull, _, simpleName) = MetadataExtractor.BuildTypeNames(md, td);

                    if (!string.IsNullOrEmpty(nsTrim) &&
                        !ns.Equals(nsTrim, StringComparison.OrdinalIgnoreCase) &&
                        !ns.StartsWith(nsTrim + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (isQualified && !MatchesQualified(q, qBare, displayFull, bareFull, clrName))
                        continue;

                    var acc = MetadataExtractor.GetEffectiveVisibility(md, td);
                    found.Add(new LocateHit(displayFull, clrName, ns, simpleName, acc, asmName, asm.Path, asm.Group));
                }
            }
            catch (Exception ex)
            {
                scanWarnings.Add($"Failed to scan '{asm.Path}': {ex.Message}");
            }
        });

        foreach (var w in scanWarnings)
            warnings?.Add(w);

        var seen = new HashSet<(string ClrName, string Path)>(LocateHitKeyComparer.Instance);
        return found
            .OrderBy(h => h.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.AssemblyPath, StringComparer.OrdinalIgnoreCase)
            .Where(h => seen.Add((h.ClrName, h.AssemblyPath)))
            .Take(limit)
            .ToList();
    }

    private static bool MatchesQualified(string q, string qBare, string displayFull, string bareFull, string clrName)
    {
        return displayFull.Equals(q, StringComparison.OrdinalIgnoreCase) ||
               bareFull.Equals(qBare, StringComparison.OrdinalIgnoreCase) ||
               clrName.Equals(q, StringComparison.OrdinalIgnoreCase) ||
               clrName.Equals(qBare, StringComparison.OrdinalIgnoreCase) ||
               bareFull.EndsWith("." + qBare, StringComparison.OrdinalIgnoreCase) ||
               clrName.EndsWith("." + qBare, StringComparison.OrdinalIgnoreCase) ||
               clrName.EndsWith("+" + qBare, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LocateHitKeyComparer : IEqualityComparer<(string ClrName, string Path)>
    {
        public static readonly LocateHitKeyComparer Instance = new();

        public bool Equals((string ClrName, string Path) x, (string ClrName, string Path) y)
            => string.Equals(x.ClrName, y.ClrName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ClrName, string Path) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ClrName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
    }
}
