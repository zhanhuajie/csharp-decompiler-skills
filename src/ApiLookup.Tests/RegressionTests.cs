using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ApiLookup.Tests;

/// <summary>Regression tests for robustness and CLI-usability fixes.</summary>
[Collection(EnvironmentSensitiveCollection.Name)]
public class RegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDll;
    private readonly string? _prevCacheEnv;

    public RegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-regression-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;
        _prevCacheEnv = Environment.GetEnvironmentVariable(IndexCatalog.CacheDirEnvVar);
        Environment.SetEnvironmentVariable(IndexCatalog.CacheDirEnvVar, Path.Combine(_tempDir, "isolated-cache"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(IndexCatalog.CacheDirEnvVar, _prevCacheEnv);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    // ---- Index rebuild over unusable databases (previously leaked a handle and failed with "Access denied") ----

    [Fact]
    public void Index_RebuildsOverCorruptDatabaseFile()
    {
        var dbPath = Path.Combine(_tempDir, "corrupt.db");
        File.WriteAllText(dbPath, new string('x', 4096));

        var err = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "index", "--db", dbPath, "--dll", _fixtureDll }, new StringWriter(), err));

        var searchOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "search", "SamplePipeSegment", "--db", dbPath }, searchOut, new StringWriter()));
        Assert.Contains("ApiLookup.Tests.Fixtures.SamplePipeSegment", searchOut.ToString());
    }

    [Fact]
    public void Index_RebuildsOverLegacyDatabaseWithoutMetaTable()
    {
        var dbPath = Path.Combine(_tempDir, "legacy.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE legacy_types (id INTEGER PRIMARY KEY, name TEXT);";
            cmd.ExecuteNonQuery();
        }

        var statsOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "index", "--db", dbPath, "--dll", _fixtureDll }, statsOut, new StringWriter()));
        Assert.Contains("[Built] database", statsOut.ToString());

        // A second run must hit the up-to-date fast path and release its handle as well.
        var again = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "index", "--db", dbPath, "--dll", _fixtureDll }, again, new StringWriter()));
        Assert.Contains("[Up-to-date] database", again.ToString());
    }

    // ---- Mistyped commands must fail loudly instead of silently searching ----

    [Fact]
    public void MistypedCommand_FailsWithSuggestionInsteadOfSilentSearch()
    {
        var dbPath = Path.Combine(_tempDir, "typo.db");
        Assert.Equal(0, Program.Run(new[] { "index", "--db", dbPath, "--dll", _fixtureDll }, new StringWriter(), new StringWriter()));

        var err = new StringWriter();
        Assert.Equal(1, Program.Run(new[] { "serach", "SamplePipeSegment", "--db", dbPath }, new StringWriter(), err));
        Assert.Contains("Unknown command 'serach'. Did you mean 'search'?", err.ToString());

        var jsonOut = new StringWriter();
        Assert.Equal(1, Program.Run(new[] { "hierachy", "SampleBaseEntity", "--db", dbPath, "--json" }, jsonOut, new StringWriter()));
        using var doc = JsonDocument.Parse(jsonOut.ToString());
        Assert.Contains("Did you mean 'hierarchy'?", doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("offscreen")]
    [InlineData("SamplePipeSegment")]
    [InlineData("Serach")]          // not all-lowercase -> treated as a symbol, not a command typo
    [InlineData("pipe-segment-kind")]
    public void SearchShorthand_StillWorksForSymbolNames(string word)
    {
        var opts = Program.ParseArgs(new[] { word, "--db", "x.db" });
        Assert.Equal("search", opts.Command);
        Assert.Equal(new[] { word }, opts.Positional);
    }

    [Fact]
    public void EditDistance_CountsTranspositionAsSingleEdit()
    {
        Assert.Equal(1, Program.EditDistance("serach", "search"));
        Assert.Equal(1, Program.EditDistance("hierachy", "hierarchy"));
        Assert.Null(Program.SuggestClosest("offscreen", new[] { "search", "show", "index" }));
    }

    // ---- Per-command option validation ----

    [Theory]
    [InlineData("search", "Wall", "--role", "return")]
    [InlineData("show", "Wall", "--limit", "3")]
    [InlineData("refs", "--member", "Port")]
    [InlineData("hierarchy", "Part", "--inherited")]
    [InlineData("locate", "Part", "--docs")]
    [InlineData("search", "Wall", "--list")]
    [InlineData("index", "--list", "--dir", "C:\\sdk")]
    [InlineData("index", "--list", "--prune")]
    [InlineData("index", "--drop", "x", "--index", "y")]
    [InlineData("show", "Wall", "Door")]
    [InlineData("refs", "extra")]
    [InlineData("search", "Wall", "--namspace", "Autodesk")]
    public void InvalidOptionUsage_IsRejected(params string[] args)
        => Assert.Throws<ArgumentException>(() => Program.ParseArgs(args));

    [Theory]
    [InlineData("by-type", "Wall", "--role", "return", "--ns", "Autodesk", "--limit", "5")]
    [InlineData("show", "Wall", "--member", "Create", "--inherited", "--ext", "--docs")]
    [InlineData("scan", "Wall", "--limit", "2", "--list-refs", "--dll", "a.dll")]
    [InlineData("search", "offscreen device", "--kind", "method", "--asm", "Acad")]
    [InlineData("--list")]
    [InlineData("index", "--prune", "--json")]
    [InlineData("show", "--help", "--limit", "3")]  // help short-circuits validation
    [InlineData("--version", "--verbose")]
    public void ValidOptionUsage_IsAccepted(params string[] args)
        => Program.ParseArgs(args);

    [Fact]
    public void InvalidOptionUsage_MessagesAreActionable()
    {
        var notValid = Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "search", "Wall", "--role", "return" }));
        Assert.Contains("Option '--role' is not valid for 'search'", notValid.Message);

        var unknown = Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "search", "Wall", "--namspace", "A" }));
        Assert.Contains("Did you mean '--namespace'?", unknown.Message);
    }

    // ---- Error output consistency ----

    [Fact]
    public void TypeNotFound_UsesErrorPrefixAndVerbosePrintsStackTrace()
    {
        var dbPath = Path.Combine(_tempDir, "nf.db");
        Assert.Equal(0, Program.Run(new[] { "index", "--db", dbPath, "--dll", _fixtureDll }, new StringWriter(), new StringWriter()));

        var err = new StringWriter();
        Assert.Equal(1, Program.Run(new[] { "show", "DefinitelyMissingTypeXyz", "--db", dbPath }, new StringWriter(), err));
        Assert.StartsWith("Error: Type 'DefinitelyMissingTypeXyz' not found", err.ToString());

        var verboseErr = new StringWriter();
        var missingDb = Path.Combine(_tempDir, "does-not-exist.db");
        Assert.Equal(1, Program.Run(new[] { "refs", "--db", missingDb, "--verbose" }, new StringWriter(), verboseErr));
        Assert.Contains("System.IO.FileNotFoundException", verboseErr.ToString());
    }

    // ---- Version / build provenance ----

    [Fact]
    public void VersionVerbose_PrintsEmbeddedSourceHash()
    {
        var hash = Program.GetSourceHash();
        Assert.NotNull(hash);
        Assert.Matches("^[0-9a-f]{16}$", hash);

        var plain = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "--version" }, plain, new StringWriter()));
        Assert.DoesNotContain("source-hash", plain.ToString());

        var verbose = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "--version", "--verbose" }, verbose, new StringWriter()));
        Assert.Contains($"source-hash: {hash}", verbose.ToString());
    }

    // ---- Prune removes abandoned temp builds but never live ones ----

    [Fact]
    public void Prune_RemovesStaleTempBuildsButKeepsLiveOnes()
    {
        var cacheDir = Path.Combine(_tempDir, "prune-cache");
        Directory.CreateDirectory(cacheDir);

        var deadOwner = Path.Combine(cacheDir, $"named-a.db{IndexCatalog.TempBuildMarker}{int.MaxValue}-0123456789abcdef");
        var liveOwner = Path.Combine(cacheDir, $"named-b.db{IndexCatalog.TempBuildMarker}{Environment.ProcessId}-0123456789abcdef");
        var abandoned = Path.Combine(cacheDir, $"named-c.db{IndexCatalog.TempBuildMarker}{Environment.ProcessId}-fedcba9876543210");
        foreach (var f in new[] { deadOwner, liveOwner, abandoned })
            File.WriteAllText(f, "partial build");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - IndexCatalog.TempBuildMaxAge - TimeSpan.FromHours(1));

        var result = IndexCatalog.PruneAutoIndexes(cacheDir);

        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(2, result.RemovedTempCount);
        Assert.False(File.Exists(deadOwner));
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(liveOwner));
    }

    [Fact]
    public void SuccessfulBuild_LeavesNoTempFilesBehind()
    {
        var cacheDir = Path.Combine(_tempDir, "tmp-cleanup");
        Directory.CreateDirectory(cacheDir);
        var dbPath = Path.Combine(cacheDir, "x.db");
        Assert.Equal(0, Program.Run(new[] { "index", "--db", dbPath, "--dll", _fixtureDll }, new StringWriter(), new StringWriter()));

        Assert.Empty(Directory.GetFiles(cacheDir, "*" + IndexCatalog.TempBuildMarker + "*"));
    }
}
