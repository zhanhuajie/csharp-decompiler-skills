using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace ApiLookup.Tests;

public class CliTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDll;

    public CliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "apilookup-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fixtureDll = typeof(Fixtures.SamplePipeSegment).Assembly.Location;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ParseArgs_SupportsFlagsBeforeCommandLimit30AndValidation()
    {
        var opts = Program.ParseArgs(new[] { "--json", "--limit", "30", "hierarchy", "SampleBaseEntity", "--tfm", "net8.0" });
        Assert.True(opts.Json);
        Assert.Equal(30, opts.Limit);
        Assert.Equal("hierarchy", opts.Command);
        Assert.Equal(new[] { "SampleBaseEntity" }, opts.Positional);
        Assert.Equal("net8.0", opts.TargetFramework);

        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "by-type", "Wall", "--role", "invalid" }));
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "search", "Wall", "--kind", "invalid" }));
    }

    [Fact]
    public void Run_EndToEndCommandsAndJsonOutputsWork()
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

        // 3. search --list-refs --json emits single valid JSON object
        var searchOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "search", "offscreen", "--db", dbPath, "--list-refs", "--json" }, searchOut, new StringWriter()));
        using (var doc = JsonDocument.Parse(searchOut.ToString()))
        {
            Assert.True(doc.RootElement.TryGetProperty("assemblies", out var asms));
            Assert.True(asms.GetArrayLength() == 1);
            Assert.True(doc.RootElement.TryGetProperty("hits", out var hits));
            Assert.True(hits.GetArrayLength() >= 1);
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

        // 5. show delegate prints clean single-line delegate declaration
        var delOut = new StringWriter();
        Assert.Equal(0, Program.Run(new[] { "show", "SampleFilterHandler", "--db", dbPath }, delOut, new StringWriter()));
        var delText = delOut.ToString();
        Assert.Contains("public delegate bool SampleFilterHandler(SampleBaseEntity entity, int depth);", delText);
        Assert.DoesNotContain("BeginInvoke", delText);

        // 6. index --drop missing --json returns exit code 1 and valid JSON
        var dropOut = new StringWriter();
        Assert.Equal(1, Program.Run(new[] { "index", "--drop", "definitely-nonexistent-index-xyz", "--json" }, dropOut, new StringWriter()));
        using (var doc = JsonDocument.Parse(dropOut.ToString()))
        {
            Assert.False(doc.RootElement.GetProperty("dropped").GetBoolean());
        }
    }
}
