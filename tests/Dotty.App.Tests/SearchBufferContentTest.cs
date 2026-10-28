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

    // ---- Production-shaped capture (WI-T2) -------------------------------------------------------------------

    private static string Format(IReadOnlyList<Dotty.Runtime.Search.SearchMatch> matches) =>
        string.Join(";", matches.Select(m => $"{m.Row}:{m.StartCol}-{m.EndCol}"));

    /// <summary>
    /// 3 rows x 24 columns with five lines written: line0/line1 end up in scrollback (oldest first), line2..line4 stay
    /// visible. line0, line2 and line4 contain "needle".
    /// </summary>
    private static TerminalBuffer BuildBufferWithScrollback()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 24, scrollbackCapacity: 50);
        buffer.SetCursor(0, 0);
        buffer.WriteText("line0 needle\r\nline1\r\nline2 needle\r\nline3\r\nline4 needle".AsSpan(), CellAttributes.Default);
        Assert.Equal(2, buffer.ScrollbackCount);
        return buffer;
    }

    private const string ScrollbackAndVisibleNeedles = "-2:6-12;0:6-12;2:6-12";

    [Fact]
    public void SearchEngine_FindMatches_SearchCapture_FindsScrollbackMatches()
    {
        // The host's RefreshSearch captures with CaptureRenderSnapshotForSearch. The earlier per-frame shape
        // (CaptureRenderSnapshotVisible(sbStart: 0, sbEnd: -1)) materialized no scrollback, so scrollback was never searched.
        var buffer = BuildBufferWithScrollback();

        using var snapshot = buffer.CaptureRenderSnapshotForSearch();
        var matches = SearchEngine.FindMatches(snapshot, "needle", regex: false, matchCase: false);

        Assert.Equal(ScrollbackAndVisibleNeedles, Format(matches));
    }

    [Fact]
    public void SearchEngine_FindMatches_LiveBuffer_FindsScrollbackMatches()
    {
        var buffer = BuildBufferWithScrollback();
        Assert.Equal(ScrollbackAndVisibleNeedles, Format(SearchEngine.FindMatches(buffer, "needle")));
    }

    [Fact]
    public void SearchEngine_FindMatches_SearchCapture_MapsWideAndCombiningScrollbackToColumns()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 24, scrollbackCapacity: 20);
        buffer.SetCursor(0, 0);
        buffer.WriteText("prefix界needle\r\ne\u0301😀target\r\nvisible\r\nlast".AsSpan(), CellAttributes.Default);
        Assert.Equal(2, buffer.ScrollbackCount);

        using var snapshot = buffer.CaptureRenderSnapshotForSearch();

        Assert.Equal("-2:8-14", Format(SearchEngine.FindMatches(snapshot, "needle")));
        Assert.Equal("-1:1-9", Format(SearchEngine.FindMatches(snapshot, "😀target")));
    }

    [Fact]
    public void SearchEngine_FindMatches_SearchCapture_MarksTheActiveScrollbackMatch()
    {
        var buffer = BuildBufferWithScrollback();
        using var snapshot = buffer.CaptureRenderSnapshotForSearch();

        var matches = SearchEngine.FindMatches(snapshot, "needle", activeMatchIndex: 0);

        Assert.True(matches[0].IsActive);
        Assert.Equal(-2, matches[0].Row);
        Assert.False(matches[1].IsActive);
    }

    [Fact]
    public void TryGetScrollbackRow_UsesOldestFirstIndicesOnSnapshotAndLiveBuffer()
    {
        var buffer = BuildBufferWithScrollback();
        using var snapshot = buffer.CaptureRenderSnapshotForSearch();

        foreach (IRenderSource source in new IRenderSource[] { snapshot, buffer })
        {
            Assert.True(source.TryGetScrollbackRow(0, out var hot0, out var cold0));
            Assert.Equal("line0 needle", RowText.Build(hot0, cold0, 0, hot0.Length - 1, ContinuationPolicy.SpaceCell));
            Assert.True(source.TryGetScrollbackRow(1, out var hot1, out var cold1));
            Assert.Equal("line1", RowText.Build(hot1, cold1, 0, hot1.Length - 1, ContinuationPolicy.SpaceCell));
            Assert.False(source.TryGetScrollbackRow(2, out _, out _));
            Assert.False(source.TryGetScrollbackRow(-1, out _, out _));
        }
    }

    [Fact]
    public void VisibleOnlySnapshot_HasNoScrollbackCells_AndDisposedSearchSnapshotsReleaseThem()
    {
        var buffer = BuildBufferWithScrollback();

        using (var visible = buffer.CaptureRenderSnapshotVisible(scrollOffset: 0, sbStart: 0, sbEnd: -1))
            Assert.False(visible.TryGetScrollbackRow(0, out _, out _));

        buffer.CaptureRenderSnapshotForSearch().Dispose();
        using var reused = buffer.CaptureRenderSnapshotVisible();
        Assert.False(reused.TryGetScrollbackRow(0, out _, out _));
    }

    [Fact]
    public void SearchEngine_FindMatches_TextRangeSnapshot_StillSearchesMaterializedScrollbackText()
    {
        // Snapshots captured with an explicit text range carry strings, not cells: the engine falls back to them.
        var buffer = new TerminalBuffer(rows: 2, columns: 24);
        buffer.SetCursor(0, 0);
        buffer.WriteText("prefix界needle".AsSpan(), CellAttributes.Default);
        buffer.ScrollUpLines(1);

        using var snapshot = buffer.CaptureRenderSnapshotVisible(sbStart: 0, sbEnd: buffer.ScrollbackCount - 1);
        Assert.False(snapshot.TryGetScrollbackRow(0, out _, out _));
        Assert.Equal("-1:8-14", Format(SearchEngine.FindMatches(snapshot, "needle")));
    }

    // ---- Matching semantics (WI-T1: behavior preserved while allocations go away) ------------------------------

    private static string SearchRow(string rowText, string query, bool regex = false, bool matchCase = false)
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 30);
        buffer.SetCursor(0, 0);
        buffer.WriteText(rowText.AsSpan(), CellAttributes.Default);
        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        return Format(SearchEngine.FindMatches(snapshot, query, regex, matchCase));
    }

    [Fact]
    public void SearchEngine_Regex_InvalidPatternFallsBackToLiteral()
    {
        Assert.Equal("0:1-3", SearchRow("a[xb", "[x", regex: true));
        Assert.Equal("0:0-1", SearchRow("(a)", "(", regex: true));
    }

    [Fact]
    public void SearchEngine_Regex_FindsEveryNonEmptyMatchWithDisplayColumns()
    {
        Assert.Equal("0:1-3;0:5-7", SearchRow("a12b 34c", @"\d+", regex: true));
        Assert.Equal("", SearchRow("abc", "x*", regex: true));
    }

    [Fact]
    public void SearchEngine_Regex_RespectsMatchCase()
    {
        Assert.Equal("0:0-3", SearchRow("ABC", "abc", regex: true));
        Assert.Equal("", SearchRow("ABC", "abc", regex: true, matchCase: true));
    }

    [Fact]
    public void SearchEngine_Regex_TimeoutFallsBackToLiteralWithoutHanging()
    {
        string row = new string('a', 26) + "!";
        var started = System.Diagnostics.Stopwatch.StartNew();

        // Catastrophic backtracking is cut off by the 250 ms match timeout; the literal fallback finds no "(a+)+$".
        Assert.Equal("", SearchRow(row, "(a+)+$", regex: true));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void SearchEngine_Literal_QueryLongerThanTheRowFindsNothing()
    {
        Assert.Equal("", SearchRow("ab", "abc"));
    }

    [Fact]
    public void SearchEngine_Literal_TrailingBlankCellsAreNotSearchable()
    {
        Assert.Equal("", SearchRow("ab", "b "));
        Assert.Equal("0:1-2", SearchRow("a b", " "));
    }

    [Fact]
    public void SearchEngine_FindMatches_DoesNotAllocatePerTrailingBlankCell()
    {
        // 60 rows x 200 columns, each row ~40 text cells and 160 trailing blanks. The previous builder re-materialized the
        // row text for every blank it trimmed (megabytes per search); the current one allocates a few small objects.
        var buffer = new TerminalBuffer(rows: 60, columns: 200, scrollbackCapacity: 0);
        for (int row = 0; row < 60; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText($"row {row:D2} log line with an error in it".AsSpan(), CellAttributes.Default);
        }

        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        Assert.Equal(60, SearchEngine.FindMatches(snapshot, "error").Count); // warm up pools and JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        var matches = SearchEngine.FindMatches(snapshot, "error");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(60, matches.Count);
        Assert.True(allocated < 128 * 1024, $"FindMatches allocated {allocated} bytes");
    }
}
