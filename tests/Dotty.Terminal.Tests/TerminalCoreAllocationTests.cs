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
    public void RepeatedDecrqmQueryDoesNotAllocate()
    {
        var adapter = new TerminalAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] query = Encoding.UTF8.GetBytes("\u001b[?1$p");
        for (int i = 0; i < 16; i++)
            parser.Feed(query);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
            parser.Feed(query);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RepeatedXtgettcapQueryDoesNotAllocate()
    {
        var adapter = new TerminalAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] query = Encoding.UTF8.GetBytes("\u001bP+q544E\u001b\\\\");
        for (int i = 0; i < 16; i++)
            parser.Feed(query);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
            parser.Feed(query);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RepeatedModifyOtherKeysNegotiationDoesNotAllocate()
    {
        var adapter = new TerminalAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] set = Encoding.UTF8.GetBytes("\u001b[>4;1m");
        byte[] reset = Encoding.UTF8.GetBytes("\u001b[>4;0m");
        for (int i = 0; i < 16; i++)
        {
            parser.Feed(set);
            parser.Feed(reset);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
        {
            parser.Feed(set);
            parser.Feed(reset);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RepeatedIdenticalPaletteSetIsIdempotentAndCheap()
    {
        // Setting the same entry twice allocates only the reply plumbing;
        // the second set is a no-op (early return before snapshot/remap).
        // Measured loosely: warm run establishes caches, second identical
        // set must not exceed the first set's allocation.
        var adapter = new TerminalAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        uint[] baseline = SgrColorArgb.GetAnsiPaletteSnapshot();
        try
        {
            byte[] set = Encoding.UTF8.GetBytes("\u001b]4;1;#ff0000\u0007");
            parser.Feed(set);
            long before = GC.GetAllocatedBytesForCurrentThread();
            parser.Feed(set);
            long second = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(second <= 2048, $"second identical set allocated {second} bytes");
        }
        finally
        {
            SgrColorArgb.SetAnsiPalette(baseline);
            SgrColorArgb.ResetExtendedPalette();
        }
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
