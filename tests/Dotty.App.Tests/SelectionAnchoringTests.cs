using System;
using System.Numerics;
using System.Text;
using Dotty.Runtime.Selection;
using Dotty.Silk.Input;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Silk.NET.Input;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Content-anchored selection (W4-C14): output, scroll, reflow, invalidation, extraction and word rules.</summary>
public sealed class SelectionAnchoringTests
{
    private sealed class Term
    {
        public readonly TerminalAdapter Adapter;
        public readonly BasicAnsiParser Parser = new();
        public readonly TextSelectionService Selection;

        public Term(int rows, int columns)
        {
            Adapter = new TerminalAdapter(rows, columns);
            Parser.Handler = Adapter;
            Selection = new TextSelectionService(Adapter.Buffer);
        }

        public TerminalBuffer Buffer => Adapter.Buffer;
        public void Feed(string text) => Parser.Feed(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void OutputWhileSelected_KeepsTheSameText()
    {
        var t = new Term(5, 20);
        t.Feed("alpha\r\nbeta gamma\r\n");
        t.Selection.SelectWord(1, 6);
        Assert.Equal("gamma", t.Selection.GetSelectedText());

        for (int i = 0; i < 12; i++)
            t.Feed($"line {i}\r\n");

        Assert.True(t.Selection.HasSelection);
        Assert.Equal("gamma", t.Selection.GetSelectedText());
        Assert.True(t.Selection.GetNormalizedRange().StartRow < 0);
    }

    [Fact]
    public void Resize_NarrowerAndWider_KeepsText()
    {
        var t = new Term(6, 30);
        t.Feed("hello world foo bar\r\nnext");
        t.Selection.StartSelection(0, 6);
        t.Selection.UpdateSelection(0, 14);
        Assert.Equal("world foo", t.Selection.GetSelectedText());

        t.Adapter.ResizeBuffer(6, 8);
        Assert.True(t.Selection.HasSelection);
        Assert.Equal("world foo", t.Selection.GetSelectedText());

        t.Adapter.ResizeBuffer(6, 40);
        Assert.True(t.Selection.HasSelection);
        Assert.Equal("world foo", t.Selection.GetSelectedText());
    }

    [Fact]
    public void Csi3J_ClearsSelection()
    {
        var t = new Term(3, 20);
        for (int i = 0; i < 8; i++) t.Feed($"row {i}\r\n");
        t.Selection.SelectLine(-2);
        Assert.True(t.Selection.HasSelection);

        t.Feed("\x1b[3J");
        Assert.False(t.Selection.HasSelection);
    }

    [Fact]
    public void Eviction_ClearsSelection()
    {
        var t = new Term(3, 20);
        t.Buffer.MaxScrollback = 4;
        for (int i = 0; i < 4; i++) t.Feed($"row {i}\r\n");
        t.Selection.SelectLine(-t.Buffer.ScrollbackCount);
        Assert.True(t.Selection.HasSelection);

        for (int i = 0; i < 6; i++) t.Feed($"more {i}\r\n");
        Assert.False(t.Selection.HasSelection);
    }

    [Fact]
    public void AlternateScreenSwitch_ClearsSelection()
    {
        var t = new Term(4, 20);
        t.Feed("text");
        t.Selection.SelectLine(0);
        t.Feed("\x1b[?1049h");
        Assert.False(t.Selection.HasSelection);
    }

    [Fact]
    public void BlockExtraction_CutsThroughWideCharacters()
    {
        var t = new Term(3, 10);
        t.Feed("a中b\r\n中cd");
        t.Selection.StartSelection(0, 1, SelectionMode.Block);
        t.Selection.UpdateSelection(1, 2);

        Assert.Equal(SelectionMode.Block, t.Selection.Mode);
        Assert.Equal("中" + Environment.NewLine + " c", t.Selection.GetSelectedText());
    }

    [Theory]
    [InlineData("cat /usr/local/bin:x", 6, "/usr/local/bin")]
    [InlineData("go ex.com/a?b=1&c=2#top now", 5, "ex.com/a?b=1&c=2#top")]
    [InlineData("mail me@host.org,", 6, "me@host.org")]
    [InlineData("日本語 テキスト", 2, "日本語")]
    [InlineData("say 👨‍👩‍👧 ok", 4, "👨‍👩‍👧")]
    [InlineData("a,,b", 1, ",,")]
    public void DoubleClickWord_Corpus(string line, int column, string expected)
    {
        var t = new Term(2, 40);
        t.Feed(line);
        t.Selection.SelectWord(0, column);
        Assert.Equal(expected, t.Selection.GetSelectedText());
    }

    [Fact]
    public void WordChars_ConfiguresPunctuation()
    {
        var t = new Term(2, 40);
        t.Feed("cat /usr/bin");
        t.Selection.WordChars = string.Empty;
        t.Selection.SelectWord(0, 6);
        Assert.Equal("usr", t.Selection.GetSelectedText());
    }

    [Fact]
    public void TripleClickLine_SpansContinuationRows()
    {
        var t = new Term(5, 10);
        t.Feed("0123456789abcdefghijKLMNO\r\nnext");
        t.Selection.SelectLine(1);

        var range = t.Selection.GetNormalizedRange();
        Assert.Equal(0, range.StartRow);
        Assert.Equal(2, range.EndRow);
        Assert.Equal("0123456789abcdefghijKLMNO", t.Selection.GetSelectedText());
    }

    [Fact]
    public void SelectAll_CoversScrollback()
    {
        var t = new Term(3, 10);
        for (int i = 0; i < 6; i++) t.Feed($"r{i}\r\n");
        t.Selection.SelectAll();

        var range = t.Selection.GetNormalizedRange();
        Assert.Equal(-t.Buffer.ScrollbackCount, range.StartRow);
        Assert.StartsWith("r0", t.Selection.GetSelectedText());
    }

    [Fact]
    public void SelectionOptions_ParseRectangleModifier()
    {
        Assert.Equal(RectangleSelectionModifier.Alt, SelectionOptions.ParseRectangleModifier(null));
        Assert.Equal(RectangleSelectionModifier.Ctrl, SelectionOptions.ParseRectangleModifier(" Control "));
        Assert.Equal(RectangleSelectionModifier.None, SelectionOptions.ParseRectangleModifier("none"));
        Assert.Equal(RectangleSelectionModifier.Alt, SelectionOptions.ParseRectangleModifier("bogus"));
    }
}

/// <summary>Mouse gesture table for local selection, driven through the fake mouse host.</summary>
[Collection("Allocation-sensitive tests")]
public sealed class SelectionGestureTests
{
    // Fake geometry: 10×20 cells below a 20 px tab bar; cell (row, col) centre.
    private static Vector2 At(int row, int col) => new(col * 10f + 5f, 20f + row * 20f + 10f);

