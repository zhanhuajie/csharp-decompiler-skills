using Microsoft.Data.Sqlite;

namespace ApiLookup;

public static class IndexCatalog
{
    public static string DefaultCacheDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(baseDir, "csharp-api-lookup", "indexes");
    }

    public static string GetNamedIndexPath(string name, string? cacheDir = null)
    {
        var safe = SanitizeName(name);
        return Path.Combine(cacheDir ?? DefaultCacheDirectory(), $"named-{safe}.db");
    }

    public static string GetAutoCachePath(string sourceKey, string? cacheDir = null)
        => Path.Combine(cacheDir ?? DefaultCacheDirectory(), $"auto-{sourceKey}.db");

    public static string ResolveDbPath(string? indexName, string? explicitDbPath, string? sourceKey, string? cacheDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDbPath))
            return Path.GetFullPath(explicitDbPath);
        if (!string.IsNullOrWhiteSpace(indexName))
            return GetNamedIndexPath(indexName, cacheDir);
        if (!string.IsNullOrWhiteSpace(sourceKey))
            return GetAutoCachePath(sourceKey, cacheDir);
        throw new InvalidOperationException("No index name, db path, or source provided.");
    }

    public static IReadOnlyList<IndexInfo> ListIndexes(string? cacheDir = null)
    {
        var dir = cacheDir ?? DefaultCacheDirectory();
        if (!Directory.Exists(dir)) return Array.Empty<IndexInfo>();

        var list = new List<IndexInfo>();
        foreach (var file in Directory.GetFiles(dir, "*.db").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var store = IndexStore.OpenReadOnly(file);
                var fileName = Path.GetFileName(file);
                var isNamed = fileName.StartsWith("named-", StringComparison.OrdinalIgnoreCase);
                var name = store.GetMeta("index_name") ?? Path.GetFileNameWithoutExtension(file);
                var builtAt = store.GetMeta("built_at_utc") ?? string.Empty;
                var sources = store.GetMeta("sources") ?? string.Empty;
                var asmCount = (int)store.ScalarLong("SELECT COUNT(*) FROM assemblies");
                var typeCount = (int)store.ScalarLong("SELECT COUNT(*) FROM types");
                var memCount = (int)store.ScalarLong("SELECT COUNT(*) FROM members");
                var size = new FileInfo(file).Length;
                list.Add(new IndexInfo(name, file, builtAt, asmCount, typeCount, memCount, size, sources, isNamed));
            }
            catch
            {
                // Ignore unreadable or legacy DB files while listing
            }
        }
        return list;
    }

    public static bool DropNamedIndex(string name, string? cacheDir = null)
    {
        var path = GetNamedIndexPath(name, cacheDir);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public static PruneResult PruneAutoIndexes(string? cacheDir = null)
    {
        var dir = cacheDir ?? DefaultCacheDirectory();
        if (!Directory.Exists(dir))
            return new PruneResult(0, 0, Array.Empty<string>());

        var removed = new List<string>();
        long freedBytes = 0;

        foreach (var file in Directory.GetFiles(dir, "auto-*.db").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var len = new FileInfo(file).Length;
                File.Delete(file);
                removed.Add(file);
                freedBytes += len;
            }
            catch
            {
                // Skip files currently locked by another process
            }
        }

        return new PruneResult(removed.Count, freedBytes, removed);
    }

    public static IDisposable AcquireBuildLock(string dbPath, TimeSpan? timeout = null)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var lockPath = dbPath + ".lock";
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new BuildLockHandle(stream, lockPath);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string SanitizeName(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray();
        return new string(chars);
    }

    private sealed class BuildLockHandle : IDisposable
    {
        private readonly FileStream _stream;
        private readonly string _lockPath;

        public BuildLockHandle(FileStream stream, string lockPath)
        {
            _stream = stream;
            _lockPath = lockPath;
        }

        public void Dispose()
        {
            _stream.Dispose();
            try { File.Delete(_lockPath); } catch { }
        }
    }
}
