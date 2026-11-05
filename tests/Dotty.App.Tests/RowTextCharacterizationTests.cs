using Dotty.Runtime.Hyperlinks;
using Dotty.Runtime.Search;
using Dotty.Terminal.Adapter;
using Xunit;
using SearchMatch = Dotty.Runtime.Search.SearchMatch;

namespace Dotty.App.Tests;

/// <summary>
/// Characterization of every row-to-text path (WI-T0): the golden strings below were derived by reading the code
/// that existed before the <see cref="RowText"/> unification, and the same tests must keep passing after it.
/// Two policies exist and are pinned here: strings keep one space per wide-glyph placeholder cell
/// (<c>GetRowText</c>, scrollback lines, snapshot row text), while search and hyperlink text skip the placeholder.
/// </summary>
public sealed class RowTextCharacterizationTests
{
    private const int Cols = 12;
    private const string Smile = "\U0001F600";
    private const string Family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";

    /// <summary>
    /// Ten 12-column rows: ASCII with gaps, CJK, combining mark, emoji, ZWJ family, hyperlink cells, a wide glyph that
    /// does not fit at the right edge (it lands on the next row), one that exactly fits, and a row with never-written
    /// cells between two written ones.
    /// </summary>
    private static TerminalBuffer BuildFixture()
    {
        var buffer = new TerminalBuffer(rows: 10, columns: Cols, scrollbackCapacity: 50);
        WriteAt(buffer, 0, 0, "ab  cd");
        WriteAt(buffer, 1, 0, "界a界");
        WriteAt(buffer, 2, 0, "e\u0301x");
        WriteAt(buffer, 3, 0, Smile + "x");
        WriteAt(buffer, 4, 0, Family + "x");

        var link = CellAttributes.Default;
        link.HyperlinkId = buffer.GetOrCreateHyperlinkId("https://example.com/");
        buffer.SetCursor(5, 0);
        buffer.WriteText("link".AsSpan(), link);
        buffer.WriteText(" plain".AsSpan(), CellAttributes.Default);

        // 11 narrow cells, then a width-2 glyph that cannot fit in the last column: it wraps to row 7 and
        // row 6 keeps an empty final cell.
        WriteAt(buffer, 6, 0, "abcdefghijk界");
        WriteAt(buffer, 8, 0, "abcdefghij界");
        WriteAt(buffer, 9, 0, "x");
        WriteAt(buffer, 9, 4, "y");
        return buffer;
    }

    private static void WriteAt(TerminalBuffer buffer, int row, int col, string text)
    {
        buffer.SetCursor(row, col);
        buffer.WriteText(text.AsSpan(), CellAttributes.Default);
    }

    private static string Cells(string text, int usedCells) => text + new string(' ', Cols - usedCells);

    /// <summary>Full-width row strings (placeholder cell = one space, unwritten cell = one space).</summary>
    private static string[] VisibleGolden() =>
    [
        Cells("ab  cd", 6),
        Cells("界 a界 ", 5),
        Cells("e\u0301x", 2),
        Cells(Smile + " x", 3),
        Cells(Family + " x", 3),
        Cells("link plain", 10),
        Cells("abcdefghijk", 11),
        Cells("界 ", 2),
        "abcdefghij界 ",
        Cells("x   y", 5),
    ];

    /// <summary>Scrollback strings stop at the row's last written cell (rows 0..8 after scrolling nine lines).</summary>
    private static readonly string[] ScrollbackGolden =
    [
        "ab  cd",
        "界 a界 ",
        "e\u0301x",
        Smile + " x",
        Family + " x",
        "link plain",
        "abcdefghijk",
        "界 ",
        "abcdefghij界 ",
    ];

    private static string Format(IReadOnlyList<SearchMatch> matches) =>
        string.Join(";", matches.Select(m => $"{m.Row}:{m.StartCol}-{m.EndCol}"));

    private static string Search(TerminalBuffer buffer, string query, bool regex = false, bool matchCase = false)
    {
        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        return Format(SearchEngine.FindMatches(snapshot, query, regex, matchCase));
    }

    // ---- GetRowText (TerminalBuffer.GetRowText) ----

    [Fact]
    public void GetRowText_MatchesGolden()
    {
        var buffer = BuildFixture();
        string[] golden = VisibleGolden();
        for (int row = 0; row < golden.Length; row++)
            Assert.Equal(golden[row], buffer.GetRowText(row));
    }

