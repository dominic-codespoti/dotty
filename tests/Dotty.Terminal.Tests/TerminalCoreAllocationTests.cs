using System;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class TerminalCoreAllocationTests
{
    [Fact]
    public void AdapterOutputAllocatesNothingAfterScrollbackAndStyleWarmup()
    {
        var adapter = new TerminalAdapter(rows: 8, columns: 80, scrollbackCapacity: 32);
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] line = Encoding.ASCII.GetBytes(
            "\u001b[?25h\u001b]8;;https://example.test\u0007\u001b[31mallocation-free terminal line\u001b[0m\u001b]8;;\u0007\r\n");

        for (int i = 0; i < 128; i++)
            parser.Feed(line);

        Assert.Equal(32, adapter.Buffer.ScrollbackCount);

        for (int i = 0; i < 32; i++)
            parser.Feed(line);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
            parser.Feed(line);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void VariableResizeSequenceAllocatesNothingAfterWarmup()
    {
        var primary = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 256);
        var secondary = new TerminalBuffer(rows: 12, columns: 40, scrollbackCapacity: 256);
        string wrappedLine = new string('x', 120);
        FillWrappedScrollback(primary, wrappedLine);
        FillWrappedScrollback(secondary, wrappedLine);
        Assert.Equal(256, primary.ScrollbackCount);
        Assert.Equal(256, secondary.ScrollbackCount);
        primary.SetAlternateScreen(true);
        primary.WriteText(wrappedLine.AsSpan(), CellAttributes.Default);

        (int Rows, int Columns)[] warmSizes =
        {
            (30, 100),
            (24, 80),
        };

        (int Rows, int Columns)[] measuredSizes =
        {
            (30, 100),
            (24, 80),
        };

        for (int i = 0; i < 8; i++)
        {
            foreach (var size in warmSizes)
                ResizeBoth(primary, secondary, size.Rows, size.Columns);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            foreach (var size in measuredSizes)
                ResizeBoth(primary, secondary, size.Rows, size.Columns);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        primary.SetAlternateScreen(false);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void VisibleSnapshotCaptureAndDisposeAllocateNothingAfterWarmup()
    {
        var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 64);
        for (int i = 0; i < 8; i++)
        {
            using var snapshot = buffer.CaptureRenderSnapshotVisible();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
        {
            using var snapshot = buffer.CaptureRenderSnapshotVisible();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RepeatedIdenticalPaletteSetIsIdempotentAndCheap()
    {
        // Warm parser paths first; repeated identical updates and queries must not allocate.
        var adapter = new TerminalAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] set = Encoding.UTF8.GetBytes("\u001b]4;1;#ff0000\u0007");
        parser.Feed(set);
        long before = GC.GetAllocatedBytesForCurrentThread();
        parser.Feed(set);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
    [Fact]
    public void RepeatedPaletteQueriesAndNoOpResetsAllocateNothingAfterWarmup()
    {
        var adapter = new TerminalAdapter();
        adapter.ReplyRequested += static _ => { };
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] setDefault = Encoding.UTF8.GetBytes("]10;#010203");
        parser.Feed(setDefault);
        long before = GC.GetAllocatedBytesForCurrentThread();
        parser.Feed(setDefault);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        byte[] setPalette = Encoding.UTF8.GetBytes("]4;1;#ff0000");
        parser.Feed(setPalette);
        byte[] query = Encoding.UTF8.GetBytes("]4;1;?");
        parser.Feed(query);
        before = GC.GetAllocatedBytesForCurrentThread();
        parser.Feed(query);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        byte[] reset = Encoding.UTF8.GetBytes("]104");
        parser.Feed(reset);
        parser.Feed(reset);
        before = GC.GetAllocatedBytesForCurrentThread();
        parser.Feed(reset);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void FillWrappedScrollback(TerminalBuffer buffer, string line)
    {
        for (int i = 0; i < 160; i++)
        {
            buffer.WriteText(line.AsSpan(), CellAttributes.Default);
            buffer.CarriageReturn();
            buffer.LineFeed();
        }
    }

    private static void ResizeBoth(TerminalBuffer primary, TerminalBuffer secondary, int rows, int columns)
    {
        primary.Resize(rows, columns);
        secondary.Resize(rows, columns);
    }

}
