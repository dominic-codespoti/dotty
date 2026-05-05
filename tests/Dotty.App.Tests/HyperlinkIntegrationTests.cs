using System;
using System.Linq;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Integration tests for the full OSC 8 hyperlink flow.
/// Tests the complete pipeline: OSC sequence -> parser -> adapter -> buffer -> storage.
/// </summary>
public class HyperlinkIntegrationTests
{
    #region End-to-End OSC 8 Flow

    [Fact]
    public void FullFlow_Osc8Sequence_TextHasHyperlink()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Click here\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);
        var url = adapter.Buffer.GetHyperlinkUrl(cold.HyperlinkId);
        Assert.Equal("https://example.com", url);
    }

    [Fact]
    public void FullFlow_HyperlinkEnd_TextAfterHasNoHyperlink()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Link\u001b]8;;\u0007 Normal";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var linkCold = adapter.Buffer.GetColdCell(0, 0);
        var normalCold = adapter.Buffer.GetColdCell(0, 6);

        Assert.True(linkCold.HyperlinkId > 0);
        Assert.Equal((ushort)0, normalCold.HyperlinkId);
    }

    [Fact]
    public void FullFlow_MultipleHyperlinks_StoredCorrectly()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://first.com\u0007First\u001b]8;;\u0007 text \u001b]8;;https://second.com\u0007Second\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var firstCold = adapter.Buffer.GetColdCell(0, 0);
        var secondCold = adapter.Buffer.GetColdCell(0, 12);

        var firstUrl = adapter.Buffer.GetHyperlinkUrl(firstCold.HyperlinkId);
        var secondUrl = adapter.Buffer.GetHyperlinkUrl(secondCold.HyperlinkId);

        Assert.Equal("https://first.com", firstUrl);
        Assert.Equal("https://second.com", secondUrl);
        Assert.NotEqual(firstCold.HyperlinkId, secondCold.HyperlinkId);
    }

    [Fact]
    public void FullFlow_SameUrl_ReusesHyperlinkId()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Link1\u001b]8;;\u0007 \u001b]8;;https://example.com\u0007Link2\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold1 = adapter.Buffer.GetColdCell(0, 0);
        var cold2 = adapter.Buffer.GetColdCell(0, 7);

        Assert.Equal(cold1.HyperlinkId, cold2.HyperlinkId);
    }

    [Fact]
    public void FullFlow_DifferentUrls_DifferentHyperlinkIds()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Link1\u001b]8;;\u0007 \u001b]8;;https://other.com\u0007Link2\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold1 = adapter.Buffer.GetColdCell(0, 0);
        var cold2 = adapter.Buffer.GetColdCell(0, 7);

        Assert.NotEqual(cold1.HyperlinkId, cold2.HyperlinkId);
    }

    #endregion

    #region OSC 8 with Various Terminators

    [Fact]
    public void FullFlow_BelTerminator_Works()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Text";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);
    }

    [Fact]
    public void FullFlow_STTerminator_Works()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u001b\\Text";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);
    }

    [Fact]
    public void FullFlow_MixedTerminators_Works()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://first.com\u0007First\u001b]8;;\u001b\\Second\u001b]8;;\u0007Text";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);
    }

    #endregion

    #region Hyperlink with Text Attributes

    [Fact]
    public void FullFlow_HyperlinkWithBold_PreservesAttributes()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b[1m\u001b]8;;https://example.com\u0007BoldLink\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);

        var cell = adapter.Buffer.GetCell(0, 0);
        var style = adapter.Buffer.StyleSet.GetStyle(cell.StyleId);
        Assert.True(style.Bold);
    }

    [Fact]
    public void FullFlow_HyperlinkWithColor_PreservesColor()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b[31m\u001b]8;;https://example.com\u0007RedLink\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);

        var cell = adapter.Buffer.GetCell(0, 0);
        var style = adapter.Buffer.StyleSet.GetStyle(cell.StyleId);
        Assert.NotEqual((uint)0, style.Foreground.Argb);
    }

    [Fact]
    public void FullFlow_HyperlinkWithReset_ResetClearsHyperlink()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Link\u001b[0mText";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var linkCold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(linkCold.HyperlinkId > 0);
    }

    #endregion

    #region Multiline Hyperlinks

    [Fact]
    public void FullFlow_HyperlinkSpanningLines_Works()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Line1\nLine2\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold1 = adapter.Buffer.GetColdCell(0, 0);
        var cold2 = adapter.Buffer.GetColdCell(1, 0);

        Assert.True(cold1.HyperlinkId > 0);
        if (cold2.HyperlinkId > 0)
        {
            Assert.Equal(cold1.HyperlinkId, cold2.HyperlinkId);
        }
    }

    #endregion

    #region Hyperlink URL Retrieval

    [Fact]
    public void FullFlow_RetrieveUrlFromCell_ReturnsCorrectUrl()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com/page\u0007Link\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        var url = adapter.Buffer.GetHyperlinkUrl(cold.HyperlinkId);
        Assert.Equal("https://example.com/page", url);
    }

    [Fact]
    public void FullFlow_InvalidHyperlinkId_ReturnsNull()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);

        var url = adapter.Buffer.GetHyperlinkUrl(999);

        Assert.Null(url);
    }

    [Fact]
    public void FullFlow_ZeroHyperlinkId_ReturnsNull()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);

        var url = adapter.Buffer.GetHyperlinkUrl(0);

        Assert.Null(url);
    }

    #endregion

    #region Complex Scenarios

    [Fact]
    public void FullFlow_NestedHyperlinks_LastOneWins()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://first.com\u0007First\u001b]8;;https://second.com\u0007Second\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var secondCold = adapter.Buffer.GetColdCell(0, 6);
        var url = adapter.Buffer.GetHyperlinkUrl(secondCold.HyperlinkId);
        Assert.Equal("https://second.com", url);
    }

    [Fact]
    public void FullFlow_EmptyHyperlinkUrl_EndsHyperlink()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Link\u001b]8;;\u0007Normal";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var normalCold = adapter.Buffer.GetColdCell(0, 5);
        Assert.Equal((ushort)0, normalCold.HyperlinkId);
    }

    [Fact]
    public void FullFlow_LongUrl_StoredCorrectly()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var longUrl = "https://example.com/" + new string('a', 1000);
        var input = $"\u001b]8;;{longUrl}\u0007Link\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        var retrievedUrl = adapter.Buffer.GetHyperlinkUrl(cold.HyperlinkId);
        Assert.Equal(longUrl, retrievedUrl);
    }

    [Fact]
    public void FullFlow_UrlWithSpecialCharacters_StoredCorrectly()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var url = "https://example.com/path?query=value&foo=bar#section";
        var input = $"\u001b]8;;{url}\u0007Link\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        var retrievedUrl = adapter.Buffer.GetHyperlinkUrl(cold.HyperlinkId);
        Assert.Equal(url, retrievedUrl);
    }

    [Fact]
    public void FullFlow_HyperlinkWithCursorMove_MovesCorrectly()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007\u001b[5CLink\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 5);
        var cell = adapter.Buffer.GetCell(0, 5);
        Assert.True(cold.HyperlinkId > 0);
        var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        Assert.Equal("L", grapheme);
    }

    #endregion

    #region Security Scenarios

    [Fact]
    public void FullFlow_JavaScriptUrl_ParsedButNotValidatedAtParserLevel()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;javascript:alert('xss')\u0007Link\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);
        var url = adapter.Buffer.GetHyperlinkUrl(cold.HyperlinkId);
        Assert.Equal("javascript:alert('xss')", url);
    }

    [Fact]
    public void FullFlow_DataUrl_ParsedButNotValidatedAtParserLevel()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;data:text/html,<script>alert('xss')</script>\u0007Link\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);
    }

    #endregion

    #region Buffer Operations with Hyperlinks

    [Fact]
    public void FullFlow_ClearScreen_RemovesHyperlinksFromCells()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Link\u001b]8;;\u0007";
        parser.Feed(Encoding.UTF8.GetBytes(input));

        Assert.True(adapter.Buffer.GetColdCell(0, 0).HyperlinkId > 0);

        adapter.OnEraseDisplay(2);

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.Equal((ushort)0, cold.HyperlinkId);
    }

    [Fact]
    public void FullFlow_ResizeBuffer_PreservesHyperlinks()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007Link\u001b]8;;\u0007";
        parser.Feed(Encoding.UTF8.GetBytes(input));

        var originalId = adapter.Buffer.GetColdCell(0, 0).HyperlinkId;

        adapter.ResizeBuffer(20, 120);

        var cold = adapter.Buffer.GetColdCell(0, 0);
        Assert.Equal(originalId, cold.HyperlinkId);
    }

    [Fact]
    public void FullFlow_SaveRestoreCursor_PreservesHyperlinkAttribute()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007\u001b7More\u001b8Text\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold2 = adapter.Buffer.GetColdCell(0, 0);

        Assert.True(cold2.HyperlinkId > 0);
    }

    #endregion

    #region Unicode Support

    [Fact]
    public void FullFlow_UnicodeUrl_StoredCorrectly()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var url = "https://example.com/\u4e2d\u6587";
        var input = $"\u001b]8;;{url}\u0007Link\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        var retrievedUrl = adapter.Buffer.GetHyperlinkUrl(cold.HyperlinkId);
        Assert.Equal(url, retrievedUrl);
    }

    [Fact]
    public void FullFlow_UnicodeHyperlinkText_Preserved()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;

        var input = "\u001b]8;;https://example.com\u0007\u4e2d\u6587\u001b]8;;\u0007";

        parser.Feed(Encoding.UTF8.GetBytes(input));

        var cold = adapter.Buffer.GetColdCell(0, 0);
        var cell = adapter.Buffer.GetCell(0, 0);
        Assert.True(cold.HyperlinkId > 0);
        var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        Assert.Equal("\u4e2d", grapheme);
    }

    #endregion
}
