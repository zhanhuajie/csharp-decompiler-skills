using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace ApiLookup.Tests;

[Collection(EnvironmentSensitiveCollection.Name)]
public class CliTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDll;
    private readonly string? _prevCacheEnv;

    public CliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-cli-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ParseArgs_SupportsFlagsBeforeCommandLimit30AndValidation()
    {
        var opts = CliParser.Parse(new[] { "--json", "--limit", "30", "hierarchy", "SampleBaseEntity", "--tfm", "net8.0" });
        Assert.True(opts.Json);
        Assert.Equal(30, opts.Limit);
        Assert.Equal("hierarchy", opts.Command);
        Assert.Equal(new[] { "SampleBaseEntity" }, opts.Positional);
        Assert.Equal("net8.0", opts.TargetFramework);

        Assert.Throws<ArgumentException>(() => CliParser.Parse(new[] { "by-type", "Wall", "--role", "invalid" }));
        Assert.Throws<ArgumentException>(() => CliParser.Parse(new[] { "search", "Wall", "--kind", "invalid" }));
    }

    [Fact]
    public void Run_EndToEndCommandsConsistentCamelCaseJsonAndErrorEnvelopesWork()
    {
        var dbPath = Path.Combine(_tempDir, "cli-test.db");

        // 1. --version
        var verOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "--version" }, verOut, new StringWriter()));
        Assert.StartsWith("apilookup ", verOut.ToString().Trim());

        // 2. Build DB explicitly
        var idxOut = new StringWriter();
        var idxErr = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "index", "--db", dbPath, "--dll", _fixtureDll }, idxOut, idxErr));

        // 3. search --list-refs --json emits single valid camelCase JSON object
        var searchOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "search", "offscreen", "--db", dbPath, "--list-refs", "--json" }, searchOut, new StringWriter()));
        using (var doc = JsonDocument.Parse(searchOut.ToString()))
        {
            Assert.True(doc.RootElement.TryGetProperty("assemblies", out var asms));
            Assert.Equal(1, asms.GetArrayLength());
            Assert.True(doc.RootElement.TryGetProperty("hits", out var hits));
            Assert.True(hits.GetArrayLength() >= 1);
            Assert.True(hits[0].TryGetProperty("fullName", out _));
        }

        // 4. show ambiguous type returns exit code 2 and structured AmbiguousTypesResult JSON
        var ambOut = new StringWriter();
        var ambCode = Program.Run(new[] { "show", "ISampleItem", "--db", dbPath, "--json" }, ambOut, new StringWriter());
        Assert.Equal(2, ambCode);
        using (var doc = JsonDocument.Parse(ambOut.ToString()))
        {
            Assert.True(doc.RootElement.GetProperty("ambiguous").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("candidates").GetArrayLength() >= 2);
        }

        // 5. show interface --inherited includes parent interface members
        var ifaceOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "show", "IDerivedSampleItem", "--db", dbPath, "--inherited" }, ifaceOut, new StringWriter()));
        var ifaceText = ifaceOut.ToString();
        Assert.Contains("SubCategory", ifaceText);
        Assert.Contains("Inherited from ApiLookup.Tests.Fixtures.ISampleItem", ifaceText);
        Assert.Contains("ItemCode", ifaceText);

        // 6. show delegate prints clean single-line delegate declaration
        var delOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "show", "SampleFilterHandler", "--db", dbPath }, delOut, new StringWriter()));
        var delText = delOut.ToString();
        Assert.Contains("public delegate bool SampleFilterHandler(SampleBaseEntity entity, int depth);", delText);
        Assert.DoesNotContain("BeginInvoke", delText);

        // 6b. show class deriving from constructed generic base preserves generic arguments
        var genericBaseOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "show", "SamplePipeContainer", "--db", dbPath }, genericBaseOut, new StringWriter()));
        Assert.Contains("public class SamplePipeContainer : SampleContainer<SamplePipeSegment>", genericBaseOut.ToString());

        // 7. --json error contract: missing type or invalid flag emits valid ErrorJsonResult to stdout with exit code 1
        var missingOut = new StringWriter();
        Assert.Equal(1, Program.Run(new[] { "show", "DefinitelyMissingTypeXyz", "--db", dbPath, "--json" }, missingOut, new StringWriter()));
        using (var doc = JsonDocument.Parse(missingOut.ToString()))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("DefinitelyMissingTypeXyz", doc.RootElement.GetProperty("error").GetString());
        }

        var badArgOut = new StringWriter();
        Assert.Equal(1, Program.Run(new[] { "search", "Wall", "--kind", "invalid", "--json" }, badArgOut, new StringWriter()));
        using (var doc = JsonDocument.Parse(badArgOut.ToString()))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("Invalid --kind", doc.RootElement.GetProperty("error").GetString());
        }

        // 8. index --drop missing --json in isolated APILOOKUP_CACHE_DIR returns exit code 1 and valid JSON
        var dropOut = new StringWriter();
        Assert.Equal(1, Program.Run(new[] { "index", "--drop", "definitely-nonexistent-index-xyz", "--json" }, dropOut, new StringWriter()));
        using (var doc = JsonDocument.Parse(dropOut.ToString()))
        {
            Assert.False(doc.RootElement.GetProperty("dropped").GetBoolean());
        }

        // 9. locate / scan zero-cache PE scan with --list-refs --json & --include-nonpublic (consistent camelCase!)
        var locOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "locate", "InternalFixtureHelper", "--dll", _fixtureDll, "--include-nonpublic", "--list-refs", "--json" }, locOut, new StringWriter()));
        using (var doc = JsonDocument.Parse(locOut.ToString()))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("scannedAssemblies").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("references").GetArrayLength());
            var matches = doc.RootElement.GetProperty("matches");
            Assert.Equal(1, matches.GetArrayLength());
            Assert.Equal("ApiLookup.Tests.Fixtures.InternalFixtureHelper", matches[0].GetProperty("fullName").GetString());
        }

        var scanTextOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "scan", "SamplePipeSegment", "--db", dbPath }, scanTextOut, new StringWriter()));
        Assert.Contains("[FOUND] ApiLookup.Tests.Fixtures.SamplePipeSegment", scanTextOut.ToString());
    }
}
