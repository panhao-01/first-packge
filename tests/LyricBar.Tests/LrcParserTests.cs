using LyricBar.Services;
using Xunit;

namespace LyricBar.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parse_BasicLines_ProducesSortedLines()
    {
        var lrc = "[ti:test]\n[00:10.00]Second\n[00:03.50]First\n";
        var lines = LrcParser.Parse(lrc);

        Assert.Equal(2, lines.Count);
        Assert.Equal("First", lines[0].Text);
        Assert.Equal(3500, lines[0].TimeMs);
        Assert.Equal("Second", lines[1].Text);
        Assert.Equal(10000, lines[1].TimeMs);
    }

    [Fact]
    public void Parse_MultipleTagsOnOneLine_ExpandsToMultipleEntries()
    {
        var lines = LrcParser.Parse("[00:01.00][00:05.00]Repeat");
        Assert.Equal(2, lines.Count);
        Assert.Equal(1000, lines[0].TimeMs);
        Assert.Equal(5000, lines[1].TimeMs);
        Assert.All(lines, l => Assert.Equal("Repeat", l.Text));
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmptyList()
    {
        Assert.Empty(LrcParser.Parse(null));
        Assert.Empty(LrcParser.Parse(""));
        Assert.Empty(LrcParser.Parse("[ar:someone] no timestamps here"));
    }

    [Fact]
    public void FindIndex_ReturnsActiveLine()
    {
        var lines = LrcParser.Parse("[00:01.00]A\n[00:10.00]B\n[00:20.00]C");
        Assert.Equal(-1, LrcParser.FindIndex(lines, 500));
        Assert.Equal(0, LrcParser.FindIndex(lines, 2000));
        Assert.Equal(1, LrcParser.FindIndex(lines, 15000));
        Assert.Equal(2, LrcParser.FindIndex(lines, 99999));
    }
}

public class SearchMatcherTests
{
    [Fact]
    public void Score_ExactTitleAndArtist_Highest()
    {
        var exact = SearchMatcher.Score("晴天", "周杰伦", "晴天", "周杰伦");
        var partial = SearchMatcher.Score("晴天 Live", "周杰伦", "晴天", "周杰伦");
        Assert.True(exact > partial);
        Assert.True(exact >= 100);
    }

    [Fact]
    public void Normalize_StripsPunctuationAndSpaces()
    {
        Assert.Equal("sunnyday晴天live", SearchMatcher.Normalize("Sunny Day! 晴天（Live）"));
    }

    [Fact]
    public void Score_WrongSong_Low()
    {
        var score = SearchMatcher.Score("七里香", "周杰伦", "晴天", "周杰伦");
        Assert.True(score < 50);
    }
}

