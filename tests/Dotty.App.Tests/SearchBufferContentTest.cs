using System;
using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class SearchBufferContentTest
{
    [Fact]
    public void Search_CanFindText_FromRealBufferOutput()
    {
        // Create buffer like real terminal
        var buffer = new TerminalBuffer(rows: 10, columns: 80);
        
        // Write actual text that would appear in terminal
        buffer.SetCursor(0, 0);
        buffer.WriteText("Desktop    Notes    Templates".AsSpan(), CellAttributes.Default);
        
        buffer.SetCursor(1, 0);
        buffer.WriteText("Documents  Pictures  Unity".AsSpan(), CellAttributes.Default);
        
        buffer.SetCursor(2, 0);
        buffer.WriteText("Downloads  Postman   Videos".AsSpan(), CellAttributes.Default);
        
        // Search for "Notes"
        var search = new TerminalSearch(buffer);
        int count = search.Search("Notes", caseSensitive: false, useRegex: false);
        
        Console.WriteLine($"Found {count} matches for 'Notes'");
        
        // Debug: Print what's actually in the buffer
        for (int row = 0; row < 3; row++)
        {
            var lineText = GetLineText(buffer, row);
            Console.WriteLine($"Row {row}: '{lineText}'");
        }
        
        // Should find "Notes"
        Assert.True(count > 0, $"Should find 'Notes' in buffer but found {count} matches");
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
