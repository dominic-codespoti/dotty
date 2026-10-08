using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection("Allocation-sensitive tests")]
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

        AllocationAssert.NoAllocations(() => parser.Feed(line), warmupIterations: 0, measuredIterationsPerWindow: 256, windows: 5);
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

        AllocationAssert.NoAllocations(
            () =>
            {
                foreach (var size in measuredSizes)
                    ResizeBoth(primary, secondary, size.Rows, size.Columns);
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 64,
            windows: 5,
            beforeEachWindow: () => primary.SetAlternateScreen(true));
        primary.SetAlternateScreen(false);
    }

    [Fact]
    public void VisibleSnapshotCaptureAndDisposeAllocateNothingAfterWarmup()
    {
        var buffer = new TerminalBuffer(rows: 24, columns: 80, scrollbackCapacity: 64);
        for (int i = 0; i < 8; i++)
        {
            using var snapshot = buffer.CaptureRenderSnapshotVisible();
        }

        AllocationAssert.NoAllocations(
            () =>
            {
                using var snapshot = buffer.CaptureRenderSnapshotVisible();
            },
            warmupIterations: 8,
            measuredIterationsPerWindow: 128,
            windows: 5);
    }

    [Fact]
    public void RepeatedIdenticalPaletteSetIsIdempotentAndCheap()
    {
        // Warm parser paths first; repeated identical updates and queries must not allocate.
        var adapter = new TerminalAdapter();
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] set = Encoding.UTF8.GetBytes("\u001b]4;1;#ff0000\u0007");
        AllocationAssert.NoAllocations(() => parser.Feed(set), warmupIterations: 1, measuredIterationsPerWindow: 1, windows: 5);
    }
    [Fact]
    public void RepeatedPaletteQueriesAndNoOpResetsAllocateNothingAfterWarmup()
    {
        var adapter = new TerminalAdapter();
        adapter.ReplyRequested += static _ => { };
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] setDefault = Encoding.UTF8.GetBytes("\u001b]10;#010203\u0007");
        AllocationAssert.NoAllocations(() => parser.Feed(setDefault), warmupIterations: 1, measuredIterationsPerWindow: 1, windows: 5);

        byte[] setPalette = Encoding.UTF8.GetBytes("\u001b]4;1;#ff0000\u0007");
        byte[] query = Encoding.UTF8.GetBytes("\u001b]4;1;?\u0007");
        AllocationAssert.NoAllocations(
            () => parser.Feed(query),
            warmupIterations: 1,
            measuredIterationsPerWindow: 1,
            windows: 5,
            beforeEachWindow: () => parser.Feed(setPalette));

        byte[] reset = Encoding.UTF8.GetBytes("\u001b]104\u0007");
        AllocationAssert.NoAllocations(
            () => parser.Feed(reset),
            warmupIterations: 1,
            measuredIterationsPerWindow: 1,
            windows: 5,
            beforeEachWindow: () => parser.Feed(reset));
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
