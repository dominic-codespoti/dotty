using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class CursorMovementConformanceTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; }
        public BasicAnsiParser Parser { get; }
        public List<string> Replies { get; } = new();

        public Rig(int rows = 8, int columns = 10)
        {
            Adapter = new TerminalAdapter(rows, columns);
            Adapter.ReplyRequested += reply => Replies.Add(reply.ToString());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }
        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));
        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void ChaHpaAndVpaAddressOneAxisWithinOriginAndPreserveTheOther()
    {
        using var t = new Rig();
        t.Feed(Esc + "[3;5r" + Esc + "[?6h" + Esc + "[2;4H");
        Assert.Equal((3, 3), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));

        t.Feed(Esc + "[7`"); // HPA aliases CHA and addresses the origin-relative column.
        Assert.Equal((3, 6), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
        t.Feed(Esc + "[6n");
        Assert.Equal(Esc + "[2;7R", t.Replies[^1]);

        t.Feed(Esc + "[5d"); // VPA addresses the origin-relative row, preserving column.
        Assert.Equal((4, 6), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void HprAndVprUseAbsoluteBoundsRatherThanDirectionalMovementMargins()
    {
        using var t = new Rig();
        t.Feed(Esc + "[3;5r" + Esc + "[?69h" + Esc + "[3;8s");
        t.Feed(Esc + "[1;1H" + Esc + "[C"); // outside the vertical margin region: move in the screen range.
        Assert.Equal((0, 1), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
        t.Feed(Esc + "[4;5H");
        t.Feed(Esc + "[99C");
        Assert.Equal((3, 7), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
        t.Feed(Esc + "[99a");
        Assert.Equal((3, 9), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));

        t.Feed(Esc + "[4;5H" + Esc + "[99B");
        Assert.Equal((4, 4), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
        t.Feed(Esc + "[99e");
        Assert.Equal((7, 4), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
        t.Feed(Esc + "[1;1H" + Esc + "[999999999999999999999999a");
        Assert.Equal((0, 9), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void CsiSOverloadsMarginsOnlyWhenEnabledAndEsc7StillSavesCursor()
    {
        using var t = new Rig();
        t.Feed(Esc + "[3;5r" + Esc + "[?69h" + Esc + "[3;8s");
        Assert.True(t.Adapter.Buffer.LeftRightMarginMode);
        t.Feed(Esc + "[?6h" + Esc + "[1;1H");
        Assert.Equal((2, 2), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));

        t.Feed(Esc + "[?6l" + Esc + "[?69l" + Esc + "[4;6H" + Esc + "7" + Esc + "[2;9H" + Esc + "8");
        Assert.Equal((3, 5), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));

        t.Feed(Esc + "[2;2H" + Esc + "[s" + Esc + "[5;9H" + Esc + "8");
        Assert.Equal((1, 1), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void InvalidPrivateOrIntermediateCursorSequencesDoNotAliasSupportedForms()
    {
        using var t = new Rig();
        t.Feed(Esc + "[3;4H");
        t.Feed(Esc + "[?9`" + Esc + "[?9a" + Esc + "[?9e" + Esc + "[9$a");
        Assert.Equal((2, 3), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void DisablingHorizontalMarginsPreservesTheCursorPosition()
    {
        using var t = new Rig();
        t.Feed(Esc + "[?69h" + Esc + "[3;8s" + Esc + "[4;4H\b" + Esc + "[?69l");

        Assert.Equal((3, 2), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void BackTabIgnoresMarginsAndStopsAtTheScreenEdgeForAnOversizedCount()
    {
        using var t = new Rig();
        t.Feed(Esc + "[?69h" + Esc + "[3;8s" + Esc + "[4;7H" + Esc + "[2147483647Z");

        Assert.Equal((3, 0), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void ResettingOneReverseWrapModeDoesNotDisableTheOther()
    {
        using var t = new Rig();
        t.Feed(Esc + "[?1045h" + Esc + "[?45h" + Esc + "[?45l" + Esc + "[3;1H" + Esc + "[D");
        Assert.Equal((1, 9), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));

        t.Feed(Esc + "[?1045l" + Esc + "[3;1H" + Esc + "[D");
        Assert.Equal((2, 0), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));

        t.Feed(Esc + "[?45h" + Esc + "[HabcdefghijX" + Esc + "[2D");
        Assert.Equal((0, 9), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void DecalnFillsTheVisibleScreenAndHomesWithoutGrowingScrollback()
    {
        using var t = new Rig(rows: 8, columns: 40);
        t.Feed(Esc + "[3;6r" + Esc + "[?69h" + Esc + "[5;30s" + Esc + "[?6h" + Esc + "[31m");
        t.Feed(Esc + "#");
        t.Feed("\u007f8");

        Assert.Equal((0, 0), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
        Assert.False(t.Adapter.Buffer.OriginMode);
        Assert.True(t.Adapter.Buffer.LeftRightMarginMode);
        Assert.Equal(0, t.Adapter.Buffer.ScrollbackCount);
        for (int row = 0; row < t.Adapter.Buffer.Rows; row++)
            Assert.Equal(new string('E', t.Adapter.Buffer.Columns), t.Adapter.Buffer.GetRowText(row));

        t.Feed(Esc + "[1;40HXY");
        Assert.Equal((1, 1), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
    }

    [Fact]
    public void DecalnClearsBothMarginBoundsForSubsequentCursorMovement()
    {
        using var t = new Rig(rows: 8, columns: 40);
        t.Feed(Esc + "[3;6r" + Esc + "[?69h" + Esc + "[5;30s" + Esc + "#8");

        t.Feed(Esc + "[4;5H" + Esc + "[99B");
        Assert.Equal(7, t.Adapter.Buffer.CursorRow);

        t.Feed(Esc + "[4;5H" + Esc + "[99C");
        Assert.Equal(39, t.Adapter.Buffer.CursorCol);
    }

    [Fact]
    public void ChtCountsSuccessiveStopsAndClampsAtTheRightMargin()
    {
        using var t = new Rig(rows: 12, columns: 40);
        t.Feed(Esc + "[3;10r" + Esc + "[?69h" + Esc + "[5;30s" + Esc + "[7;1H" + Esc + "[2I");
        Assert.Equal((6, 16), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));

        t.Feed(Esc + "[2I");
        Assert.Equal((6, 29), (t.Adapter.Buffer.CursorRow, t.Adapter.Buffer.CursorCol));
        t.Feed(Esc + "[5;999s" + Esc + "[7;1H" + Esc + "[999C");
        Assert.Equal(39, t.Adapter.Buffer.CursorCol);
    }

    [Fact]
    public void ClearAllTabStopsRemovesDefaultsWhileCustomStopsStillWork()
    {
        using var t = new Rig(rows: 8, columns: 40);
        t.Feed(Esc + "[3g" + Esc + "[1;21H" + Esc + "H" + Esc + "[1;1H" + Esc + "[I");
        Assert.Equal(20, t.Adapter.Buffer.CursorCol);

        t.Feed(Esc + "[I");
        Assert.Equal(39, t.Adapter.Buffer.CursorCol);
    }
}
