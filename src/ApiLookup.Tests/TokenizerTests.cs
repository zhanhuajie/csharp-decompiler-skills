using Xunit;

namespace ApiLookup.Tests;

public class TokenizerTests
{
    [Fact]
    public void BuildSearchTokens_SplitsCamelCaseAndAdjacentPairs()
    {
        var tokens = Tokenizer.BuildSearchTokens("CreateAutoCADOffScreenDevice");
        Assert.Contains("CreateAutoCADOffScreenDevice", tokens);
        Assert.Contains("Off", tokens);
        Assert.Contains("Screen", tokens);
        Assert.Contains("OffScreen", tokens);
        Assert.Contains("AutoCAD", tokens);
    }

    [Fact]
    public void BuildFtsQuery_StripsSpecialCharsAndSupportsCamelCase()
    {
        var fts = Tokenizer.BuildFtsQuery("C++ List<T> offscreen device");
        Assert.NotNull(fts);
        Assert.DoesNotContain("+", fts);
        Assert.DoesNotContain("<", fts);
        Assert.Contains("\"offscreen\"*", fts);
        Assert.Contains("\"device\"*", fts);
    }

    [Fact]
    public void CjkTokenization_ProducesBigramsAndSearchableQuery()
    {
        var summaryTokens = Tokenizer.BuildCjkSummaryTokens("三维管道段处理器");
        Assert.Contains("三维", summaryTokens);
        Assert.Contains("维管", summaryTokens);
        Assert.Contains("管道", summaryTokens);
        Assert.Contains("道段", summaryTokens);

        Assert.Equal(string.Empty, Tokenizer.BuildCjkSummaryTokens("Pure English summary text"));

        var fts = Tokenizer.BuildFtsQuery("三维管道");
        Assert.NotNull(fts);
        Assert.Contains("\"三维\"", fts);
        Assert.Contains("\"维管\"", fts);
        Assert.Contains("\"管道\"", fts);
    }
}
