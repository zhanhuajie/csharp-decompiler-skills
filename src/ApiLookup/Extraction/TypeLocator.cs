using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ApiLookup;

/// <summary>
/// Fast zero-cache type locator that scans PE type definitions and type forwarders in parallel
/// without building a SQLite index.
/// </summary>
public static class TypeLocator
{
    private sealed record RawLocateCandidate(LocateHit Hit, string BareFullName, bool IsForwarder);

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

        var qBare = TypeNameHelper.StripGenerics(q);
        var isQualified = qBare.Contains('.') || qBare.Contains('+');
        var lastSep = qBare.LastIndexOfAny(new[] { '.', '+' });
        var targetLeaf = lastSep >= 0 ? qBare[(lastSep + 1)..] : qBare;
        var nsTrim = string.IsNullOrWhiteSpace(namespacePrefix) ? null : namespacePrefix.Trim().TrimEnd('.');
        var asmSub = string.IsNullOrWhiteSpace(assemblyFilter) ? null : assemblyFilter.Trim();

        var found = new ConcurrentBag<RawLocateCandidate>();
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

                    var (ns, clrName, displayFull, bareFull, _, simpleName) = TypeNameHelper.BuildTypeNames(md, td);

                    if (!MatchesNamespace(ns, nsTrim))
                        continue;

                    if (isQualified && !MatchesQualified(q, qBare, displayFull, bareFull, clrName))
                        continue;

                    var acc = MetadataExtractor.GetEffectiveVisibility(md, td);
                    found.Add(new RawLocateCandidate(
                        new LocateHit(displayFull, clrName, ns, simpleName, acc, asmName, asm.Path, asm.Group),
                        bareFull,
                        IsForwarder: false));
                }

                // Also scan [TypeForwardedTo] entries in facade assemblies (e.g. netstandard, System.Runtime)
                foreach (var handle in md.ExportedTypes)
                {
                    var et = md.GetExportedType(handle);
                    if (!et.IsForwarder) continue;

                    var rawName = md.GetString(et.Name);
                    var bareLeaf = DisplaySignatureProvider.StripGenericArity(rawName);
                    if (!bareLeaf.Equals(targetLeaf, StringComparison.OrdinalIgnoreCase) &&
                        !rawName.Equals(targetLeaf, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var (ns, clrName, displayFull, bareFull, simpleName) = TypeNameHelper.BuildExportedTypeNames(md, et);
                    if (!MatchesNamespace(ns, nsTrim))
                        continue;
                    if (isQualified && !MatchesQualified(q, qBare, displayFull, bareFull, clrName))
                        continue;

                    var targetAsm = ResolveForwarderTargetAssembly(md, et);
                    var acc = string.IsNullOrEmpty(targetAsm) ? "forwarded" : $"forwarded -> {targetAsm}";
                    found.Add(new RawLocateCandidate(
                        new LocateHit(displayFull, clrName, ns, simpleName, acc, asmName, asm.Path, asm.Group),
                        bareFull,
                        IsForwarder: true));
                }
            }
            catch (Exception ex)
            {
                scanWarnings.Add($"Failed to scan '{asm.Path}': {ex.Message}");
            }
        });

        foreach (var w in scanWarnings)
            warnings?.Add(w);

        var definedTypes = found
            .Where(c => !c.IsForwarder)
            .Select(c => c.BareFullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<(string ClrName, string Path)>(LocateHitKeyComparer.Instance);
        return found
            // Suppress facade forwarder stubs when the concrete TypeDefinition was already located in the scan set
            .Where(c => !c.IsForwarder || !definedTypes.Contains(c.BareFullName))
            .Select(c => c.Hit)
            .OrderBy(h => h.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.AssemblyPath, StringComparer.OrdinalIgnoreCase)
            .Where(h => seen.Add((h.ClrName, h.AssemblyPath)))
            .Take(limit)
            .ToList();
    }

    private static string ResolveForwarderTargetAssembly(MetadataReader md, ExportedType et)
    {
        var cur = et;
        while (cur.Implementation.Kind == HandleKind.ExportedType)
            cur = md.GetExportedType((ExportedTypeHandle)cur.Implementation);

        if (cur.Implementation.Kind == HandleKind.AssemblyReference)
            return md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)cur.Implementation).Name);

        return string.Empty;
    }

    private static bool MatchesNamespace(string ns, string? nsTrim)
    {
        if (string.IsNullOrEmpty(nsTrim)) return true;
        return ns.Equals(nsTrim, StringComparison.OrdinalIgnoreCase) ||
               ns.StartsWith(nsTrim + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesQualified(string q, string qBare, string displayFull, string bareFull, string clrName)
    {
        var qDot = qBare.Replace('+', '.');
        return displayFull.Equals(q, StringComparison.OrdinalIgnoreCase) ||
               bareFull.Equals(qDot, StringComparison.OrdinalIgnoreCase) ||
               clrName.Equals(q, StringComparison.OrdinalIgnoreCase) ||
               clrName.Equals(qBare, StringComparison.OrdinalIgnoreCase) ||
               bareFull.EndsWith("." + qDot, StringComparison.OrdinalIgnoreCase) ||
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
