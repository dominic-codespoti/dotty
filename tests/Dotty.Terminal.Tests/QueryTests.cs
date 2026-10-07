using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>P0 query compliance: DECRQM, XTGETTCAP and modifyOtherKeys.</summary>
public sealed class QueryTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Decrqm_ReportsPrivateDecawmAndAnsiSevenAsUnrecognized()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[?7$p"); Feed(parser, "\u001b[?7l");
        Feed(parser, "\u001b[?7$p"); Feed(parser, "\u001b[7$p");
        Assert.Equal(new[] { "\u001b[?7;1$y", "\u001b[?7;2$y", "\u001b[7;0$y" }, replies);
    }

    [Fact]
    public void Xtgettcap_AnswersMultipleNamesAndCancelsQuery()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001bP+q544E;436F\u001b\\");
        Feed(parser, "\u001bP+q544E\x18");
        Assert.Equal(new[] { "\u001bP1+r544E=646F747479\u001b\\", "\u001bP1+r436F=323536\u001b\\" }, replies);
    }

    [Fact]
    public void Xtgettcap_AnswersKnownAndIgnoresUnknownNames()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001bP+q544E\u001b\\"); Feed(parser, "\u001bP+q1234\u001b\\");
        Assert.Equal(new[] { "\u001bP1+r544E=646F747479\u001b\\" }, replies);
    }

    [Fact]
    public void Xtgettcap_OverflowTailIsNotDispatched()
    {
        var (_, parser, replies) = Setup();
        byte[] prefix = Encoding.UTF8.GetBytes("\u001bP+q");
        byte[] tail = Encoding.UTF8.GetBytes("544E\u001b\\");
        var input = new byte[prefix.Length + 4097 + tail.Length];
        prefix.CopyTo(input, 0);
        Array.Fill(input, (byte)'x', prefix.Length, 4097);
        tail.CopyTo(input, prefix.Length + 4097);
        parser.Feed(input);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("\x18")]
    [InlineData("\x1A")]
    public void Dcs_CanAndSubAbortThenParserAnswersNextQuery(string cancel)
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001bP+q544E" + cancel);
        Feed(parser, "\u001bP+q544E\u001b\\");
        Assert.Equal(new[] { "\u001bP1+r544E=646F747479\u001b\\" }, replies);
    }

    [Fact]
    public void Xtgettcap_KittyKeyboardReturnsHexEncodedDecimalFlags()
    {
        var (adapter, parser, replies) = Setup();
        string flags = adapter.KittyKeyboardFlags.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string hexFlags = Convert.ToHexString(Encoding.ASCII.GetBytes(flags));
        Feed(parser, "\u001bP+q6B697474792D6B6579626F617264\u001b\\");
        Assert.Equal(new[] { "\u001bP1+r6B697474792D6B6579626F617264=" + hexFlags + "\u001b\\" }, replies);
    }

    [Fact]
    public void ModifyOtherKeysSequencesDoNotReachSgrHandler()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, "\u001b[1;31m"); Feed(parser, "\u001b[>4m"); Feed(parser, "\u001b[>1;2m"); Feed(parser, "X");
        using (var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible())
        {
            ref readonly var style = ref snapshot.GetStyle(snapshot.Cells[0].StyleId);
            Assert.True(style.Bold);
            Assert.Equal("#AA0000", style.Foreground.ToHexString());
        }
        Feed(parser, "\u001b[0;32mY");
        using var afterSgr = adapter.Buffer.CaptureRenderSnapshotVisible();
        ref readonly var green = ref afterSgr.GetStyle(afterSgr.Cells[1].StyleId);
        Assert.False(green.Bold);
        Assert.Equal("#00AA00", green.Foreground.ToHexString());
    }

    [Fact]
    public void ModifyOtherKeys_NegotiatesAndResetsOnRisAndOmittedLevel()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, "\u001b[>4;1m"); Assert.Equal(1, adapter.ModifyOtherKeysLevel);
        Feed(parser, "\u001b[>4;0m"); Assert.Equal(0, adapter.ModifyOtherKeysLevel);
        Feed(parser, "\u001b[>4;2m"); Feed(parser, "\u001bc"); Assert.Equal(0, adapter.ModifyOtherKeysLevel);
        Feed(parser, "\u001b[>4;2m"); Feed(parser, "\u001b[>4m"); Assert.Equal(0, adapter.ModifyOtherKeysLevel);
        Feed(parser, "\u001b[>4;2m"); Feed(parser, "\u001b[>m"); Assert.Equal(0, adapter.ModifyOtherKeysLevel);
    }
}
