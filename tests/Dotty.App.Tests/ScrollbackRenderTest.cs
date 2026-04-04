using System;
using System.IO;
using Xunit;
using Dotty.Terminal.Adapter;
using Dotty.App.Controls.Canvas.Rendering;

namespace Dotty.Tests.ScrollbackDiagnostics;

/// <summary>
/// Test harness to diagnose scrollback blank space issue.
/// Creates a mock terminal with scrollback and verifies rendering.
/// </summary>
public class ScrollbackRenderTest
{
    [Fact]
    public void Scrollback_With500kLines_ShouldHaveContent()
    {
        // Create a terminal buffer with default settings
        var buffer = new TerminalBuffer(30, 80); // 30 rows, 80 columns
        buffer.MaxScrollback = 10000; // Match production setting
        
        // First, fill the entire visible screen to trigger scrolling behavior
        // This ensures subsequent LineFeed calls will push lines into scrollback
        Console.WriteLine("Filling visible screen (30 rows)...");
        for (int row = 0; row < buffer.Rows; row++)
        {
            var setupLine = $"Setup line {row}";
            buffer.WriteText(setupLine.AsSpan(), (string?)null);
            if (row < buffer.Rows - 1)
            {
                buffer.CarriageReturn();
                buffer.LineFeed();
            }
        }
        // Cursor is now at bottom row (row 29), next CR+LF will scroll
        Console.WriteLine($"After setup: Scrollback count = {buffer.ScrollbackCount}");
        
        // Now simulate generating 500k lines (like: yes "test" | head -n 500000)
        Console.WriteLine("Generating 500,000 lines of output...");
        for (int i = 0; i < 500000; i++)
        {
            // Write a line to the terminal - this will now push into scrollback
            var lineText = $"Line {i} - The quick brown fox jumps over the lazy dog";
            buffer.CarriageReturn();
            buffer.WriteText(lineText.AsSpan(), (string?)null);
            buffer.CarriageReturn();
            buffer.LineFeed();
            
            // Debug: Check scrollback every 100k lines
            if (i > 0 && i % 100000 == 0)
            {
                Console.WriteLine($"  After {i} lines: Scrollback count = {buffer.ScrollbackCount}");
            }
        }
        
        Console.WriteLine($"Final scrollback count: {buffer.ScrollbackCount}");
        
        Console.WriteLine($"Scrollback count: {buffer.ScrollbackCount}");
        Console.WriteLine($"Max scrollback: {buffer.MaxScrollback}");
        
        // Check content distribution
        int contentLines = 0;
        int emptyLines = 0;
        int firstContent = -1;
        int lastContent = -1;
        
        for (int i = 0; i < buffer.ScrollbackCount; i++)
        {
            var line = buffer.GetScrollbackLine(i);
            if (line.Length > 0)
            {
                contentLines++;
                if (firstContent == -1) firstContent = i;
                lastContent = i;
            }
            else
            {
                emptyLines++;
            }
        }
        
        Console.WriteLine($"Content lines: {contentLines}");
        Console.WriteLine($"Empty lines: {emptyLines}");
        Console.WriteLine($"First content at: {firstContent}");
        Console.WriteLine($"Last content at: {lastContent}");
        
        // Show sample of what's in scrollback
        Console.WriteLine("\nSample scrollback content:");
        for (int i = 0; i < buffer.ScrollbackCount; i += 1000)
        {
            var line = buffer.GetScrollbackLine(i);
            Console.WriteLine($"  [{i}] Length={line.Length}: \"{line.ToString().Substring(0, Math.Min(50, line.Length))}\"");
        }
        
        // The test: we should have content in most of the scrollback
        // Scrollback is now working correctly with all lines having content
        Assert.True(contentLines > 9000, $"Expected >9000 content lines, got {contentLines}");
        Assert.Equal(0, emptyLines);
        
        // Additional check: verify the scrollback contains recent lines
        var lastLine = buffer.GetScrollbackLine(buffer.ScrollbackCount - 1);
        Assert.Contains("Line 499970", lastLine.ToString());
    }
    
    [Fact]
    public void Scrollback_RingBufferWraparound_Trace()
    {
        // Create buffer
        var buffer = new TerminalBuffer(30, 80);
        buffer.MaxScrollback = 100; // Small for testing
        
        // First, fill the entire visible screen to trigger scrolling behavior
        // Use CR+LF to properly position cursor at start of each new line
        Console.WriteLine("Filling visible screen (30 rows)...");
        for (int row = 0; row < buffer.Rows; row++)
        {
            var setupLine = $"Setup {row}";
            buffer.WriteText(setupLine.AsSpan(), (string?)null);
            if (row < buffer.Rows - 1)
            {
                buffer.CarriageReturn();
                buffer.LineFeed();
            }
        }
        // Cursor is now at bottom row (row 29), next CR+LF will scroll
        
        // Now fill scrollback completely (150 > 100, so it wraps)
        for (int i = 0; i < 150; i++) // 150 > 100, so it wraps
        {
            buffer.CarriageReturn();
            buffer.WriteText($"Line {i}".AsSpan(), (string?)null);
            buffer.CarriageReturn();
            buffer.LineFeed();
        }
        
        Console.WriteLine($"After 150 lines:");
        Console.WriteLine($"  ScrollbackCount: {buffer.ScrollbackCount}");
        Console.WriteLine($"  MaxScrollback: {buffer.MaxScrollback}");
        
        // Dump all scrollback lines
        for (int i = 0; i < buffer.ScrollbackCount; i++)
        {
            var line = buffer.GetScrollbackLine(i);
            Console.WriteLine($"  [{i}] Length={line.Length}: \"{line}\"");
        }
        
        // Check first and last
        var first = buffer.GetScrollbackLine(0);
        var last = buffer.GetScrollbackLine(buffer.ScrollbackCount - 1);
        
        Console.WriteLine($"\nFirst (idx 0): \"{first}\"");
        Console.WriteLine($"Last (idx {buffer.ScrollbackCount - 1}): \"{last}\"");
        
        // With 30 screen rows filled first, then 150 lines:
        // - First 30 lines fill screen (Setup 0-29), no scrollback
        // - Lines 30-149 create scrollback entries (120 lines of output)
        // - With 100 max scrollback, we keep the most recent 100
        // - So scrollback contains the last 100 lines that were scrolled
        // - First in scrollback = Line 21 (21st TestLine written)
        // - Last in scrollback = Line 120 (120th TestLine written)
        Assert.Contains("Line 21", first.ToString());
        Assert.Contains("Line 120", last.ToString());
    }
}
