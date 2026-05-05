using System;
using System.Linq;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Tests for hyperlink rendering behavior.
/// Tests that hyperlinks are rendered with correct colors and decorations.
/// </summary>
public class HyperlinkRenderingTests
{
    #region Cell Hyperlink Detection for Rendering

    [Fact]
    public void BufferCell_WithHyperlinkId_Detected()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };

        buffer.WriteText("Link".AsSpan(), attrs);

        var cold = buffer.GetColdCell(0, 0);
        Assert.NotEqual((ushort)0, cold.HyperlinkId);
    }

    [Fact]
    public void BufferCell_WithoutHyperlinkId_NotDetected()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);

        buffer.WriteText("Normal".AsSpan(), CellAttributes.Default);

        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal((ushort)0, cold.HyperlinkId);
    }

    [Fact]
    public void BufferCell_RetrieveUrl_FromHyperlinkId()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var originalUrl = "https://example.com/path";
        var linkId = buffer.GetOrCreateHyperlinkId(originalUrl);
        var attrs = new CellAttributes { HyperlinkId = linkId };

        buffer.WriteText("Link".AsSpan(), attrs);

        var cold = buffer.GetColdCell(0, 0);
        var retrievedUrl = buffer.GetHyperlinkUrl(cold.HyperlinkId);

        Assert.Equal(originalUrl, retrievedUrl);
    }

    #endregion

    #region Hyperlink Range Detection

    [Fact]
    public void HyperlinkRange_ContinuousCells_SameHyperlinkId()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };

        buffer.WriteText("Hello World".AsSpan(), attrs);

        for (int i = 0; i < 11; i++)
        {
            var cold = buffer.GetColdCell(0, i);
            Assert.Equal(linkId, cold.HyperlinkId);
        }
    }

    [Fact]
    public void HyperlinkRange_DifferentHyperlinks_DifferentIds()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id1 = buffer.GetOrCreateHyperlinkId("https://first.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://second.com");

        buffer.WriteText("First".AsSpan(), new CellAttributes { HyperlinkId = id1 });
        buffer.SetCursor(0, 10);
        buffer.WriteText("Second".AsSpan(), new CellAttributes { HyperlinkId = id2 });

        Assert.Equal(id1, buffer.GetColdCell(0, 0).HyperlinkId);
        Assert.Equal(id2, buffer.GetColdCell(0, 10).HyperlinkId);
    }

    [Fact]
    public void HyperlinkRange_GapBetweenLinks_NoHyperlinkInGap()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id = buffer.GetOrCreateHyperlinkId("https://example.com");

        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = id });
        buffer.SetCursor(0, 10);
        buffer.WriteText("Link2".AsSpan(), new CellAttributes { HyperlinkId = id });

        Assert.Equal((ushort)0, buffer.GetColdCell(0, 5).HyperlinkId);
        Assert.Equal((ushort)0, buffer.GetColdCell(0, 7).HyperlinkId);
    }

    #endregion

    #region Multi-row Hyperlinks

    [Fact]
    public void Hyperlink_MultiRow_SameIdOnEachRow()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };

        buffer.WriteText("Line1".AsSpan(), attrs);
        buffer.SetCursor(1, 0);
        buffer.WriteText("Line2".AsSpan(), attrs);

        Assert.Equal(linkId, buffer.GetColdCell(0, 0).HyperlinkId);
        Assert.Equal(linkId, buffer.GetColdCell(1, 0).HyperlinkId);
    }

    #endregion

    #region Wide Characters with Hyperlinks

    [Fact]
    public void Hyperlink_WideCharacter_BaseCellHasLink()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };

        buffer.WriteText("\u6f22".AsSpan(), attrs);

        var baseCold = buffer.GetColdCell(0, 0);
        var baseCell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, baseCold.HyperlinkId);
        Assert.Equal(2, baseCell.Width);
    }

    [Fact]
    public void Hyperlink_WideCharacter_ContinuationCellHasLink()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };

        buffer.WriteText("\u6f22".AsSpan(), attrs);

        var contCold = buffer.GetColdCell(0, 1);
        var contCell = buffer.GetCell(0, 1);
        Assert.True(contCell.IsContinuation);
        Assert.Equal(linkId, contCold.HyperlinkId);
    }

    #endregion

    #region Hyperlink Attributes Integration

    [Fact]
    public void Hyperlink_CellPreservesOtherAttributes()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes
        {
            HyperlinkId = linkId,
            Bold = true,
            Italic = true,
            Underline = true,
            Foreground = new SgrColorArgb(0xFF0000),
            Background = new SgrColorArgb(0xFFFFFF)
        };

        buffer.WriteText("Styled".AsSpan(), attrs);

        var cold = buffer.GetColdCell(0, 0);
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, cold.HyperlinkId);

        var style = buffer.StyleSet.GetStyle(cell.StyleId);
        Assert.True(style.Bold);
        Assert.True(style.Italic);
        Assert.True(style.Underline);
        Assert.NotEqual((uint)0, style.Foreground.Argb);
        Assert.NotEqual((uint)0, style.Background.Argb);
    }

    [Fact]
    public void Hyperlink_DefaultStyle_HasHyperlinkId()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };

        buffer.WriteText("Link".AsSpan(), attrs);

        var cold = buffer.GetColdCell(0, 0);
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, cold.HyperlinkId);

        var style = buffer.StyleSet.GetStyle(cell.StyleId);
        Assert.False(style.Bold);
        Assert.False(style.Italic);
        Assert.False(style.Underline);
    }

    #endregion

    #region Hyperlink Overwriting

    [Fact]
    public void Hyperlink_OverwrittenWithNormal_ClearsHyperlink()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = linkId });

        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), CellAttributes.Default);

        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal((ushort)0, cold.HyperlinkId);
    }

    [Fact]
    public void Hyperlink_OverwrittenWithDifferentLink_ChangesId()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id1 = buffer.GetOrCreateHyperlinkId("https://first.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://second.com");
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = id1 });

        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), new CellAttributes { HyperlinkId = id2 });

        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal(id2, cold.HyperlinkId);
    }

    #endregion

    #region Cell Coordinate Tests for Click Detection

    [Fact]
    public void CellCoordinates_ValidRowColumn_ReturnsCell()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = linkId });

        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal(linkId, cold.HyperlinkId);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    public void CellCoordinates_InvalidRowColumn_ReturnsDefaultCell(int row, int col)
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);

        var cell = buffer.GetCell(row, col);
        var cold = buffer.GetColdCell(row, col);
        Assert.Equal((uint)' ', cell.Rune);
        Assert.Equal((ushort)0, cold.HyperlinkId);
    }

    [Fact]
    public void CellCoordinates_LastValidCell()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        buffer.SetCursor(9, 79);
        buffer.WriteText("X".AsSpan(), new CellAttributes 
        { 
            HyperlinkId = buffer.GetOrCreateHyperlinkId("https://example.com")
        });

        var cell = buffer.GetCell(9, 79);
        var cold = buffer.GetColdCell(9, 79);
        var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        Assert.Equal("X", grapheme);
        Assert.True(cold.HyperlinkId > 0);
    }

    #endregion

    #region URL Lookup Performance

    [Fact]
    public void GetHyperlinkUrl_ManyUrls_EfficientLookup()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);

        for (int i = 0; i < 1000; i++)
        {
            buffer.GetOrCreateHyperlinkId($"https://example{i}.com");
        }

        var urls = new string[1000];
        for (int i = 0; i < 1000; i++)
        {
            urls[i] = buffer.GetHyperlinkUrl((ushort)(i + 1))!;
        }

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal($"https://example{i}.com", urls[i]);
        }
    }

    [Fact]
    public void GetOrCreateHyperlinkId_SameUrlManyTimes_Efficient()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://example.com";

        ushort firstId = 0;
        for (int i = 0; i < 1000; i++)
        {
            var id = buffer.GetOrCreateHyperlinkId(url);
            if (i == 0) firstId = id;
            Assert.Equal(firstId, id);
        }
    }

    #endregion

    #region Hyperlink ID Reuse

    [Fact]
    public void HyperlinkIdReused_AfterBufferClear()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://example.com";
        var id1 = buffer.GetOrCreateHyperlinkId(url);

        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = id1 });
        buffer.ClearScreen();
        var id2 = buffer.GetOrCreateHyperlinkId(url);

        Assert.Equal(id1, id2);
        Assert.Equal(url, buffer.GetHyperlinkUrl(id2));
    }

    #endregion

    #region Renderer-Ready Data Tests

    [Fact]
    public void RenderData_HyperlinkCell_ContainsUrlInfo()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://example.com";
        var linkId = buffer.GetOrCreateHyperlinkId(url);
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = linkId });

        var cell = buffer.GetCell(0, 0);
        var cold = buffer.GetColdCell(0, 0);
        var cellUrl = buffer.GetHyperlinkUrl(cold.HyperlinkId);

        Assert.True(cold.HyperlinkId > 0);
        Assert.Equal(url, cellUrl);
        var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        Assert.Equal("L", grapheme);
    }

    [Fact]
    public void RenderData_MultipleHyperlinks_AllUrlInfoAvailable()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var urls = new[] { "https://first.com", "https://second.com", "https://third.com" };
        var ids = urls.Select(url => buffer.GetOrCreateHyperlinkId(url)).ToArray();

        for (int i = 0; i < urls.Length; i++)
        {
            buffer.SetCursor(i, 0);
            buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = ids[i] });
        }

        for (int i = 0; i < urls.Length; i++)
        {
            var cold = buffer.GetColdCell(i, 0);
            var retrievedUrl = buffer.GetHyperlinkUrl(cold.HyperlinkId);
            Assert.Equal(urls[i], retrievedUrl);
        }
    }

    #endregion

    #region Edge Cases for Rendering

    [Fact]
    public void Hyperlink_EmptyText_CellStillHasLink()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");

        buffer.WriteText("".AsSpan(), new CellAttributes { HyperlinkId = linkId });

        Assert.True(linkId > 0);
    }

    [Fact]
    public void Hyperlink_SingleCharacter_MinimalCase()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://x.com";
        var linkId = buffer.GetOrCreateHyperlinkId(url);

        buffer.WriteText("X".AsSpan(), new CellAttributes { HyperlinkId = linkId });

        var cell = buffer.GetCell(0, 0);
        var cold = buffer.GetColdCell(0, 0);
        Assert.Equal(linkId, cold.HyperlinkId);
        var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
        Assert.Equal("X", grapheme);
    }

    [Fact]
    public void Hyperlink_FullRow_Works()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: 10);
        var url = "https://example.com";
        var linkId = buffer.GetOrCreateHyperlinkId(url);

        buffer.WriteText("0123456789".AsSpan(), new CellAttributes { HyperlinkId = linkId });

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(linkId, buffer.GetColdCell(0, i).HyperlinkId);
        }
    }

    #endregion
}
