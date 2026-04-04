using System;
using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class SearchColoredTextTest
{
    [Fact]
    public void Search_CanFindColoredText()
    {
        // Create buffer like real terminal with colored text
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Write text with colors (like ls output)
        var blueAttrs = new CellAttributes { Foreground = new SgrColorArgb(0xFF0000FF) }; // Blue color (ARGB)
        var defaultAttrs = CellAttributes.Default;
        
        buffer.SetCursor(0, 0);
        buffer.WriteText("Desktop    ".AsSpan(), defaultAttrs);
        buffer.WriteText("Notes".AsSpan(), blueAttrs);  // Blue "Notes"
        buffer.WriteText("    Templates".AsSpan(), defaultAttrs);
        
        // Search for "Notes"
        var search = new TerminalSearch(buffer);
        int count = search.Search("Notes", caseSensitive: false, useRegex: false);
        
        Console.WriteLine($"Found {count} matches for 'Notes' (colored text)");
        
        // Debug: Print what's actually in the buffer row 0
        var lineText = GetLineText(buffer, 0);
        Console.WriteLine($"Row 0 content: '{lineText}'");
        
        // Check individual cells
        for (int col = 0; col < 20; col++)
        {
            var cell = buffer.GetCell(0, col);
            if (!cell.IsEmpty)
            {
                Console.WriteLine($"Cell[0,{col}]: Grapheme='{cell.Grapheme}', Rune={cell.Rune}, Foreground={cell.Foreground:X6}");
            }
        }
        
        // Should find "Notes"
        Assert.True(count > 0, $"Should find 'Notes' even with different colors, but found {count} matches");
    }
    
    private string GetLineText(TerminalBuffer buffer, int row)
    {
        System.Text.StringBuilder sb = new();
        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            if (!cell.IsEmpty && !cell.IsContinuation)
            {
                sb.Append(cell.Grapheme ?? " ");
            }
            else
            {
                sb.Append(' ');
            }
        }
        return sb.ToString().TrimEnd();
    }
}
