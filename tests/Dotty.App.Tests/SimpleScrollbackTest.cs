using System;
using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.Tests.ScrollbackDiagnostics;

public class SimpleScrollbackTest
{
    [Fact]
    public void SimpleScrollback_BasicWriteAndCapture()
    {
        // Create a small terminal buffer
        var buffer = new TerminalBuffer(5, 20); // 5 rows, 20 cols
        buffer.MaxScrollback = 10;
        
        // Fill visible screen
        Console.WriteLine("Filling visible screen...");
        for (int row = 0; row < 5; row++)
        {
            buffer.WriteText($"Row{row}".AsSpan(), (string?)null);
            if (row < 4) buffer.LineFeed();
        }
        
        // Check what's in row 0
        var row0Line = buffer.GetRowText(0);
        Console.WriteLine($"After setup - Row 0 content: '{row0Line}'");
        
        // Write one more line (should push row 0 to scrollback)
        Console.WriteLine("\nWriting one more line...");
        buffer.WriteText("NewLine".AsSpan(), (string?)null);
        buffer.LineFeed();
        
        // Check scrollback
        Console.WriteLine($"Scrollback count: {buffer.ScrollbackCount}");
        if (buffer.ScrollbackCount > 0)
        {
            var sbLine = buffer.GetScrollbackLine(0);
            Console.WriteLine($"Scrollback[0]: Length={sbLine.Length}, Content='{sbLine}'");
        }
        
        // Verify
        Assert.True(buffer.ScrollbackCount > 0, "Should have scrollback");
        var firstSb = buffer.GetScrollbackLine(0);
        Assert.True(firstSb.Length > 0, "Scrollback should have content");
        Assert.Contains("Row", firstSb.ToString());
    }
}
