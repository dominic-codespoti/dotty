using System;
using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.Tests.ScrollbackDiagnostics;

public class ScrollbackMinimalTest
{
    [Fact]
    public void Scrollback_AddSingleLine_ShouldStoreContent()
    {
        // Create a minimal buffer: 5 rows, 10 columns
        var buffer = new TerminalBuffer(5, 10);
        buffer.MaxScrollback = 10;
        
        Console.WriteLine("=== Test: Single Line to Scrollback ===");
        Console.WriteLine($"Buffer: {buffer.Rows} rows x {buffer.Columns} cols");
        Console.WriteLine($"MaxScrollback: {buffer.MaxScrollback}");
        Console.WriteLine();
        
        // Step 1: Fill the buffer to the bottom
        Console.WriteLine("Step 1: Filling buffer rows 0-4...");
        for (int i = 0; i < buffer.Rows; i++)
        {
            buffer.WriteText($"Row{i}".AsSpan(), null);
            if (i < buffer.Rows - 1)
            {
                buffer.CarriageReturn();
                buffer.LineFeed();
            }
        }
        Console.WriteLine($"Cursor at row {buffer.CursorRow} (should be 4)");
        Console.WriteLine($"Scrollback count: {buffer.ScrollbackCount} (should be 0)");
        Console.WriteLine();
        
        // Step 2: Write one more line - this should trigger scrollback
        Console.WriteLine("Step 2: Writing 'LINE1' and CR+LF...");
        buffer.CarriageReturn();
        buffer.WriteText("LINE1".AsSpan(), null);
        buffer.CarriageReturn();
        buffer.LineFeed();
        
        Console.WriteLine($"Scrollback count: {buffer.ScrollbackCount} (should be 1)");
        
        // Dump raw scrollback contents for debugging
        Console.WriteLine("Raw scrollback dump:");
        for (int i = 0; i < buffer.ScrollbackCount; i++)
        {
            var line = buffer.GetScrollbackLine(i);
            Console.WriteLine($"  [{i}] Length={line.Length}, Buf={(line.Buffer == null ? "null" : $"len={line.Buffer.Length}")}");
            if (line.Length > 0 && line.Buffer != null)
            {
                Console.WriteLine($"       Content: '{new string(line.Buffer, 0, line.Length)}'");
            }
        }
        
        if (buffer.ScrollbackCount > 0)
        {
            var line = buffer.GetScrollbackLine(0);
            Console.WriteLine($"Scrollback[0]: Length={line.Length}, Content='{line}'");
            Assert.True(line.Length > 0, "Scrollback line should have content");
            Assert.Contains("Row0", line.ToString());
        }
        else
        {
            Assert.Fail("No scrollback entries created!");
        }
    }
    
    [Fact]
    public void Scrollback_AddMultipleLines_TraceIndices()
    {
        var buffer = new TerminalBuffer(5, 20);
        buffer.MaxScrollback = 10;
        
        Console.WriteLine("\n=== Test: Multiple Lines to Scrollback ===");
        
        // Fill buffer first - use CR+LF to properly position cursor
        for (int i = 0; i < buffer.Rows; i++)
        {
            buffer.WriteText($"Initial{i}".AsSpan(), null);
            if (i < buffer.Rows - 1) 
            {
                buffer.CarriageReturn();
                buffer.LineFeed();
            }
        }
        
        // Now add 15 lines, exceeding scrollback capacity
        // Use CR+LF to properly position cursor at start of each new line
        for (int i = 0; i < 15; i++)
        {
            buffer.CarriageReturn();
            buffer.WriteText($"TestLine{i:00}".AsSpan(), null);
            buffer.CarriageReturn();
            buffer.LineFeed();
            
            Console.WriteLine($"After line {i}: scrollback={buffer.ScrollbackCount}");
        }
        
        Console.WriteLine("\nFinal scrollback contents:");
        for (int i = 0; i < buffer.ScrollbackCount; i++)
        {
            var line = buffer.GetScrollbackLine(i);
            Console.WriteLine($"  [{i}] len={line.Length}: '{line}'");
        }
        
        // With 15 lines and capacity 10, we should have 10 lines
        // The scrollback captures what scrolls off the top of the screen
        // First visible TestLine scrolls into scrollback at position 0
        // So we get TestLine01 through TestLine10 (first 10 TestLines that scrolled)
        Assert.Equal(10, buffer.ScrollbackCount);
        
        var first = buffer.GetScrollbackLine(0);
        var last = buffer.GetScrollbackLine(9);
        
        Console.WriteLine($"\nFirst: '{first}'");
        Console.WriteLine($"Last: '{last}'");
        
        Assert.Contains("TestLine01", first.ToString());
        Assert.Contains("TestLine10", last.ToString());
    }
}