    private static (TerminalMouseControllerTests.FakeTerminalMouseHost Host, Dotty.Runtime.Tabs.TerminalTab Tab) Setup(string text)
    {
        var host = new TerminalMouseControllerTests.FakeTerminalMouseHost();
        var tab = host.TabManager.CreateTab(cols: 80, rows: 24);
        host.TabManager.SelectTab(tab);
        tab.PaneTree.Layout(800, 480, 10, 20);
        tab.ActivePane.Session.Parser.Feed(Encoding.UTF8.GetBytes(text));
        return (host, tab);
    }

    [Fact]
    public void AltDrag_MakesBlockSelection()
    {
        var (host, tab) = Setup("abcdef\r\nghijkl\r\nmnopqr");
        using var _ = host;
        var controller = new TerminalMouseController(host, () => 1000);
        var mouse = new TerminalMouseControllerTests.FakeMouse { Position = At(0, 1) };
        host.Alt = true;
        controller.HandleMouseDown(mouse, MouseButton.Left);
        mouse.Position = At(2, 3);
        controller.HandleMouseMove(mouse, mouse.Position);
        controller.HandleMouseUp(mouse, MouseButton.Left);

        var selection = tab.ActivePane.Selection;
        Assert.Equal(SelectionMode.Block, selection.Mode);
        Assert.Equal("bcd" + Environment.NewLine + "hij" + Environment.NewLine + "nop", selection.GetSelectedText());
    }

    [Fact]
    public void ShiftClick_ExtendsWithOriginalGranularity()
    {
        long now = 1000;
        var (host, tab) = Setup("one two three four");
        using var _ = host;
        var controller = new TerminalMouseController(host, () => now);
        var mouse = new TerminalMouseControllerTests.FakeMouse { Position = At(0, 5) };
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);
        now += 100;
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);
        Assert.Equal("two", tab.ActivePane.Selection.GetSelectedText());

        now += 2000;
        host.Shift = true;
        mouse.Position = At(0, 15);
        controller.HandleMouseDown(mouse, MouseButton.Left);
        controller.HandleMouseUp(mouse, MouseButton.Left);

        Assert.Equal(SelectionMode.Word, tab.ActivePane.Selection.Mode);
        Assert.Equal("two three four", tab.ActivePane.Selection.GetSelectedText());
    }

    [Fact]
    public void CtrlTripleClick_SelectsShellIntegrationZone()
    {
        long now = 1000;
        var (host, tab) = Setup(
            "\x1b]133;A\x07$ \x1b]133;B\x07ls\r\n\x1b]133;C\x07out1\r\nout2\r\n\x1b]133;D;0\x07\x1b]133;A\x07$ ");
        using var _ = host;
        var controller = new TerminalMouseController(host, () => now);
        var mouse = new TerminalMouseControllerTests.FakeMouse { Position = At(1, 1) };
        host.Ctrl = true;
        for (int i = 0; i < 3; i++)
        {
            controller.HandleMouseDown(mouse, MouseButton.Left);
            controller.HandleMouseUp(mouse, MouseButton.Left);
            now += 100;
        }

        Assert.Equal("out1" + Environment.NewLine + "out2", tab.ActivePane.Selection.GetSelectedText());
    }

    [Fact]
    public void TripleClick_SelectsLogicalLine()
    {
        long now = 1000;
        var (host, tab) = Setup(new string('x', 100) + "\r\nnext");
        using var _ = host;
        var controller = new TerminalMouseController(host, () => now);
        var mouse = new TerminalMouseControllerTests.FakeMouse { Position = At(1, 3) };
        for (int i = 0; i < 3; i++)
        {
            controller.HandleMouseDown(mouse, MouseButton.Left);
            controller.HandleMouseUp(mouse, MouseButton.Left);
            now += 100;
        }

        Assert.Equal(SelectionMode.Line, tab.ActivePane.Selection.Mode);
        Assert.Equal(new string('x', 100), tab.ActivePane.Selection.GetSelectedText());
    }
}
