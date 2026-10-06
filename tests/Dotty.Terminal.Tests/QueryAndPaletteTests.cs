using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// P0 query compliance (DECRQM/XTGETTCAP/modifyOtherKeys) and P1 OSC palette
/// (OSC 4 set/query, OSC 10/11 set/query) reply behavior.
/// </summary>
public sealed class QueryAndPaletteTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) =>
        parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Decrqm_ReportsSetResetAndUnrecognized()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[?1h"); // DECCKM set
        Feed(parser, "\u001b[?1$p");
        Feed(parser, "\u001b[?6$p"); // DECOM reset
        Feed(parser, "\u001b[?2004$p"); // bracketed paste reset
        Feed(parser, "\u001b[?9999$p"); // unrecognized
        Feed(parser, "\u001b[7$p"); // DECAWM ANSI form, default set

        Assert.Equal(new[]
        {
            "\u001b[?1;1$y",
            "\u001b[?6;2$y",
            "\u001b[?2004;2$y",
            "\u001b[?9999;0$y",
            "\u001b[7;1$y",
        }, replies);
        Assert.True(adapter.Buffer.AutoWrap);
    }

    [Fact]
    public void Xtgettcap_AnswersKnownKeysAndIgnoresUnknown()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001bP+q544E\u001b\\"); // TN
        Feed(parser, "\u001bP+q1234\u001b\\"); // unknown key: silent

        Assert.Single(replies);
        Assert.Equal("\u001bP1$r544E=646F747479\u001b\\", replies[0]);
    }

    [Fact]
    public void ModifyOtherKeys_NegotiatesAndResetsOnRis()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, "\u001b[>4;1m");
        Assert.Equal(1, adapter.ModifyOtherKeysLevel);
        Feed(parser, "\u001b[>4;0m");
        Assert.Equal(0, adapter.ModifyOtherKeysLevel);
        Feed(parser, "\u001b[>4;2m");
        Feed(parser, "\u001bc"); // RIS resets the level
        Assert.Equal(0, adapter.ModifyOtherKeysLevel);
    }

    [Fact]
    public void Osc4_SetAndQuery_RoundTripsLiveEntry()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b]4;1;#ff0000\u0007");
        Feed(parser, "\u001b]4;1;?\u0007");

        Assert.Equal("\u001b]4;#FF0000\u0007", replies[^1]);
    }

    [Fact]
    public void Osc4_ExtendedIndex_SetAndQuery()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b]4;200;rgb:ff/00/00\u0007");
        Feed(parser, "\u001b]4;200;?\u0007");

        Assert.Equal("\u001b]4;#FF0000\u0007", replies[^1]);
        SgrColorArgb.ResetExtendedPalette();
    }

    [Fact]
    public void Osc4_MalformedSpec_SkipsEntryWithoutReply()
    {
        var (_, parser, replies) = Setup();
        int before = replies.Count;
        Feed(parser, "\u001b]4;2;notacolor\u0007");

        Assert.Equal(before, replies.Count);
    }

    [Fact]
    public void Osc10_SetAndQuery_RoundTrips()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b]10;#112233\u0007");
        Assert.Equal("\u001b]10;#112233\u0007", replies[^1]);
        Feed(parser, "\u001b]10;?\u0007");
        Assert.Equal("\u001b]10;#112233\u0007", replies[^1]);
    }

    [Fact]
    public void Osc11_DefaultQuery_ReportsConfiguredBackground()
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetDefaultColors("#AABBCC", "#112233");
        Feed(parser, "\u001b]11;?\u0007");

        Assert.Equal("\u001b]11;#112233\u0007", replies[^1]);
    }

    [Fact]
    public void Osc4_Set_RemapsRenderedAnsiStyle()
    {
        var (adapter, parser, _) = Setup();
        uint[] baseline = SgrColorArgb.GetAnsiPaletteSnapshot();
        try
        {
            Feed(parser, "\u001b]4;1;#00FF00\u0007");
            Feed(parser, "\u001b[31mX");
            var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
            ref readonly var style = ref snapshot.GetStyle(snapshot.Cells[0].StyleId);
            Assert.Equal("#00FF00", style.Foreground.ToHexString());
        }
        finally
        {
            SgrColorArgb.SetAnsiPalette(baseline);
            SgrColorArgb.ResetExtendedPalette();
        }
    }

    [Fact]
    public void Osc4_Set_FiresPaletteChangedAndInvalidatesRows()
    {
        var (adapter, parser, _) = Setup();
        uint[] baseline = SgrColorArgb.GetAnsiPaletteSnapshot();
        try
        {
            ulong before = adapter.Buffer.Generation;
            int events = 0;
            adapter.PaletteChanged += () => events++;
            Feed(parser, "\u001b]4;3;#123456\u0007");

            Assert.Equal(1, events);
            Assert.NotEqual(before, adapter.Buffer.Generation);
        }
        finally
        {
            SgrColorArgb.SetAnsiPalette(baseline);
            SgrColorArgb.ResetExtendedPalette();
        }
    }
}
