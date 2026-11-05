using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class IsoProtectionTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(rows: 3, columns: 8, scrollbackCapacity: 8);
        public BasicAnsiParser Parser { get; }
        public TerminalBuffer Buffer => Adapter.Buffer;

        public Rig() => Parser = new BasicAnsiParser { Handler = Adapter };
        public void Feed(string text) => Parser.Feed(Encoding.UTF8.GetBytes(text));
        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void ECHHonorsSpaAndEpaWithoutCouplingToSgrReset()
    {
        using var t = new Rig();
        t.Feed(Esc + "V" + "A" + Esc + "[0m" + "B" + Esc + "W" + "C");

        Assert.True(t.Buffer.GetCell(0, 0).IsProtected);
        Assert.True(t.Buffer.GetCell(0, 1).IsProtected);
        Assert.False(t.Buffer.GetCell(0, 2).IsProtected);

        t.Feed(Esc + "[1G" + Esc + "[3X");

        Assert.Equal("AB ", t.Buffer.GetRowText(0)[..3]);
        Assert.False(t.Buffer.GetCell(0, 2).IsProtected);
    }

    [Fact]
    public void ElAndEdPreserveProtectedGlyphsAndEd2StillDiscardsHistoryAndResetsCursor()
    {
        using var t = new Rig();
        t.Feed("old\r\nrow\r\nbottom\r\n" + Esc + "V" + Esc + "[3;1H" + "P" + Esc + "W" + "u");
        Assert.True(t.Buffer.ScrollbackCount > 0);

        t.Feed(Esc + "[3;1H" + Esc + "[0K");
        Assert.Equal('P', (char)t.Buffer.GetCell(2, 0).Rune);
        Assert.Equal(0u, t.Buffer.GetCell(2, 1).Rune);

        t.Feed(Esc + "[2J");
        Assert.Equal('P', (char)t.Buffer.GetCell(2, 0).Rune);
        Assert.Equal(0, t.Buffer.ScrollbackCount);
        Assert.Equal((0, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Fact]
    public void ErasingFromWideContinuationKeepsBothProtectedHalvesAndColdGraphemeMetadata()
    {
        using var t = new Rig();
        t.Feed(Esc + "V" + "👩‍💻" + Esc + "W");
        var before = t.Buffer.GetColdCell(0, 0).GraphemeIndex;
        Assert.True(before > 0);
        Assert.True(t.Buffer.GetCell(0, 0).IsProtected);
        Assert.True(t.Buffer.GetCell(0, 1).IsProtected);
        Assert.True(t.Buffer.GetCell(0, 1).IsContinuation);

        t.Feed(Esc + "[2G" + Esc + "[1X");

        Assert.True(t.Buffer.GetCell(0, 0).IsProtected);
        Assert.True(t.Buffer.GetCell(0, 1).IsProtected);
        Assert.True(t.Buffer.GetCell(0, 1).IsContinuation);
        Assert.Equal(before, t.Buffer.GetColdCell(0, 0).GraphemeIndex);
        Assert.Empty(t.Buffer.ValidateInvariants());
    }

    [Fact]
    public void UnprotectedOverwriteRemovesMarkerAndSoftResetClearsBothIsoModes()
    {
        using var t = new Rig();
        t.Feed(Esc + "V" + "P" + Esc + "W" + Esc + "[1;1H" + "U");
        Assert.False(t.Buffer.GetCell(0, 0).IsProtected);

        t.Feed(Esc + "V" + "Q" + Esc + "[!p" + "R" + Esc + "[1;1H" + Esc + "[3X");

        Assert.Equal(0u, t.Buffer.GetCell(0, 0).Rune);
        Assert.Equal(0u, t.Buffer.GetCell(0, 1).Rune);
        Assert.Equal(0u, t.Buffer.GetCell(0, 2).Rune);

        t.Feed(Esc + "V" + "S" + Esc + "c" + "T" + Esc + "[1;1H" + Esc + "[1X");
        Assert.Equal(0u, t.Buffer.GetCell(0, 0).Rune);
    }

    [Fact]
    public void SavedCursorRestoresMarkingButNotTheGlobalEraseSelector()
    {
        using var t = new Rig();
        t.Feed(Esc + "V" + Esc + "7" + Esc + "W" + Esc + "8" + "P");
        Assert.True(t.Buffer.GetCell(0, 0).IsProtected);

        t.Feed(Esc + "W" + "U" + Esc + "[1G" + Esc + "[2X");
        Assert.Equal('P', (char)t.Buffer.GetCell(0, 0).Rune);
        Assert.Equal(0u, t.Buffer.GetCell(0, 1).Rune);
    }

    [Fact]
    public void DecscaTakesOverFromIsoProtectionForOrdinaryErase()
    {
        using var t = new Rig();
        t.Feed(Esc + "V" + "A" + Esc + "[1\"q" + "B" + Esc + "W");
        Assert.True(t.Buffer.GetCell(0, 0).IsProtected);
        Assert.True(t.Buffer.GetCell(0, 1).IsProtected);

        t.Feed(Esc + "[1G" + Esc + "[2X");

        Assert.Equal(0u, t.Buffer.GetCell(0, 0).Rune);
        Assert.Equal(0u, t.Buffer.GetCell(0, 1).Rune);
    }

    [Fact]
    public void ReflowCopiesProtectionToSynthesizedWideContinuationBeforeErase()
    {
        using var adapter = new TerminalAdapter(rows: 2, columns: 4);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(Esc + "V" + "界" + Esc + "W"));

        adapter.Buffer.Resize(2, 2);

        var row = adapter.Buffer.GetRowCells(0);
        Assert.True(row[0].IsProtected);
        Assert.True(row[1].IsProtected);
        Assert.True(row[1].IsContinuation);

        adapter.Buffer.SetCursor(0, 1);
        parser.Feed(Encoding.UTF8.GetBytes(Esc + "[1X"));
        row = adapter.Buffer.GetRowCells(0);
        Assert.True(row[0].IsProtected);
        Assert.True(row[1].IsProtected);
        Assert.True(row[1].IsContinuation);
        Assert.Empty(adapter.Buffer.ValidateInvariants());
    }

    [Fact]
    public void AlternatingProtectedAndUnprotectedCellsEraseInRunsWithConsistentRowMetadata()
    {
        using var adapter = new TerminalAdapter(rows: 2, columns: 64);
        var parser = new BasicAnsiParser { Handler = adapter };
        var input = new StringBuilder();
        for (int i = 0; i < 64; i++)
            input.Append(i % 2 == 0 ? Esc + "V" : Esc + "W").Append((char)('A' + i % 26));
        input.Append(Esc + "[1;1H" + Esc + "[64X");
        parser.Feed(Encoding.UTF8.GetBytes(input.ToString()));

        var row = adapter.Buffer.GetRowCells(0);
        for (int i = 0; i < row.Length; i++)
        {
            if ((i & 1) == 0)
            {
                Assert.True(row[i].IsProtected);
                Assert.Equal((uint)('A' + i % 26), row[i].Rune);
            }
            else
            {
                Assert.False(row[i].IsProtected);
                Assert.Equal(0u, row[i].Rune);
            }
        }
        Assert.Empty(adapter.Buffer.ValidateInvariants());
        using var linked = new TerminalAdapter(rows: 2, columns: 4);
        var linkedParser = new BasicAnsiParser { Handler = linked };
        linkedParser.Feed(Encoding.UTF8.GetBytes("abcd" + Esc + "V" + "E" + Esc + "W"));
        Assert.True(linked.Buffer.ActiveBuffer.GetRowContinuesPrevious(1));
        linkedParser.Feed(Encoding.UTF8.GetBytes(Esc + "[1;1H" + Esc + "[4X"));
        Assert.False(linked.Buffer.ActiveBuffer.GetRowContinuesPrevious(1));
        Assert.Equal('E', (char)linked.Buffer.GetCell(1, 0).Rune);
        Assert.Empty(linked.Buffer.ValidateInvariants());
    }
}
