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
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act
        buffer.WriteText("Link".AsSpan(), attrs);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.NotEqual((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void BufferCell_WithoutHyperlinkId_NotDetected()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act
        buffer.WriteText("Normal".AsSpan(), CellAttributes.Default);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void BufferCell_RetrieveUrl_FromHyperlinkId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var originalUrl = "https://example.com/path";
        var linkId = buffer.GetOrCreateHyperlinkId(originalUrl);
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        buffer.WriteText("Link".AsSpan(), attrs);
        
        // Act
        var cell = buffer.GetCell(0, 0);
        var retrievedUrl = buffer.GetHyperlinkUrl(cell.HyperlinkId);
        
        // Assert
        Assert.Equal(originalUrl, retrievedUrl);
    }

    #endregion

    #region Hyperlink Range Detection

    [Fact]
    public void HyperlinkRange_ContinuousCells_SameHyperlinkId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act - Write multiple characters with same hyperlink
        buffer.WriteText("Hello World".AsSpan(), attrs);
        
        // Assert - All cells should have the same hyperlink ID
        for (int i = 0; i < 11; i++)
        {
            var cell = buffer.GetCell(0, i);
            Assert.Equal(linkId, cell.HyperlinkId);
        }
    }

    [Fact]
    public void HyperlinkRange_DifferentHyperlinks_DifferentIds()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id1 = buffer.GetOrCreateHyperlinkId("https://first.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://second.com");
        
        // Act
        buffer.WriteText("First".AsSpan(), new CellAttributes { HyperlinkId = id1 });
        buffer.SetCursor(0, 10);
        buffer.WriteText("Second".AsSpan(), new CellAttributes { HyperlinkId = id2 });
        
        // Assert
        Assert.Equal(id1, buffer.GetCell(0, 0).HyperlinkId);
        Assert.Equal(id2, buffer.GetCell(0, 10).HyperlinkId);
    }

    [Fact]
    public void HyperlinkRange_GapBetweenLinks_NoHyperlinkInGap()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id = buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Act
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = id });
        // Gap at positions 4-9
        buffer.SetCursor(0, 10);
        buffer.WriteText("Link2".AsSpan(), new CellAttributes { HyperlinkId = id });
        
        // Assert
        Assert.Equal((ushort)0, buffer.GetCell(0, 5).HyperlinkId);
        Assert.Equal((ushort)0, buffer.GetCell(0, 7).HyperlinkId);
    }

    #endregion

    #region Multi-row Hyperlinks

    [Fact]
    public void Hyperlink_MultiRow_SameIdOnEachRow()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act - Write on multiple rows with same hyperlink
        buffer.WriteText("Line1".AsSpan(), attrs);
        buffer.SetCursor(1, 0);
        buffer.WriteText("Line2".AsSpan(), attrs);
        
        // Assert
        Assert.Equal(linkId, buffer.GetCell(0, 0).HyperlinkId);
        Assert.Equal(linkId, buffer.GetCell(1, 0).HyperlinkId);
    }

    #endregion

    #region Wide Characters with Hyperlinks

    [Fact]
    public void Hyperlink_WideCharacter_BaseCellHasLink()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act - Write wide character with hyperlink
        buffer.WriteText("\u6f22".AsSpan(), attrs); // 漢
        
        // Assert
        var baseCell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, baseCell.HyperlinkId);
        Assert.Equal(2, baseCell.Width);
    }

    [Fact]
    public void Hyperlink_WideCharacter_ContinuationCellHasLink()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act
        buffer.WriteText("\u6f22".AsSpan(), attrs); // 漢
        
        // Assert - continuation cell should have hyperlink for consistent rendering
        var contCell = buffer.GetCell(0, 1);
        Assert.True(contCell.IsContinuation);
        // The continuation cell should also have the hyperlink ID
        Assert.Equal(linkId, contCell.HyperlinkId);
    }

    #endregion

    #region Hyperlink Attributes Integration

    [Fact]
    public void Hyperlink_CellPreservesOtherAttributes()
    {
        // Arrange
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
        
        // Act
        buffer.WriteText("Styled".AsSpan(), attrs);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, cell.HyperlinkId);
        Assert.True(cell.Bold);
        Assert.True(cell.Italic);
        Assert.True(cell.Underline);
        Assert.NotEqual((uint)0, cell.Foreground);
        Assert.NotEqual((uint)0, cell.Background);
    }

    [Fact]
    public void Hyperlink_DefaultStyle_HasHyperlinkId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act
        buffer.WriteText("Link".AsSpan(), attrs);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, cell.HyperlinkId);
        // Other attributes should be default
        Assert.False(cell.Bold);
        Assert.False(cell.Italic);
        Assert.False(cell.Underline);
    }

    #endregion

    #region Hyperlink Overwriting

    [Fact]
    public void Hyperlink_OverwrittenWithNormal_ClearsHyperlink()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = linkId });
        
        // Act - Overwrite with normal text
        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), CellAttributes.Default);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void Hyperlink_OverwrittenWithDifferentLink_ChangesId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id1 = buffer.GetOrCreateHyperlinkId("https://first.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://second.com");
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = id1 });
        
        // Act
        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), new CellAttributes { HyperlinkId = id2 });
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(id2, cell.HyperlinkId);
    }

    #endregion

    #region Cell Coordinate Tests for Click Detection

    [Fact]
    public void CellCoordinates_ValidRowColumn_ReturnsCell()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = linkId });
        
        // Act
        var cell = buffer.GetCell(0, 0);
        
        // Assert
        // Cell is a struct/value type, so it's never null
        Assert.Equal(linkId, cell.HyperlinkId);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    public void CellCoordinates_InvalidRowColumn_ReturnsDefaultCell(int row, int col)
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act
        var cell = buffer.GetCell(row, col);
        
        // Assert - Out of bounds returns a default cell with space
        Assert.Equal((uint)' ', cell.Rune);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void CellCoordinates_LastValidCell()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        buffer.SetCursor(9, 79);
        buffer.WriteText("X".AsSpan(), new CellAttributes 
        { 
            HyperlinkId = buffer.GetOrCreateHyperlinkId("https://example.com")
        });
        
        // Act
        var cell = buffer.GetCell(9, 79);
        
        // Assert
        Assert.Equal("X", cell.Grapheme);
        Assert.True(cell.HyperlinkId > 0);
    }

    #endregion

    #region URL Lookup Performance

    [Fact]
    public void GetHyperlinkUrl_ManyUrls_EfficientLookup()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Add many URLs
        for (int i = 0; i < 1000; i++)
        {
            buffer.GetOrCreateHyperlinkId($"https://example{i}.com");
        }
        
        // Act - Lookup should be O(1) per URL
        var urls = new string[1000];
        for (int i = 0; i < 1000; i++)
        {
            urls[i] = buffer.GetHyperlinkUrl((ushort)(i + 1))!;
        }
        
        // Assert
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal($"https://example{i}.com", urls[i]);
        }
    }

    [Fact]
    public void GetOrCreateHyperlinkId_SameUrlManyTimes_Efficient()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://example.com";
        
        // Act - Getting same URL many times should use dictionary lookup
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
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://example.com";
        var id1 = buffer.GetOrCreateHyperlinkId(url);
        
        // Act - Write, clear, write again
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = id1 });
        buffer.ClearScreen();
        var id2 = buffer.GetOrCreateHyperlinkId(url);
        
        // Assert - URL lookup should still work
        Assert.Equal(id1, id2);
        Assert.Equal(url, buffer.GetHyperlinkUrl(id2));
    }

    #endregion

    #region Renderer-Ready Data Tests

    [Fact]
    public void RenderData_HyperlinkCell_ContainsUrlInfo()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://example.com";
        var linkId = buffer.GetOrCreateHyperlinkId(url);
        buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = linkId });
        
        // Act - Get all data needed for rendering
        var cell = buffer.GetCell(0, 0);
        var cellUrl = buffer.GetHyperlinkUrl(cell.HyperlinkId);
        
        // Assert
        Assert.True(cell.HyperlinkId > 0);
        Assert.Equal(url, cellUrl);
        Assert.Equal("L", cell.Grapheme);
    }

    [Fact]
    public void RenderData_MultipleHyperlinks_AllUrlInfoAvailable()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var urls = new[] { "https://first.com", "https://second.com", "https://third.com" };
        var ids = urls.Select(url => buffer.GetOrCreateHyperlinkId(url)).ToArray();
        
        // Write cells with different hyperlinks
        for (int i = 0; i < urls.Length; i++)
        {
            buffer.SetCursor(i, 0);
            buffer.WriteText("Link".AsSpan(), new CellAttributes { HyperlinkId = ids[i] });
        }
        
        // Act & Assert
        for (int i = 0; i < urls.Length; i++)
        {
            var cell = buffer.GetCell(i, 0);
            var retrievedUrl = buffer.GetHyperlinkUrl(cell.HyperlinkId);
            Assert.Equal(urls[i], retrievedUrl);
        }
    }

    #endregion

    #region Edge Cases for Rendering

    [Fact]
    public void Hyperlink_EmptyText_CellStillHasLink()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Act - Write empty text with hyperlink
        buffer.WriteText("".AsSpan(), new CellAttributes { HyperlinkId = linkId });
        
        // Assert - No cells written, but ID is valid
        Assert.True(linkId > 0);
    }

    [Fact]
    public void Hyperlink_SingleCharacter_MinimalCase()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var url = "https://x.com";
        var linkId = buffer.GetOrCreateHyperlinkId(url);
        
        // Act
        buffer.WriteText("X".AsSpan(), new CellAttributes { HyperlinkId = linkId });
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, cell.HyperlinkId);
        Assert.Equal("X", cell.Grapheme);
    }

    [Fact]
    public void Hyperlink_FullRow_Works()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 10); // Small buffer for test
        var url = "https://example.com";
        var linkId = buffer.GetOrCreateHyperlinkId(url);
        
        // Act - Fill entire row
        buffer.WriteText("0123456789".AsSpan(), new CellAttributes { HyperlinkId = linkId });
        
        // Assert
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(linkId, buffer.GetCell(0, i).HyperlinkId);
        }
    }

    #endregion
}
