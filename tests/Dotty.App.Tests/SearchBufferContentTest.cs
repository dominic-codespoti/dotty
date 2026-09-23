using System;
using Dotty.Runtime.Search;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class SearchBufferContentTest
{
    [Fact]
    public void SearchEngine_FindMatches_UsesDisplayColumnsAfterWideGlyph()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 20);
        buffer.SetCursor(0, 0);
        buffer.WriteText("界needle".AsSpan(), CellAttributes.Default);

        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        var matches = SearchEngine.FindMatches(snapshot, "needle", matchCase: false, regex: false);

        var match = Assert.Single(matches);
        Assert.Equal(0, match.Row);
        Assert.Equal(2, match.StartCol);
        Assert.Equal(8, match.EndCol);
    }

    [Fact]
    public void SearchEngine_FindMatches_MapsCombiningAndEmojiGraphemesToCells()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 20);
        buffer.SetCursor(0, 0);
        buffer.WriteText("e\u0301😀target".AsSpan(), CellAttributes.Default);

        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        var matches = SearchEngine.FindMatches(snapshot, "😀target", matchCase: false, regex: false);

        var match = Assert.Single(matches);
        Assert.Equal(0, match.Row);
        Assert.Equal(1, match.StartCol);
        Assert.Equal(9, match.EndCol);
    }

    [Fact]
    public void SearchEngine_FindMatches_ReportsNegativeScrollbackRowsInDisplayColumns()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 24);
        buffer.SetCursor(0, 0);
        buffer.WriteText("prefix界needle".AsSpan(), CellAttributes.Default);
        buffer.ScrollUpLines(1);

        int scrollbackCount = buffer.ScrollbackCount;
        Assert.True(scrollbackCount > 0);
        using var snapshot = buffer.CaptureRenderSnapshotVisible(
            sbStart: 0,
            sbEnd: scrollbackCount - 1);
        var matches = SearchEngine.FindMatches(snapshot, "needle", matchCase: false, regex: false);

        var match = Assert.Single(matches);
        Assert.Equal(-1, match.Row);
        Assert.Equal(8, match.StartCol);
        Assert.Equal(14, match.EndCol);
    }
}
