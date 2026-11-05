using System;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>Allocation guards for the OSC fast paths (bulk accumulation, bytes-native OSC 52).</summary>
[Collection("Allocation-sensitive tests")]
public sealed class OscAllocationTests
{
    [Fact]
    public void Osc52Write_64KiB_AllocatesOnlyTheDecodedString()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        int received = 0;
        adapter.ClipboardWriteRequested += w => received = w.Text.Length;

        byte[] raw = new byte[48 * 1024];
        Array.Fill(raw, (byte)'a');
        byte[] stream = Encoding.ASCII.GetBytes("\u001b]52;c;" + Convert.ToBase64String(raw) + "\u0007");
        Assert.True(stream.Length > 64 * 1024);

        for (int i = 0; i < 8; i++)
            parser.Feed(stream);
        Assert.Equal(raw.Length, received);

        long best = long.MaxValue;
        for (int w = 0; w < 5; w++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            parser.Feed(stream);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // One string of raw.Length chars (2 bytes each) plus header, nothing else (a stray 48 KiB buffer would show).
        Assert.True(best <= raw.Length * 2L + 1024, $"allocated {best} bytes");
    }

    [Fact]
    public void Osc52ReadQuery_DoesNotAllocate()
    {
        var adapter = new TerminalAdapter(24, 80);
        adapter.ClipboardReadRequested += static _ => { };
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] stream = Encoding.ASCII.GetBytes("\u001b]52;c;?\u0007");

        AllocationAssert.NoAllocations(
            () => parser.Feed(stream),
            warmupIterations: 16,
            measuredIterationsPerWindow: 128,
            windows: 5);
    }

    [Fact]
    public void DiscardedOverflowingOsc_DoesNotAllocateAfterWarmup()
    {
        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] stream = new byte[1024 * 1024 + 4096];
        stream[0] = 0x1b;
        stream[1] = (byte)']';
        stream[2] = (byte)'2';
        stream[3] = (byte)';';
        Array.Fill(stream, (byte)'a', 4, stream.Length - 5);
        stream[^1] = 0x07;

        AllocationAssert.NoAllocations(
            () => parser.Feed(stream),
            warmupIterations: 4,
            measuredIterationsPerWindow: 4,
            windows: 3);
    }
}
