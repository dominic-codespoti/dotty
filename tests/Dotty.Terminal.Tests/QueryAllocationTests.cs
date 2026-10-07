using System;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>Allocation guards for DECRQM, XTGETTCAP and modifyOtherKeys.</summary>
public sealed class QueryAllocationTests
{
    private static readonly TerminalReplyHandler IgnoreReplyHandler = IgnoreReply;
    private static void IgnoreReply(ReadOnlySpan<char> reply) { }

    private static BasicAnsiParser CreateParser()
    {
        var adapter = new TerminalAdapter();
        adapter.ReplyRequested += IgnoreReplyHandler;
        return new BasicAnsiParser { Handler = adapter };
    }

    private static void AssertNoAllocations(BasicAnsiParser parser, params byte[][] sequences)
    {
        for (int i = 0; i < 16; i++)
            foreach (byte[] sequence in sequences) parser.Feed(sequence);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
            foreach (byte[] sequence in sequences) parser.Feed(sequence);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RepeatedDecrqmQueryDoesNotAllocate() =>
        AssertNoAllocations(CreateParser(), Encoding.UTF8.GetBytes("\u001b[?1$p"));

    [Fact]
    public void RepeatedXtgettcapSingleNameQueryDoesNotAllocate() =>
        AssertNoAllocations(CreateParser(), Encoding.UTF8.GetBytes("\u001bP+q544E\u001b\\"));

    [Fact]
    public void RepeatedXtgettcapMultipleNameQueryDoesNotAllocate() =>
        AssertNoAllocations(CreateParser(), Encoding.UTF8.GetBytes("\u001bP+q544E;436F\u001b\\"));

    [Fact]
    public void RepeatedModifyOtherKeysSetResetDoesNotAllocate() =>
        AssertNoAllocations(CreateParser(), Encoding.UTF8.GetBytes("\u001b[>4;1m"), Encoding.UTF8.GetBytes("\u001b[>4;0m"), Encoding.UTF8.GetBytes("\u001b[>4m"));
}