    [Fact]
    public void GetRowText_OutOfRangeRowIsBlank()
    {
        var buffer = BuildFixture();
        Assert.Equal(new string(' ', Cols), buffer.GetRowText(10));
        Assert.Equal(new string(' ', Cols), buffer.GetRowText(-1));
    }

    [Fact]
    public void GetRowText_StraddlingWideGlyphLeavesLastCellBlankAndWrapsToNextRow()
    {
        var buffer = BuildFixture();
        Assert.Equal("abcdefghijk ", buffer.GetRowText(6));
        Assert.Equal("界" + new string(' ', Cols - 1), buffer.GetRowText(7));
    }

    // ---- RenderSnapshot row text (visible-row capture) ----

    [Fact]
    public void VisibleSnapshot_RowText_MatchesGolden()
    {
        var buffer = BuildFixture();
        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        string[] golden = VisibleGolden();
        for (int row = 0; row < golden.Length; row++)
            Assert.Equal(golden[row], snapshot.GetVisibleRowText(row));
    }

    [Fact]
    public void VisibleSnapshot_RowText_OutOfRangeRowIsEmpty()
    {
        var buffer = BuildFixture();
        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        Assert.Equal(string.Empty, snapshot.GetVisibleRowText(-1));
        Assert.Equal(string.Empty, snapshot.GetVisibleRowText(10));
    }

    // ---- Scrollback rows (Screen.GetScrollbackRow via TerminalBuffer, and the search snapshot's cell copy) ----

    private static TerminalBuffer BuildScrolledFixture()
    {
        var buffer = BuildFixture();
        buffer.ScrollUpLines(9);
        return buffer;
    }

    [Fact]
    public void ScrollbackLines_MatchGolden_OldestFirst()
    {
        var buffer = BuildScrolledFixture();
        Assert.Equal(ScrollbackGolden.Length, buffer.ScrollbackCount);
        for (int i = 0; i < ScrollbackGolden.Length; i++)
        {
            Assert.Equal(ScrollbackGolden[i], buffer.GetScrollbackLine(i).Text);
            Assert.Equal(ScrollbackGolden[i], buffer.GetScrollbackLineText(i));
        }

        Assert.Equal(ScrollbackGolden, buffer.GetScrollbackLines().ToArray());
    }

    [Fact]
    public void ScrollbackLine_OutOfRangeIsEmpty()
    {
        var buffer = BuildScrolledFixture();
        Assert.Equal(string.Empty, buffer.GetScrollbackLine(-1).Text);
        Assert.Equal(string.Empty, buffer.GetScrollbackLine(ScrollbackGolden.Length).Text);
    }

    [Fact]
    public void SearchSnapshot_ScrollbackRows_MatchGolden_OldestFirst()
    {
        var buffer = BuildScrolledFixture();
        using var snapshot = buffer.CaptureRenderSnapshotForSearch();
        Assert.Equal(ScrollbackGolden.Length, snapshot.ScrollbackCount);
        for (int i = 0; i < ScrollbackGolden.Length; i++)
        {
            // The snapshot's scrollback copy uses the live buffer's index: 0 = oldest.
            Assert.True(snapshot.TryGetScrollbackRow(i, out var hot, out var cold));
            Assert.Equal(ScrollbackGolden[i], RowText.Build(hot, cold, 0, hot.Length - 1, ContinuationPolicy.SpaceCell));
        }

        Assert.False(snapshot.TryGetScrollbackRow(-1, out _, out _));
        Assert.False(snapshot.TryGetScrollbackRow(ScrollbackGolden.Length, out _, out _));
    }

    [Fact]
    public void VisibleSnapshot_CarriesNoScrollbackCells()
    {
        var buffer = BuildScrolledFixture();
        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        Assert.False(snapshot.TryGetScrollbackRow(0, out _, out _));
    }

    [Fact]
    public void ScrolledFixture_VisibleRowKeepsItsText()
    {
        var buffer = BuildScrolledFixture();
        Assert.Equal(Cells("x   y", 5), buffer.GetRowText(0));
        Assert.Equal(new string(' ', Cols), buffer.GetRowText(1));
    }

    // ---- SearchEngine (skip-continuation policy, trailing blanks trimmed, display-column mapping) ----

