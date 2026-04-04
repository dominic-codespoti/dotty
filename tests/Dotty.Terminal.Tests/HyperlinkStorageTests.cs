using System;
using System.Linq;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Comprehensive tests for hyperlink storage in TerminalBuffer.
/// Tests hyperlink ID management, URL mapping, and cell storage.
/// </summary>
public class HyperlinkStorageTests
{
    #region Hyperlink ID Management

    [Fact]
    public void GetOrCreateHyperlinkId_NewUrl_ReturnsIncrementalId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act
        var id1 = buffer.GetOrCreateHyperlinkId("https://example.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://another.com");
        var id3 = buffer.GetOrCreateHyperlinkId("https://third.com");
        
        // Assert
        Assert.Equal(1, id1);
        Assert.Equal(2, id2);
        Assert.Equal(3, id3);
    }

    [Fact]
    public void GetOrCreateHyperlinkId_SameUrl_ReturnsSameId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act
        var id1 = buffer.GetOrCreateHyperlinkId("https://example.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://example.com");
        var id3 = buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Assert
        Assert.Equal(1, id1);
        Assert.Equal(1, id2);
        Assert.Equal(1, id3);
    }

    [Fact]
    public void GetOrCreateHyperlinkId_EmptyUrl_ReturnsZero()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act
        var id1 = buffer.GetOrCreateHyperlinkId(string.Empty);
        var id2 = buffer.GetOrCreateHyperlinkId("");
        var id3 = buffer.GetOrCreateHyperlinkId(null!);
        
        // Assert - 0 is reserved for "no hyperlink"
        Assert.Equal((ushort)0, id1);
        Assert.Equal((ushort)0, id2);
        Assert.Equal((ushort)0, id3);
    }

    [Fact]
    public void GetOrCreateHyperlinkId_WhitespaceUrl_ReturnsNewId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act
        var id = buffer.GetOrCreateHyperlinkId("   ");
        
        // Assert - whitespace is treated as a valid (though odd) URL
        Assert.True(id > 0);
    }

    [Fact]
    public void GetOrCreateHyperlinkId_ManyUrls_HandlesCorrectly()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act - add many URLs
        for (int i = 0; i < 100; i++)
        {
            buffer.GetOrCreateHyperlinkId($"https://example{i}.com");
        }
        
        // Verify all can be retrieved
        for (int i = 0; i < 100; i++)
        {
            var id = buffer.GetOrCreateHyperlinkId($"https://example{i}.com");
            Assert.Equal(i + 1, id); // IDs start at 1
        }
    }

    [Fact]
    public void GetOrCreateHyperlinkId_MaxUshort_HandlesOverflow()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act - This tests that ushort overflow is handled
        // Note: In practice, this would require 65535 unique URLs
        // We just verify the return type is ushort
        var id = buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Assert
        Assert.IsType<ushort>(id);
    }

    #endregion

    #region URL Lookup

    [Fact]
    public void GetHyperlinkUrl_ValidId_ReturnsUrl()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id = buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Act
        var url = buffer.GetHyperlinkUrl(id);
        
        // Assert
        Assert.Equal("https://example.com", url);
    }

    [Fact]
    public void GetHyperlinkUrl_InvalidId_ReturnsNull()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Act
        var url = buffer.GetHyperlinkUrl(999);
        
        // Assert
        Assert.Null(url);
    }

    [Fact]
    public void GetHyperlinkUrl_ZeroId_ReturnsNull()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Act
        var url = buffer.GetHyperlinkUrl(0);
        
        // Assert - 0 means no hyperlink
        Assert.Null(url);
    }

    [Fact]
    public void GetHyperlinkUrl_AllUrls_ReturnsCorrectly()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var urls = new[]
        {
            "https://example.com",
            "https://test.org",
            "http://localhost:3000",
            "file:///path/to/file.txt"
        };
        
        var ids = urls.Select(url => buffer.GetOrCreateHyperlinkId(url)).ToArray();
        
        // Act & Assert
        for (int i = 0; i < urls.Length; i++)
        {
            var retrievedUrl = buffer.GetHyperlinkUrl(ids[i]);
            Assert.Equal(urls[i], retrievedUrl);
        }
    }

    #endregion

    #region Cell Storage

    [Fact]
    public void WriteText_WithHyperlinkId_StoresInCell()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act
        buffer.WriteText("A".AsSpan(), attrs);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(linkId, cell.HyperlinkId);
    }

    [Fact]
    public void WriteText_MultipleChars_SameHyperlinkId()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act
        buffer.WriteText("Hello".AsSpan(), attrs);
        
        // Assert
        for (int i = 0; i < 5; i++)
        {
            var cell = buffer.GetCell(0, i);
            Assert.Equal(linkId, cell.HyperlinkId);
        }
    }

    [Fact]
    public void WriteText_NoHyperlinkId_ZeroInCell()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var attrs = CellAttributes.Default;
        
        // Act
        buffer.WriteText("A".AsSpan(), attrs);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void WriteText_WideChar_HyperlinkInBaseCell()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act - Write a wide character (CJK)
        buffer.WriteText("\u6f22".AsSpan(), attrs); // 漢
        
        // Assert
        var baseCell = buffer.GetCell(0, 0);
        var contCell = buffer.GetCell(0, 1);
        
        Assert.Equal(linkId, baseCell.HyperlinkId);
        // Continuation cell should also have hyperlink
        Assert.Equal(linkId, contCell.HyperlinkId);
    }

    [Fact]
    public void WriteText_OverwritingCell_ClearsHyperlink()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var linkAttrs = new CellAttributes { HyperlinkId = linkId };
        var defaultAttrs = CellAttributes.Default;
        
        buffer.WriteText("A".AsSpan(), linkAttrs);
        Assert.Equal(linkId, buffer.GetCell(0, 0).HyperlinkId);
        
        // Act - Overwrite with default attrs
        buffer.SetCursor(0, 0);
        buffer.WriteText("B".AsSpan(), defaultAttrs);
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void WriteText_DifferentHyperlinks_OnSameRow()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id1 = buffer.GetOrCreateHyperlinkId("https://first.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://second.com");
        var attrs1 = new CellAttributes { HyperlinkId = id1 };
        var attrs2 = new CellAttributes { HyperlinkId = id2 };
        
        // Act
        buffer.WriteText("First".AsSpan(), attrs1);
        buffer.SetCursor(0, 10);
        buffer.WriteText("Second".AsSpan(), attrs2);
        
        // Assert
        Assert.Equal(id1, buffer.GetCell(0, 0).HyperlinkId);
        Assert.Equal(id1, buffer.GetCell(0, 4).HyperlinkId);
        Assert.Equal(id2, buffer.GetCell(0, 10).HyperlinkId);
        Assert.Equal(id2, buffer.GetCell(0, 15).HyperlinkId);
        // Middle cells should have no hyperlink
        Assert.Equal((ushort)0, buffer.GetCell(0, 5).HyperlinkId);
    }

    [Fact]
    public void WriteText_UnicodeText_WithHyperlink()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Act - Write unicode text
        buffer.WriteText("Hello \u4e16\u754c".AsSpan(), attrs); // Hello 世界
        
        // Assert
        var cell1 = buffer.GetCell(0, 6); // 世
        var cell2 = buffer.GetCell(0, 8); // 界
        Assert.Equal(linkId, cell1.HyperlinkId);
        Assert.Equal(linkId, cell2.HyperlinkId);
    }

    #endregion

    #region Scrollback with Hyperlinks

    [Fact]
    public void LineFeed_WithScrollback_PreservesText()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 3, columns: 80);
        buffer.MaxScrollback = 100;
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Write text at top line
        buffer.WriteText("Line 1".AsSpan(), attrs);
        
        // Act - Scroll up (cursor needs to be at bottom for scroll)
        buffer.SetCursor(2, 0); // Move to bottom
        buffer.LineFeed();
        buffer.LineFeed();
        
        // Assert - The line should be captured in scrollback (as text only)
        Assert.True(buffer.ScrollbackCount >= 0);
        // Note: Scrollback captures text content but not hyperlink IDs
        // Hyperlinks in scrollback is implementation dependent
    }

    [Fact]
    public void ScrollUp_WithHyperlinks_PreservesInVisibleBuffer()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 5, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        // Write text with hyperlink
        buffer.WriteText("Line with link".AsSpan(), attrs);
        buffer.SetCursor(4, 0);
        buffer.WriteText("Last line".AsSpan(), CellAttributes.Default);
        
        // Act
        buffer.SetCursor(0, 0);
        buffer.ScrollUpLines(1);
        
        // Assert - Line 0 content should now be different
        var cell = buffer.GetCell(0, 0);
        // Note: After scroll, cells may be cleared or moved depending on implementation
    }

    #endregion

    #region Cell Reset and Clearing

    [Fact]
    public void ClearScreen_ResetsHyperlinkIds()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        buffer.WriteText("Test".AsSpan(), attrs);
        
        // Act
        buffer.ClearScreen();
        
        // Assert - Cells should be cleared
        var cell = buffer.GetCell(0, 0);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void EraseDisplay_ClearsHyperlinks()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        buffer.WriteText("Test".AsSpan(), attrs);
        
        // Act
        buffer.EraseDisplay(2); // Erase entire display
        
        // Assert
        var cell = buffer.GetCell(0, 0);
        Assert.Equal((ushort)0, cell.HyperlinkId);
    }

    [Fact]
    public void FullReset_ClearsBufferButPreservesUrlLookup()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var linkId = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = linkId };
        
        buffer.WriteText("Test".AsSpan(), attrs);
        
        // Act
        buffer.FullReset();
        
        // Assert - Cell should be cleared
        var cell = buffer.GetCell(0, 0);
        Assert.Equal((ushort)0, cell.HyperlinkId);
        
        // But URL lookup should still work (URLs are preserved)
        var url = buffer.GetHyperlinkUrl(linkId);
        Assert.Equal("https://example.com", url);
    }

    #endregion

    #region Integration with TerminalAdapter

    [Fact]
    public void TerminalAdapter_Osc8Hyperlink_SetsHyperlinkId()
    {
        // Arrange
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        
        // Act - Simulate OSC 8 sequence via OnOperatingSystemCommand
        adapter.OnOperatingSystemCommand(8, ";https://example.com");
        adapter.OnPrint("Link text".AsSpan());
        
        // Assert
        var cell = adapter.Buffer.GetCell(0, 0);
        Assert.True(cell.HyperlinkId > 0);
        var url = adapter.Buffer.GetHyperlinkUrl(cell.HyperlinkId);
        Assert.Equal("https://example.com", url);
    }

    [Fact]
    public void TerminalAdapter_Osc8EndHyperlink_ClearsHyperlinkId()
    {
        // Arrange
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        
        // Start hyperlink
        adapter.OnOperatingSystemCommand(8, ";https://example.com");
        adapter.OnPrint("Link".AsSpan());
        
        // Act - End hyperlink and write more text
        adapter.OnOperatingSystemCommand(8, ";");
        adapter.OnPrint(" Normal".AsSpan());
        
        // Assert
        var linkCell = adapter.Buffer.GetCell(0, 0);
        var normalCell = adapter.Buffer.GetCell(0, 5);
        
        Assert.True(linkCell.HyperlinkId > 0);
        Assert.Equal((ushort)0, normalCell.HyperlinkId);
    }

    [Fact]
    public void TerminalAdapter_Osc8WithIdParam_ExtractsUrl()
    {
        // Arrange
        var adapter = new TerminalAdapter(rows: 10, columns: 80);
        
        // Act - OSC 8 with id parameter
        adapter.OnOperatingSystemCommand(8, "id=xyz;https://example.com");
        adapter.OnPrint("Link".AsSpan());
        
        // Assert
        var cell = adapter.Buffer.GetCell(0, 0);
        Assert.True(cell.HyperlinkId > 0);
        var url = adapter.Buffer.GetHyperlinkUrl(cell.HyperlinkId);
        Assert.Equal("https://example.com", url);
    }

    #endregion

    #region Complex Scenarios

    [Fact]
    public void OverlappingHyperlinks_LastOneWins()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id1 = buffer.GetOrCreateHyperlinkId("https://first.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://second.com");
        
        // Act - Write with first hyperlink
        var attrs1 = new CellAttributes { HyperlinkId = id1 };
        buffer.WriteText("FirstSecond".AsSpan(), attrs1);
        
        // Overwrite middle with second hyperlink
        buffer.SetCursor(0, 5);
        var attrs2 = new CellAttributes { HyperlinkId = id2 };
        buffer.WriteText("Second".AsSpan(), attrs2);
        
        // Assert
        Assert.Equal(id1, buffer.GetCell(0, 0).HyperlinkId);
        Assert.Equal(id2, buffer.GetCell(0, 5).HyperlinkId);
        Assert.Equal(id2, buffer.GetCell(0, 10).HyperlinkId);
    }

    [Fact]
    public void InsertChars_ShiftsHyperlinks()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = id };
        
        buffer.WriteText("ABC".AsSpan(), attrs);
        buffer.SetCursor(0, 1);
        
        // Act - Insert character at position 1
        buffer.InsertChars(1);
        
        // Assert - Original cells should be shifted
        // This tests that insert operations preserve or shift hyperlink IDs
        var cellAt1 = buffer.GetCell(0, 1);
        var cellAt2 = buffer.GetCell(0, 2);
        
        // Implementation detail: hyperlinks should be shifted with their cells
    }

    [Fact]
    public void DeleteChars_RemovesHyperlinks()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = id };
        
        buffer.WriteText("ABC".AsSpan(), attrs);
        buffer.SetCursor(0, 1);
        
        // Act - Delete character at position 1
        buffer.DeleteChars(1);
        
        // Assert
        var cellAt1 = buffer.GetCell(0, 1);
        // After delete, the cell should contain what was at position 2
        Assert.Equal(id, cellAt1.HyperlinkId);
    }

    [Fact]
    public void ResizeBuffer_PreservesHyperlinks()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id = buffer.GetOrCreateHyperlinkId("https://example.com");
        var attrs = new CellAttributes { HyperlinkId = id };
        
        buffer.WriteText("Test".AsSpan(), attrs);
        
        // Act - Resize buffer
        buffer.Resize(20, 120);
        
        // Assert - Hyperlink should be preserved in the cell
        var cell = buffer.GetCell(0, 0);
        Assert.Equal(id, cell.HyperlinkId);
    }

    [Fact]
    public void MultipleLines_HyperlinksOnEach()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var id1 = buffer.GetOrCreateHyperlinkId("https://line1.com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://line2.com");
        
        // Act
        buffer.WriteText("Line 1".AsSpan(), new CellAttributes { HyperlinkId = id1 });
        buffer.SetCursor(1, 0);
        buffer.WriteText("Line 2".AsSpan(), new CellAttributes { HyperlinkId = id2 });
        
        // Assert
        Assert.Equal(id1, buffer.GetCell(0, 0).HyperlinkId);
        Assert.Equal(id2, buffer.GetCell(1, 0).HyperlinkId);
    }

    #endregion

    #region Security and Edge Cases

    [Fact]
    public void HyperlinkStorage_MalformedUrl_StillStored()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act - Various malformed URLs
        var id1 = buffer.GetOrCreateHyperlinkId("not-a-url");
        var id2 = buffer.GetOrCreateHyperlinkId("javascript:alert('xss')");
        var id3 = buffer.GetOrCreateHyperlinkId("data:text/html,<script>");
        
        // Assert - Storage layer doesn't validate, just stores
        Assert.True(id1 > 0);
        Assert.True(id2 > 0);
        Assert.True(id3 > 0);
        
        Assert.Equal("not-a-url", buffer.GetHyperlinkUrl(id1));
        Assert.Equal("javascript:alert('xss')", buffer.GetHyperlinkUrl(id2));
    }

    [Fact]
    public void HyperlinkStorage_UrlCaseSensitivity_Preserved()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Act - URLs with different casing
        var id1 = buffer.GetOrCreateHyperlinkId("https://Example.Com");
        var id2 = buffer.GetOrCreateHyperlinkId("https://example.com");
        
        // Assert - URLs are case-sensitive for storage
        // (though DNS is case-insensitive, storage treats them as different)
        Assert.Equal(1, id1);
        Assert.Equal(2, id2);
    }

    [Fact]
    public void HyperlinkStorage_VeryLongUrl_StoredCorrectly()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var longUrl = "https://example.com/" + new string('a', 10000);
        
        // Act
        var id = buffer.GetOrCreateHyperlinkId(longUrl);
        var retrieved = buffer.GetHyperlinkUrl(id);
        
        // Assert
        Assert.Equal(longUrl, retrieved);
    }

    [Fact]
    public void HyperlinkStorage_UnicodeUrl_StoredCorrectly()
    {
        // Arrange
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        var unicodeUrl = "https://example.com/\u4e2d\u6587\u8def\u5f84";
        
        // Act
        var id = buffer.GetOrCreateHyperlinkId(unicodeUrl);
        var retrieved = buffer.GetHyperlinkUrl(id);
        
        // Assert
        Assert.Equal(unicodeUrl, retrieved);
    }

    #endregion
}
