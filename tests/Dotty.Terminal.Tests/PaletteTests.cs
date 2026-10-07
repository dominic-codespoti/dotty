using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class PaletteTests
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

    private static uint Foreground(TerminalAdapter adapter, int cell = 0)
    {
        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
        return snapshot.GetStyle(snapshot.Cells[cell].StyleId).Foreground.Argb;
    }

    private static uint[] StockAnsi() =>
    [
        0xFF000000u, 0xFFAA0000u, 0xFF00AA00u, 0xFFAA5500u,
        0xFF0000AAu, 0xFFAA00AAu, 0xFF00AAAAu, 0xFFAAAAAAu,
        0xFF555555u, 0xFFFF5555u, 0xFF55FF55u, 0xFFFFFF55u,
        0xFF5555FFu, 0xFFFF55FFu, 0xFF55FFFFu, 0xFFFFFFFFu,
    ];

    [Fact]
    public void Osc4_QueryHasIndexAndSixteenBitRgbFormat()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b]4;1;#00ff00\u0007\u001b]4;1;?\u0007");
        Assert.Equal("\u001b]4;1;rgb:0000/FFFF/0000\u001b\\", Assert.Single(replies));
    }

    [Fact]
    public void Osc4_SetAndMalformedSpecsDoNotReply()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b]4;1;#ff0000\u0007\u001b]4;2;bad\u0007");
        Assert.Empty(replies);
    }

    [Fact]
    public void Osc10And11SetsDoNotReplyAndQueriesReportRgbFormat()
    {
        var (adapter, parser, replies) = Setup();
        adapter.SetDefaultColors("#AABBCC", "#112233");
        Feed(parser, "\u001b]10;#010203\u0007\u001b]11;not-a-color\u0007");
        Assert.Empty(replies);
        Assert.Equal(0xFF010203u, adapter.DefaultForegroundOverrideArgb);
        Assert.Null(adapter.DefaultBackgroundOverrideArgb);
        Feed(parser, "\u001b]10;?\u0007\u001b]11;?\u0007");
        Assert.Equal("\u001b]10;rgb:0101/0202/0303\u001b\\", replies[0]);
        Assert.Equal("\u001b]11;rgb:1111/2222/3333\u001b\\", replies[1]);
    }

    [Fact]
    public void PaletteIsIsolatedPerAdapterIncludingStyles()
    {
        var (a, parserA, _) = Setup();
        var (b, parserB, _) = Setup();
        Feed(parserA, "\u001b]4;1;#00ff00\u0007\u001b[31mA");
        Feed(parserB, "\u001b[31mB");
        Assert.Equal(0xFF00FF00u, Foreground(a));
        Assert.Equal(0xFFAA0000u, Foreground(b));
    }

    [Fact]
    public void Osc104ResetsAllOrSelectedEntriesAndIgnoresBadTokens()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b]4;1;#00ff00;2;#0000ff;200;#123456\u0007");
        Feed(parser, "\u001b]104;2;bad;;\u0007");
        Feed(parser, "\u001b]4;2;?;1;?;200;?\u0007");
        Assert.Equal(3, replies.Count);
        Assert.Equal("\u001b]4;2;rgb:0000/AAAA/0000\u001b\\", replies[0]);
        Assert.Equal("\u001b]4;1;rgb:0000/FFFF/0000\u001b\\", replies[1]);
        Assert.Equal("\u001b]4;200;rgb:1212/3434/5656\u001b\\", replies[2]);
        Feed(parser, "\u001b]104\u0007\u001b]4;1;?;200;?\u0007");
        Assert.Equal("\u001b]4;1;rgb:AAAA/0000/0000\u001b\\", replies[3]);
        Assert.Equal("\u001b]4;200;rgb:FFFF/0000/D7D7\u001b\\", replies[4]);
    }

    [Fact]
    public void Osc110And111ClearOnlyTheirOwnOverrides()
    {
        var (adapter, parser, replies) = Setup();
        int changes = 0;
        adapter.PaletteChanged += () => changes++;
        Feed(parser, "\u001b]10;#010203\u0007\u001b]11;#040506\u0007");
        Feed(parser, "\u001b]110\u0007");
        Assert.Null(adapter.DefaultForegroundOverrideArgb);
        Assert.Equal(0xFF040506u, adapter.DefaultBackgroundOverrideArgb);
        Feed(parser, "\u001b]111\u0007");
        Assert.Null(adapter.DefaultBackgroundOverrideArgb);
        Assert.Equal(4, changes);
        Assert.Empty(replies);
    }

    [Fact]
    public void SetPaletteBaselineRemapsExistingAnsiStylesAndClearsOverrides()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, "\u001b[31mR\u001b]4;1;#00ff00\u0007");
        Assert.Equal(0xFF00FF00u, Foreground(adapter));
        uint[] custom = StockAnsi();
        custom[1] = 0xFF112233u;
        int changes = 0;
        adapter.PaletteChanged += () => changes++;
        adapter.SetPaletteBaseline(custom);
        Assert.Equal(0xFF112233u, Foreground(adapter));
        Feed(parser, "\u001b[31mX");
        Assert.Equal(0xFF112233u, Foreground(adapter, 1));
        Assert.Equal(1, changes);
        adapter.SetPaletteBaseline(custom);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void RisRestoresAnsiAndExtendedEntriesAndClearsDefaultOverrides()
    {
        var (adapter, parser, _) = Setup();
        Feed(parser, "\u001b]4;1;#00ff00;200;#010203\u0007\u001b]10;#040506\u0007");
        int changes = 0;
        adapter.PaletteChanged += () => changes++;
        Feed(parser, "\u001bc");
        Assert.Equal(1, changes);
        Assert.Null(adapter.DefaultForegroundOverrideArgb);
        Feed(parser, "\u001b[31mA\u001b[38;5;200mB");
        Assert.Equal(0xFFAA0000u, Foreground(adapter));
        Assert.Equal(0xFFFF00D7u, Foreground(adapter, 1));
    }

    [Fact]
    public void SetDefaultColorsIsIdempotentAndPreservesOverrides()
    {
        var (adapter, parser, _) = Setup();
        adapter.SetDefaultColors("#112233", "#445566");
        Feed(parser, "\u001b]10;#ABCDEF\u0007");
        int changes = 0;
        adapter.PaletteChanged += () => changes++;
        adapter.SetDefaultColors("112233", "445566");
        Assert.Equal(0xFFABCDEFu, adapter.DefaultForegroundOverrideArgb);
        Assert.Equal(0, changes);
    }
}
