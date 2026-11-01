using System.Collections.Generic;
using System.Text;
using Dotty.Runtime.Accessibility;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public class AccessibleTextModelTests
{
    private long _now;

    private (AccessibleTextModel Model, AccessibleChangeCoalescer Coalescer) Create(int window = 50)
    {
        var coalescer = new AccessibleChangeCoalescer(() => _now, window);
        return (new AccessibleTextModel(coalescer), coalescer);
    }

    private static TerminalAdapter Screen(string text, int rows = 4, int columns = 20)
    {
        var adapter = new TerminalAdapter(rows, columns);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes(text));
        return adapter;
    }

    private static void Update(AccessibleTextModel model, TerminalAdapter adapter, AccessibleCellRange? selection = null)
    {
        using var snapshot = adapter.Buffer.CaptureRenderSnapshotVisible();
        model.Update(snapshot, selection);
    }

    [Fact]
    public void Lines_AreTrimmedAndJoined()
    {
        var (model, _) = Create();
        using var adapter = Screen("hello   \r\n\r\n  x");
        Update(model, adapter);
        Assert.Equal(["hello", "", "  x", ""], model.Lines);
        Assert.Equal("hello\n\n  x\n", model.Text);
        Assert.Equal(model.Text.Length - 1, model.CaretOffset); // cursor after "x" on row 2
    }

    public static IEnumerable<object[]> RoundTripCorpus() =>
    [
        ["abc def"],
        ["日本語テキスト"],       // wide CJK
        ["a😀b👍🏽c"],            // astral emoji, skin-tone modifier cluster
        ["e\u0301 n\u0303o"],    // combining marks
        ["x👨‍👩‍👧y"],              // ZWJ sequence
    ];

    [Theory]
    [MemberData(nameof(RoundTripCorpus))]
    public void OffsetsAndCells_RoundTrip(string line)
    {
        var (model, _) = Create();
        using var adapter = Screen(line + "\r\nsecond");
        Update(model, adapter);
        for (int row = 0; row < model.Lines.Count; row++)
        {
            foreach (var cluster in model.GetClusters(row))
            {
                Assert.Equal(cluster.Offset, model.OffsetFromCell(row, cluster.Column));
                Assert.Equal(cluster.Offset, model.OffsetFromCell(row, cluster.Column + cluster.Width - 1)); // wide tail
                Assert.Equal((row, cluster.Column), model.CellFromOffset(cluster.Offset));
                for (int inside = 1; inside < cluster.Length; inside++)
                    Assert.Equal((row, cluster.Column), model.CellFromOffset(cluster.Offset + inside));
                Assert.False(char.IsLowSurrogate(model.Text[cluster.Offset]));
            }
        }
        Assert.Equal(line.Length, model.Lines[0].Length);
    }

    [Fact]
    public void WideCharactersOccupyTwoColumns()
    {
        var (model, _) = Create();
        using var adapter = Screen("日本x");
        Update(model, adapter);
        Assert.Equal([new AccessibleCluster(0, 2, 0, 1), new AccessibleCluster(2, 2, 1, 1), new AccessibleCluster(4, 1, 2, 1)],
            model.GetClusters(0));
        Assert.Equal(3, model.OffsetFromCell(0, 10)); // past the trimmed text: line end
        Assert.Equal((0, 5), model.CellFromOffset(3));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 3)]   // over the combining cluster "e\u0301"
    [InlineData(3, -1, 1)]
    [InlineData(0, -1, 0)]
    public void CharacterMoves_StepWholeClusters(int from, int count, int expected)
    {
        var (model, _) = Create();
        using var adapter = Screen("ae\u0301b");
        Update(model, adapter);
        Assert.Equal(expected, model.Move(from, AccessibleTextUnit.Character, count));
    }

    [Theory]
    [InlineData(0, 1, 4)]    // "foo |bar.baz" — '.' is a word char by default
    [InlineData(4, 1, 12)]   // over "bar.baz", then the blank
    [InlineData(12, 1, 16)]  // "!!" punctuation run, then both line breaks (whitespace)
    [InlineData(16, 1, 19)]
    [InlineData(14, -1, 12)]
    [InlineData(16, -1, 12)]
    [InlineData(12, -1, 4)]
    [InlineData(5, -1, 4)]
    [InlineData(4, -1, 0)]
    [InlineData(18, 5, 19)]  // document end
    public void WordMoves_UseSelectionWordRules(int from, int count, int expected)
    {
        var (model, _) = Create();
        using var adapter = Screen("foo bar.baz !!\r\n\r\nqux", rows: 3);
        Update(model, adapter);
        Assert.Equal("foo bar.baz !!\n\nqux", model.Text);
        Assert.Equal(expected, model.Move(from, AccessibleTextUnit.Word, count));
    }

    [Fact]
    public void LineMoves_KeepColumn()
    {
        var (model, _) = Create();
        using var adapter = Screen("abcdef\r\nxy\r\n日本語", rows: 3);
        Update(model, adapter);
        int fromD = 3;
        Assert.Equal(7 + 2, model.Move(fromD, AccessibleTextUnit.Line, 1));   // "xy" is shorter: line end
        Assert.Equal(10 + 1, model.Move(fromD, AccessibleTextUnit.Line, 2));  // column 3 is the tail of 本
        Assert.Equal(0, model.Move(fromD, AccessibleTextUnit.Line, -1));
        Assert.Equal(model.Text.Length, model.Move(fromD, AccessibleTextUnit.Line, 9));
    }

    [Fact]
    public void Selection_IsMirroredAsOrderedOffsets()
    {
        var (model, _) = Create();
        using var adapter = Screen("日本語 abc\r\nnext", rows: 2);
        Update(model, adapter, new AccessibleCellRange(0, 2, 0, 3));
        Assert.Equal((1, 2), model.Selection);
        Update(model, adapter, new AccessibleCellRange(1, 1, 0, 7));
        Assert.Equal((5, 9), model.Selection);
        Update(model, adapter);
        Assert.Null(model.Selection);
    }

    [Fact]
    public void Coalescer_MergesBurstsUntilWindowElapses()
    {
        var (model, _) = Create(window: 50);
        var events = new List<AccessibleTextChange>();
        model.Changed += events.Add;
        using var adapter = Screen("one", rows: 3);

        Update(model, adapter);          // t=0 initial text
        _now = 10;
        new BasicAnsiParser { Handler = adapter }.Feed("!"u8); // caret + row 0
        Update(model, adapter);
        _now = 20;
        Assert.False(model.Poll());
        new BasicAnsiParser { Handler = adapter }.Feed("\r\n\r\ntwo"u8);
        Update(model, adapter, new AccessibleCellRange(0, 0, 0, 1));
        _now = 49;
        Assert.False(model.Poll());
        _now = 50;
        Assert.True(model.Poll());
        Assert.Equal(new AccessibleTextChange(AccessibleChangeKind.Text | AccessibleChangeKind.Caret | AccessibleChangeKind.Selection, 0, 2),
            Assert.Single(events));

        _now = 60;
        Update(model, adapter, new AccessibleCellRange(0, 0, 0, 1)); // nothing changed
        _now = 500;
        Assert.False(model.Poll());

        Update(model, adapter); // selection cleared at t=500
        _now = 549;
        Assert.False(model.Poll());
        _now = 550;
        Assert.True(model.Poll());
        Assert.Equal(new AccessibleTextChange(AccessibleChangeKind.Selection, -1, -1), events[1]);
    }

    [Fact]
    public void ChangeMerge_UnionsRows()
    {
        var a = new AccessibleTextChange(AccessibleChangeKind.Text, 3, 4);
        var b = new AccessibleTextChange(AccessibleChangeKind.Caret, -1, -1);
        var c = new AccessibleTextChange(AccessibleChangeKind.Text, 1, 2);
        Assert.Equal(new AccessibleTextChange(AccessibleChangeKind.Text | AccessibleChangeKind.Caret, 3, 4), a.Merge(b));
        Assert.Equal(new AccessibleTextChange(AccessibleChangeKind.Text | AccessibleChangeKind.Caret, 1, 4), b.Merge(a).Merge(c));
    }
}
