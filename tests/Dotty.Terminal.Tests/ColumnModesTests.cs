using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class ColumnModesTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : System.IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(rows: 2, columns: 8, scrollbackCapacity: 8);
        public TerminalBuffer Buffer => Adapter.Buffer;
        public BasicAnsiParser Parser { get; }

        public Rig() => Parser = new BasicAnsiParser { Handler = Adapter };
        public void Feed(string text) => Parser.Feed(Encoding.UTF8.GetBytes(text));
        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void ColumnModeThreeRequiresModeFortyAndDeniedHostDoesNotClaimRequestedWidth()
    {
        using var t = new Rig();
        int requests = 0;
        t.Adapter.WindowResizeRequested += (_, _) => requests++;
        t.Feed("keep");

        t.Adapter.OnSetColumnMode(3, enabled: true);

        Assert.Equal(0, requests);
        Assert.Equal(8, t.Buffer.Columns);
        Assert.Equal("keep", t.Buffer.GetRowText(0).TrimEnd());

        t.Adapter.OnSetColumnMode(40, enabled: true);
        t.Adapter.OnSetColumnMode(3, enabled: true);

        Assert.Equal(1, requests);
        Assert.Equal(8, t.Buffer.Columns);
        Assert.Equal("        ", t.Buffer.GetRowText(0));
        t.Feed("Z");
        Assert.Equal('Z', (char)t.Buffer.GetCell(0, 0).Rune);

    }

    [Fact]
    public void ModeThreeClearsVisibleScreenStructurallyButPreservesHistoryAndSgr()
    {
        using var t = new Rig();
        int requestedColumns = 0;
        t.Adapter.WindowResizeRequested += (_, cols) => requestedColumns = cols;
        t.Feed("old\r\nhistory\r\nvisible\r\n" + Esc + "[31m" + Esc + "V" + "P" + Esc + "W");
        int history = t.Buffer.ScrollbackCount;
        Assert.True(history > 0);
        Assert.True(t.Buffer.GetCell(1, 0).IsProtected);
        t.Buffer.SetLeftRightMarginMode(true);
        t.Buffer.SetHorizontalMargins(2, 6);
        t.Buffer.SetCursor(1, 6);
        int redStyle = t.Buffer.GetCell(1, 0).StyleId;

        t.Adapter.OnSetColumnMode(40, enabled: true);
        t.Adapter.OnSetColumnMode(3, enabled: true);

        Assert.Equal(132, requestedColumns);
        Assert.Equal(8, t.Buffer.Columns);
        Assert.Equal(history, t.Buffer.ScrollbackCount);
        Assert.Equal((0, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("        ", t.Buffer.GetRowText(0));
        Assert.Equal("        ", t.Buffer.GetRowText(1));
        Assert.False(t.Buffer.GetCell(1, 0).IsProtected);

        t.Feed("N");
        Assert.Equal('N', (char)t.Buffer.GetCell(0, 0).Rune);
        Assert.Equal(redStyle, t.Buffer.GetCell(0, 0).StyleId);
    }

    [Fact]
    public void ModeNinetyFivePreservesVisibleCellsAtLevelFiveButIsIgnoredAtLevelFour()
    {
        using (var levelFive = new Rig())
        {
            int requestedColumns = 0;
            levelFive.Adapter.WindowResizeRequested += (_, cols) => requestedColumns = cols;
            levelFive.Feed("preserve");
            levelFive.Adapter.OnSetColumnMode(40, enabled: true);
            levelFive.Adapter.OnSetColumnMode(95, enabled: true);
            levelFive.Buffer.SetLeftRightMarginMode(true);
            levelFive.Buffer.SetHorizontalMargins(3, 6);
            levelFive.Buffer.SetOriginMode(true);

            levelFive.Adapter.OnSetColumnMode(3, enabled: true);

            Assert.True(levelFive.Adapter.NoClearOnColumnChange);
            Assert.Equal(132, requestedColumns);
            Assert.Equal("preserve", levelFive.Buffer.GetRowText(0));
            Assert.Equal((0, 0), (levelFive.Buffer.CursorRow, levelFive.Buffer.CursorCol));
        }

        using (var levelFour = new Rig())
        {
            levelFour.Adapter.OnSetConformanceLevel(64, controls: 0);
            levelFour.Adapter.OnSetColumnMode(40, enabled: true);
            levelFour.Adapter.OnSetColumnMode(95, enabled: true);
            levelFour.Feed("clear me");

            levelFour.Adapter.OnSetColumnMode(3, enabled: true);

            Assert.False(levelFour.Adapter.NoClearOnColumnChange);
            Assert.Equal("        ", levelFour.Buffer.GetRowText(0));
        }
    }

    [Fact]
    public void AllowedSynchronousHostResizeRunsAfterClearAndDoesNotEraseFollowingPrint()
    {
        using var t = new Rig();
        t.Adapter.WindowResizeRequested += (_, cols) =>
        {
            if (cols != 0)
                t.Adapter.ResizeBuffer(t.Buffer.Rows, cols);
        };
        t.Feed("old");
        t.Adapter.OnSetColumnMode(40, enabled: true);

        t.Adapter.OnSetColumnMode(3, enabled: true);
        t.Feed("after");

        Assert.Equal(132, t.Buffer.Columns);
        Assert.Equal("after", t.Buffer.GetRowText(0).TrimEnd());
    }

    [Fact]
    public void SynchronousColumnResizeExpandsFullWidthMarginsForFollowingWrites()
    {
        using var t = new Rig();
        t.Adapter.WindowResizeRequested += (_, cols) =>
        {
            if (cols != 0)
                t.Adapter.ResizeBuffer(t.Buffer.Rows, cols);
        };
        t.Feed(Esc + "[?69h" + Esc + "[2;7s");
        t.Adapter.OnSetColumnMode(40, enabled: true);

        t.Adapter.OnSetColumnMode(3, enabled: true);
        t.Feed(Esc + "[1;101HX" + Esc + "[1;132HY");

        Assert.Equal(132, t.Buffer.Columns);
        Assert.Equal((uint)'X', t.Buffer.GetCell(0, 100).Rune);
        Assert.Equal((uint)'Y', t.Buffer.GetCell(0, 131).Rune);
    }
}
