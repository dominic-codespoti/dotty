using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class StatusStringAndAttributeExtentTests
{
    [Fact]
    public void StatusStringReportsCurrentRenditionAndRejectsUnsupportedRequests()
    {
        var adapter = new TerminalAdapter(rows: 6, columns: 12);
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[1m"));

        adapter.OnRequestStatusString("m"u8);
        adapter.OnRequestStatusString("?unknown"u8);
        adapter.OnRequestStatusString("*x"u8);
        Assert.Equal("\u001bP1$r0;1m\u001b\\", replies[0]);
        Assert.Equal("\u001bP0$r\u001b\\", replies[1]);
        Assert.Equal("\u001bP1$r0*x\u001b\\", replies[2]);
    }

    [Fact]
    public void AttributeChangesRespectRectangleAndStreamExtents()
    {
        var adapter = new TerminalAdapter(rows: 4, columns: 6);
        adapter.OnPrint("abcdef\r\nghijkl\r\nmnopqr".AsSpan());

        adapter.OnSetAttributeChangeExtent(2);
        adapter.OnChangeRectangleAttributes(1, 2, 2, 4, new[] { 1 }, reverse: false);

        Assert.True(AttributeAt(adapter, 0, 1).Bold);
        Assert.True(AttributeAt(adapter, 0, 3).Bold);
        Assert.False(AttributeAt(adapter, 0, 4).Bold, "Rectangle mode changed the cell right of the inclusive right bound.");
        Assert.False(AttributeAt(adapter, 1, 0).Bold, "Rectangle mode changed the cell left of the inclusive left bound.");
        Assert.True(AttributeAt(adapter, 1, 2).Bold);
        Assert.True(AttributeAt(adapter, 1, 3).Bold);
        Assert.False(AttributeAt(adapter, 1, 4).Bold, "Rectangle mode changed the cell right of the inclusive right bound.");
        adapter.OnChangeRectangleAttributes(2, 3, 2, 3, new[] { 22 }, reverse: false);
        Assert.False(AttributeAt(adapter, 1, 2).Bold, "DECCARA 22 must clear bold inside its inclusive rectangle.");
        Assert.True(AttributeAt(adapter, 1, 3).Bold);

        adapter.OnSetAttributeChangeExtent(0);
        adapter.OnChangeRectangleAttributes(1, 2, 2, 4, new[] { 5 }, reverse: false);
        Assert.True(AttributeAt(adapter, 1, 3).SlowBlink);
        Assert.True(AttributeAt(adapter, 0, 1).SlowBlink);
        Assert.False(AttributeAt(adapter, 1, 5).SlowBlink, "Stream mode changed a cell after the inclusive ending coordinate.");
        Assert.False(AttributeAt(adapter, 0, 0).SlowBlink, "Stream mode changed a cell before the inclusive starting coordinate.");
        adapter.OnChangeRectangleAttributes(1, 2, 1, 2, new[] { 1 }, reverse: true);
        Assert.False(AttributeAt(adapter, 0, 1).Bold, "DECRARA 1 must clear bold at its inclusive stream coordinate.");
        Assert.True(AttributeAt(adapter, 0, 2).Bold, "DECRARA must leave the next cell beyond its inclusive right bound unchanged.");
        Assert.True(AttributeAt(adapter, 0, 3).Bold);
        adapter.OnChangeRectangleAttributes(4, 1, 4, 6, new[] { 1 }, reverse: false);
        Assert.False(AttributeAt(adapter, 3, 0).Bold, "Stream mode changed an unprinted blank cell.");
        adapter.OnSetAttributeChangeExtent(2);
        adapter.OnChangeRectangleAttributes(4, 1, 4, 6, new[] { 1 }, reverse: false);
        Assert.True(AttributeAt(adapter, 3, 0).Bold);
    }

    [Fact]
    public void AttributeResetAndReversePreserveUnselectedStyleMetadata()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 4);
        adapter.OnSetGraphicsRendition("1;2;3;4;5;7;8".AsSpan());
        adapter.OnHyperlink("https://example.com");
        adapter.OnPrint("X".AsSpan());
        CellAttributes before = AttributeAt(adapter, 0, 0);

        adapter.OnChangeRectangleAttributes(1, 1, 1, 1, new[] { 0 }, reverse: false);
        CellAttributes reset = AttributeAt(adapter, 0, 0);
        Assert.False(reset.Bold);
        Assert.Equal(UnderlineStyle.None, reset.UnderlineStyle);
        Assert.False(reset.SlowBlink);
        Assert.False(reset.Inverse);
        Assert.True(reset.Invisible);
        Assert.True(reset.Faint);
        Assert.True(reset.Italic);
        Assert.Equal(before.HyperlinkId, reset.HyperlinkId);

        adapter.OnChangeRectangleAttributes(1, 1, 1, 1, new[] { 0 }, reverse: true);
        CellAttributes reversed = AttributeAt(adapter, 0, 0);
        Assert.True(reversed.Bold);
        Assert.Equal(UnderlineStyle.Single, reversed.UnderlineStyle);
        Assert.True(reversed.SlowBlink);
        Assert.True(reversed.Inverse);
        Assert.True(reversed.Invisible);
        Assert.True(reversed.Faint);
        Assert.True(reversed.Italic);
        Assert.Equal(before.HyperlinkId, reversed.HyperlinkId);

        adapter.OnChangeRectangleAttributes(1, 1, 1, 1, new[] { 8 }, reverse: true);
        Assert.False(AttributeAt(adapter, 0, 0).Invisible);
    }

    [Fact]
    public void DecscaStatusExcludesIsoProtectionProvenance()
    {
        var adapter = new TerminalAdapter();
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        var parser = new BasicAnsiParser { Handler = adapter };

        parser.Feed(Encoding.UTF8.GetBytes("\u001bV\u001bP$q\"q\u001b\\"));
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[1\"q\u001bP$q\"q\u001b\\"));

        Assert.Equal(new[] { "\u001bP1$r0\"q\u001b\\", "\u001bP1$r1\"q\u001b\\" }, replies);
    }

    private static CellAttributes AttributeAt(TerminalAdapter adapter, int row, int column)
    {
        var cell = adapter.Buffer.GetCell(row, column);
        return adapter.StyleSet.GetStyle(cell.StyleId);
    }
}
