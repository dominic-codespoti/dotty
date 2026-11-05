using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class TitleModesTests
{
    [Fact]
    public void OscTitleCodes_UpdateWindowAndIconIndependently()
    {
        using var adapter = new TerminalAdapter();
        var changed = new List<string>();
        adapter.TitleChanged += changed.Add;
        adapter.OnSetTitle(2, "window"u8);
        adapter.OnSetTitle(1, "icon"u8);
        Assert.Equal("window", changed[^1]);
        Assert.Equal("window", adapter.WindowTitle);
        Assert.Equal("icon", adapter.IconTitle);
        adapter.OnSetTitle(0, "both"u8);
        Assert.Equal("both", adapter.WindowTitle);
        Assert.Equal("both", adapter.IconTitle);
    }

    [Fact]
    public void Utf8AndHexTitleModesAreIndependentAndResetToDefaults()
    {
        using var adapter = new TerminalAdapter();
        adapter.OnSetTitleModes(stackalloc[] { 0 }, enabled: true);
        adapter.OnSetTitle(2, "c3a9"u8);
        adapter.OnSetTitleModes(stackalloc[] { 0 }, enabled: false);
        adapter.OnSetTitleModes(stackalloc[] { 2 }, enabled: false);
        adapter.OnSetTitle(2, new byte[] { 0xE9 });
        Assert.Equal("é", adapter.WindowTitle);
        adapter.OnSetTitleModes(ReadOnlySpan<int>.Empty, enabled: true);
        adapter.OnSetTitle(2, "plain"u8);
        Assert.Equal("plain", adapter.WindowTitle);
    }

    [Fact]
    public void TitleReportsAreDeniedByDefaultAndUtf8BytesAreEmittedWithoutReencoding()
    {
        using var adapter = new TerminalAdapter();
        var replies = new List<byte[]>();
        adapter.RawReplyBytesRequested += value => replies.Add(value.ToArray());
        adapter.OnSetTitle(0, "é"u8);
        adapter.OnWindowOperation(20, 0, 0);
        adapter.OnWindowOperation(21, 0, 0);
        Assert.Empty(replies);

        adapter.TitleReportEnabled = true;
        adapter.OnWindowOperation(20, 0, 0);
        adapter.OnWindowOperation(21, 0, 0);
        Assert.Equal(Encoding.UTF8.GetBytes("\u001b]Lé\u001b\\"), replies[0]);
        Assert.Equal(Encoding.UTF8.GetBytes("\u001b]lé\u001b\\"), replies[1]);
    }

    [Fact]
    public void Latin1TitleReportUsesRawBytesAndSanitizesControls()
    {
        using var adapter = new TerminalAdapter();
        var replies = new List<byte[]>();
        adapter.TitleReportEnabled = true;
        adapter.RawReplyBytesRequested += value => replies.Add(value.ToArray());
        adapter.OnSetTitleModes(stackalloc[] { 2, 3 }, enabled: false);
        adapter.OnSetTitle(2, new byte[] { 0xE9, 0x1B, 0x07, 0x0A, 0x9B });
        adapter.OnWindowOperation(21, 0, 0);
        Assert.Equal(new byte[] { 0x1b, (byte)']', (byte)'l', 0xe9, 0x1b, (byte)'\\' }, replies[^1]);
    }

    [Fact]
    public void TitleReportHexModeEncodesThePayloadAndIconReportUsesIconValue()
    {
        using var adapter = new TerminalAdapter();
        var replies = new List<byte[]>();
        adapter.TitleReportEnabled = true;
        adapter.RawReplyBytesRequested += value => replies.Add(value.ToArray());
        adapter.OnSetTitle(1, "icon"u8);
        adapter.OnSetTitleModes(stackalloc[] { 1 }, enabled: true);
        adapter.OnWindowOperation(20, 0, 0);
        Assert.Equal(Encoding.ASCII.GetBytes("\x1b]L69636F6E\x1b\\"), replies[^1]);
    }

    [Fact]
    public void MalformedHexIsRejectedAndTitlesRemainBounded()
    {
        using var adapter = new TerminalAdapter();
        adapter.OnSetTitleModes(stackalloc[] { 0 }, enabled: true);
        adapter.OnSetTitle(2, "4G"u8);
        Assert.Null(adapter.WindowTitle);
        adapter.OnSetTitle(2, "abc"u8);
        Assert.Null(adapter.WindowTitle);

        byte[] invalidBeyondLimit = new byte[TerminalAdapter.MaxTitleChars * 8 + 2];
        Array.Fill(invalidBeyondLimit, (byte)'0');
        invalidBeyondLimit[^1] = (byte)'G';
        adapter.OnSetTitle(2, invalidBeyondLimit);
        Assert.Null(adapter.WindowTitle);

        adapter.ResetTitleModes();
        byte[] longTitle = new byte[TerminalAdapter.MaxTitleChars * 8];
        Array.Fill(longTitle, (byte)'x');
        adapter.OnSetTitle(2, longTitle);
        Assert.Equal(TerminalAdapter.MaxTitleChars, adapter.WindowTitle!.Length);

        string boundary = new string('x', TerminalAdapter.MaxTitleChars - 1) + "😀";
        adapter.OnSetTitle(2, Encoding.UTF8.GetBytes(boundary));
        Assert.Equal(TerminalAdapter.MaxTitleChars - 1, adapter.WindowTitle!.Length);
        Assert.False(char.IsHighSurrogate(adapter.WindowTitle[^1]));
    }

    [Fact]
    public void ParserPreservesPartialUtf8OscTitleAndRoutesTitleQueries()
    {
        using var adapter = new TerminalAdapter { TitleReportEnabled = true };
        var parser = new BasicAnsiParser { Handler = adapter };
        var titles = new List<string>();
        var replies = new List<byte[]>();
        adapter.TitleChanged += titles.Add;
        adapter.RawReplyBytesRequested += value => replies.Add(value.ToArray());

        parser.Feed("\u001b]2;caf"u8);
        parser.Feed(new byte[] { 0xc3 });
        parser.Feed(new byte[] { 0xa9, 0x1b });
        parser.Feed("\\"u8);
        parser.Feed("\u001b[21t"u8);

        Assert.Equal(new[] { "café" }, titles);
        Assert.Equal(Encoding.UTF8.GetBytes("\u001b]lcafé\u001b\\"), replies[^1]);
    }
    [Fact]
    public void ParserResetsTitleModesOnRisAndEmptyListsDoNotScroll()
    {
        using var adapter = new TerminalAdapter { TitleReportEnabled = true };
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<byte[]>();
        adapter.RawReplyBytesRequested += value => replies.Add(value.ToArray());

        parser.Feed("\u001b]2;kept\u0007X"u8);
        int row = adapter.Buffer.CursorRow;
        int col = adapter.Buffer.CursorCol;
        parser.Feed("\u001b[>0t\u001b[>t\u001b[>0t\u001b[>T"u8);
        Assert.Equal(row, adapter.Buffer.CursorRow);
        Assert.Equal(col, adapter.Buffer.CursorCol);

        parser.Feed("\u001b]2;new\u0007\u001b[>0t\u001b[>1t\u001b"u8);
        parser.Feed("c"u8);
        Assert.Equal("new", adapter.WindowTitle);
        Assert.True(adapter.TitleReportEnabled);

        parser.Feed("\u001b]2;after\u0007\u001b[21t"u8);
        Assert.Equal("after", adapter.WindowTitle);
        Assert.Equal(Encoding.UTF8.GetBytes("\u001b]lafter\u001b\\"), replies[^1]);
    }
}
