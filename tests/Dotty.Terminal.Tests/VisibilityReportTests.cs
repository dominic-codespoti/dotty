using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>Mode 2033 visibility reports and the CSI ? 998 n query (reply: CSI ? 999 ; Ps n).</summary>
public sealed class VisibilityReportTests
{
    private const string Visible = "\u001b[?999;1n";
    private const string Hidden = "\u001b[?999;2n";

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => { lock (replies) replies.Add(reply.ToString()); };
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void PrivateDsr998_EndToEnd_RepliesWithVisibilityRegardlessOfMode()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?998n");
        adapter.SetVisibility(false);
        Feed(parser, "\u001b[?998n");
        Assert.Equal(new[] { Visible, Hidden }, replies);
        Assert.False(adapter.VisibilityReportsEnabled);
    }


    [Fact]
    public void Defaults_AreVisibleAndDisabled()
    {
        var (adapter, _, replies) = Setup();
        Assert.True(adapter.IsVisible);
        Assert.False(adapter.VisibilityReportsEnabled);
        Assert.Empty(replies);
    }

    [Fact]
    public void Enable_ReportsImmediatelyAndOnEveryRepeat()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?2033h");
        Assert.True(adapter.VisibilityReportsEnabled);
        Feed(parser, "\u001b[?2033h");
        Assert.Equal(new[] { Visible, Visible }, replies);
    }

    [Fact]
    public void Enable_WhileHiddenReportsHidden()
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetVisibility(false);
        Feed(parser, "\u001b[?2033h");
        Assert.Equal(new[] { Hidden }, replies);
    }

    [Fact]
    public void Disable_NeverReportsAndStopsUnsolicitedReports()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?2033l");
        adapter.SetVisibility(false);
        adapter.SetVisibility(true);
        Assert.False(adapter.VisibilityReportsEnabled);
        Assert.Empty(replies);
    }

    [Fact]
    public void Edges_EmitOnlyWhenEnabledAndChanged()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?2033h");
        replies.Clear();

        adapter.SetVisibility(false);
        adapter.SetVisibility(false);
        adapter.SetVisibility(true);
        adapter.SetVisibility(true);

        Assert.Equal(new[] { Hidden, Visible }, replies);
        Assert.True(adapter.IsVisible);
    }

    [Fact]
    public void Edges_WhileDisabledAreRememberedForTheNextEnable()
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetVisibility(false);
        Assert.False(adapter.IsVisible);
        Assert.Empty(replies);

        adapter.SetVisibility(true);
        adapter.SetVisibility(false);
        Feed(parser, "\u001b[?2033h");
        Assert.Equal(new[] { Hidden }, replies);
    }

    [Fact]
    public void Query_AnswersRegardlessOfModeAndKeepsItUnchanged()
    {
        var (adapter, _, replies) = Setup();
        adapter.HandleVisibilityQuery();
        adapter.SetVisibility(false);
        adapter.HandleVisibilityQuery();
        Assert.Equal(new[] { Visible, Hidden }, replies);
        Assert.False(adapter.VisibilityReportsEnabled);
    }

    [Fact]
    public void Query_DoesNotDisableAnEnabledMode()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?2033h");
        adapter.HandleVisibilityQuery();
        Assert.True(adapter.VisibilityReportsEnabled);
        Assert.Equal(new[] { Visible, Visible }, replies);
    }

    [Fact]
    public void FullReset_DisablesTheModeButNotTheHostFact()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?2033h");
        adapter.SetVisibility(false);
        replies.Clear();

        Feed(parser, "\u001bc");

        Assert.False(adapter.VisibilityReportsEnabled);
        Assert.False(adapter.IsVisible);
        adapter.SetVisibility(true);
        Assert.Empty(replies);
    }

    [Fact]
    public void NoSubscriber_DoesNotThrow()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        Feed(parser, "\u001b[?2033h");
        adapter.SetVisibility(false);
        adapter.HandleVisibilityQuery();
        Assert.True(adapter.VisibilityReportsEnabled);
    }

    [Fact]
    public async Task ConcurrentEdges_NeverLoseOrDuplicateAReport()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?2033h");
        replies.Clear();

        // Host thread flips visibility 1000 times while another thread keeps re-enabling the
        // mode (each enable reports the current state under the same lock).
        var flipper = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
                adapter.SetVisibility(i % 2 == 0);
        }, TestContext.Current.CancellationToken);
        var enabler = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
                adapter.OnSetVisibilityReports(true);
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(flipper, enabler);

        // i = 0 sets "visible" on a visible adapter (no edge); the other 999 calls are edges,
        // and every enable adds exactly one report.
        Assert.Equal(999 + 1000, replies.Count);
        Assert.All(replies, r => Assert.True(r == Visible || r == Hidden));
        Assert.False(adapter.IsVisible);
        adapter.HandleVisibilityQuery();
        Assert.Equal(Hidden, replies[^1]);
    }

    [Fact]
    public async Task ConcurrentEdgesAlone_AlternateStrictly()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?2033h");
        replies.Clear();

        await Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
                adapter.SetVisibility(i % 2 == 0);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(999, replies.Count);
        for (int i = 0; i < replies.Count; i++)
            Assert.Equal(i % 2 == 0 ? Hidden : Visible, replies[i]);
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class VisibilityAllocationTests
{
    private static void IgnoreReply(ReadOnlySpan<char> reply) { }

    [Fact]
    public void ModeToggleQueryAndEdgesDoNotAllocate()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        adapter.ReplyRequested += IgnoreReply;
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[] enable = Encoding.UTF8.GetBytes("\u001b[?2033h");
        byte[] disable = Encoding.UTF8.GetBytes("\u001b[?2033l");

        void Round()
        {
            parser.Feed(enable);
            adapter.SetVisibility(false);
            adapter.SetVisibility(true);
            adapter.HandleVisibilityQuery();
            parser.Feed(disable);
        }

        for (int i = 0; i < 16; i++) Round();
        AllocationAssert.NoAllocations(Round, warmupIterations: 0, measuredIterationsPerWindow: 128, windows: 5);
    }
}