    [Theory]
    [InlineData("cd", "0:4-6;6:2-4;8:2-4")]
    [InlineData("界a", "1:0-3")]
    [InlineData("a界", "1:2-5")]
    [InlineData("界", "1:0-2;1:3-5;7:0-2;8:10-12")]
    [InlineData("e\u0301x", "2:0-2")]
    [InlineData("x", "2:1-2;3:2-3;4:2-3;9:0-1")]
    [InlineData("x   y", "9:0-5")]
    [InlineData("j界", "8:9-12")]
    [InlineData("k pl", "5:3-7")]
    [InlineData(" ", "0:2-3;0:3-4;5:4-5;9:1-2;9:2-3;9:3-4")]
    [InlineData("d ", "")]
    [InlineData("n ", "")]
    [InlineData("AB", "0:0-2;6:0-2;8:0-2")]
    public void SearchEngine_LiteralColumns_MatchGolden(string query, string expected)
    {
        Assert.Equal(expected, Search(BuildFixture(), query));
    }

    [Fact]
    public void SearchEngine_MatchCase_IsHonored()
    {
        var buffer = BuildFixture();
        Assert.Equal("", Search(buffer, "AB", matchCase: true));
        Assert.Equal("0:0-2;6:0-2;8:0-2", Search(buffer, "ab", matchCase: true));
    }

    [Fact]
    public void SearchEngine_Regex_UsesDisplayColumns()
    {
        Assert.Equal("0:1-5", Search(BuildFixture(), @"b\s+c", regex: true));
    }

    [Fact]
    public void SearchEngine_LiteralMatchesOverlap()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 12);
        WriteAt(buffer, 0, 0, "aaaa");
        Assert.Equal("0:0-2;0:1-3;0:2-4", Search(buffer, "aa"));
    }

    // ---- TerminalSearch (live-buffer search; its text paths stay separate from RowText) ----

    [Fact]
    public void TerminalSearch_LiveSearch_ColumnsAreTextIndices()
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 24, scrollbackCapacity: 20);
        WriteAt(buffer, 0, 0, "界needle");
        buffer.ScrollUpLines(1);
        WriteAt(buffer, 0, 0, "界needle");

        var search = new TerminalSearch(buffer);
        Assert.Equal(2, search.Search("needle"));

        // Scrollback text keeps the wide glyph's placeholder as a space ("界 needle"); the visible row skips it.
        Assert.Equal(-1, search.Matches[0].Row);
        Assert.Equal(2, search.Matches[0].StartColumn);
        Assert.Equal(6, search.Matches[0].Length);
        Assert.Equal(0, search.Matches[1].Row);
        Assert.Equal(1, search.Matches[1].StartColumn);
        Assert.Equal(6, search.Matches[1].Length);
    }

    // ---- HyperlinkScanner (skip-continuation policy; columns map through wide glyphs) ----

    [Fact]
    public void HyperlinkScanner_ImplicitUrlAfterWideGlyph_UsesDisplayColumns()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 40);
        WriteAt(buffer, 0, 0, "界https://a.io/x");

        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        var links = HyperlinkScanner.ScanRow(snapshot, 0);

        var link = Assert.Single(links);
        Assert.Equal(HyperlinkSource.Detected, link.Source);
        Assert.Equal("https://a.io/x", link.Url);
        Assert.Equal(2, link.StartCol);
        Assert.Equal(15, link.EndCol);

        Assert.False(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 1));
        Assert.True(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 2));
        Assert.True(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 15));
        Assert.False(HyperlinkScanner.ContainsLinkAt(snapshot, 0, 16));
    }

    [Fact]
    public void HyperlinkScanner_ExplicitSpanText_SkipsPlaceholderCells()
    {
        var buffer = new TerminalBuffer(rows: 3, columns: 40);
        var link = CellAttributes.Default;
        link.HyperlinkId = buffer.GetOrCreateHyperlinkId("https://example.com/");
        buffer.SetCursor(1, 0);
        buffer.WriteText("界x".AsSpan(), link);
        buffer.WriteText(" end".AsSpan(), CellAttributes.Default);

        using var snapshot = buffer.CaptureRenderSnapshotVisible();
        var span = Assert.Single(HyperlinkScanner.ScanRow(snapshot, 1));

        Assert.Equal(HyperlinkSource.Explicit, span.Source);
        Assert.Equal(0, span.StartCol);
        Assert.Equal(2, span.EndCol);
        Assert.Equal("界x", HyperlinkScanner.GetSpanText(snapshot, span));
    }
}
