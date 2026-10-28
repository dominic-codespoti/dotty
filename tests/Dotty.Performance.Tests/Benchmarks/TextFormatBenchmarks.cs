using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Runtime.Search;
using Dotty.Runtime.Selection;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Row-to-text benchmarks for search, copy and scrollback extraction (plan 08, WI-1d). They put a number on the
/// seven independent text-extraction implementations before they are unified.
///
/// Stable entry points only, so the text-extraction refactor can change internals without invalidating the keys:
/// <see cref="SearchEngine.FindMatches"/> over a captured <see cref="RenderSnapshot"/>,
/// <see cref="TextSelectionService"/> (<c>StartSelection</c> / <c>UpdateSelection</c> / <c>GetSelectedText</c>) and
/// <see cref="TerminalBuffer.GetScrollbackLine"/>. If one of those is renamed, update only the single call in the
/// matching benchmark method; the descriptions (baseline keys) must not change.
///
/// Deliberately absent until scrollback search captures history rows in production: "search over 10k scrollback".
/// The snapshot used here is captured with the production-shaped default arguments (no scrollback text), and the
/// search buffers use scrollback capacity 0, so the search benchmarks measure exactly the visible rows.
///
/// Fixtures are built lazily by the one benchmark that needs them (see <see cref="ReflowBenchmarks"/> for why).
/// This class opts out of the no-GC region.
/// </summary>
[BenchmarkCategory("Memory", "Text")]
public class TextFormatBenchmarks : PerformanceTestBase
{
    private const int GridRows = 60;
    private const int GridColumns = 200;
    private const int ScrollbackCapacity = 10_000;
    private const int ScrollbackFixtureLines = ScrollbackCapacity + 24;
    private const string RegexQuery = @"(error|warning)\s+\w+";

    private readonly TextSelectionService _selection = new();

    private RenderSnapshot? _logSnapshot;
    private RenderSnapshot? _cjkSnapshot;
    private string? _cjkQuery;
    private TerminalBuffer? _visibleBuffer;
    private TerminalBuffer? _cjkBuffer;
    private TerminalBuffer? _scrollback;
    private TerminalBuffer? _scrollbackCjk;

    protected override bool UseNoGcRegion => false;

    #region Fixtures

    private static TerminalBuffer BuildBuffer(int rows, int columns, int scrollbackCapacity, byte[] content)
    {
        var adapter = new TerminalAdapter(rows, columns, scrollbackCapacity);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(content);
        return adapter.Buffer;
    }

    private static TerminalBuffer BuildScrollbackBuffer(byte[] content)
    {
        TerminalBuffer buffer = BuildBuffer(24, 80, ScrollbackCapacity, content);
        if (buffer.ScrollbackCount != ScrollbackCapacity)
            throw new InvalidOperationException($"Scrollback ring not full after setup: {buffer.ScrollbackCount}.");
        return buffer;
    }

    // The owning buffers stay referenced for as long as their snapshots are used.
    private RenderSnapshot LogSnapshot() => _logSnapshot ??= VisibleBuffer().CaptureRenderSnapshotVisible();

    private RenderSnapshot CjkSnapshot()
    {
        _cjkQuery ??= BenchmarkContent.CjkText(0, 2);
        _cjkBuffer ??= BuildBuffer(GridRows, GridColumns, 0, BenchmarkContent.CjkGrid(GridRows, GridColumns));
        return _cjkSnapshot ??= _cjkBuffer.CaptureRenderSnapshotVisible();
    }

    private TerminalBuffer VisibleBuffer() => _visibleBuffer ??=
        BuildBuffer(GridRows, GridColumns, 0, BenchmarkContent.LogGrid(GridRows, GridColumns));

    private TerminalBuffer Scrollback() => _scrollback ??=
        BuildScrollbackBuffer(BenchmarkContent.ScrollbackLines(ScrollbackFixtureLines, 80));

    private TerminalBuffer ScrollbackCjk() => _scrollbackCjk ??=
        BuildScrollbackBuffer(BenchmarkContent.CjkScrollbackLines(ScrollbackFixtureLines, 80));

    #endregion

    #region Search (visible rows only)

    [Benchmark(Description = "Search: literal over 60x200 visible rows")]
    public int Search_Literal() =>
        SearchEngine.FindMatches(LogSnapshot(), "error", regex: false, matchCase: false).Count;

    [Benchmark(Description = "Search: literal, many matches per row (query e)")]
    public int Search_Literal_ManyMatches() =>
        SearchEngine.FindMatches(LogSnapshot(), "e", regex: false, matchCase: false).Count;

    [Benchmark(Description = "Search: regex over 60x200 visible rows")]
    public int Search_Regex() =>
        SearchEngine.FindMatches(LogSnapshot(), RegexQuery, regex: true, matchCase: false).Count;

    [Benchmark(Description = "Search: wide/CJK visible rows")]
    public int Search_WideChars()
    {
        var snapshot = CjkSnapshot();
        return SearchEngine.FindMatches(snapshot, _cjkQuery!, regex: false, matchCase: false).Count;
    }

    #endregion

    #region Copy

    [Benchmark(Description = "Copy: select all 60x200 visible")]
    public int Copy_AllVisible()
    {
        var buffer = VisibleBuffer();
        _selection.StartSelection(0, 0);
        _selection.UpdateSelection(GridRows - 1, GridColumns - 1);
        return _selection.GetSelectedText(buffer).Length;
    }

    [Benchmark(Description = "Copy: select 10k scrollback lines (80 cols)")]
    public int Copy_Scrollback()
    {
        var buffer = Scrollback();
        _selection.StartSelection(-ScrollbackCapacity, 0);
        _selection.UpdateSelection(-1, 79);
        return _selection.GetSelectedText(buffer).Length;
    }

    [Benchmark(Description = "Copy: select 10k scrollback lines, CJK")]
    public int Copy_Scrollback_Cjk()
    {
        var buffer = ScrollbackCjk();
        _selection.StartSelection(-ScrollbackCapacity, 0);
        _selection.UpdateSelection(-1, 79);
        return _selection.GetSelectedText(buffer).Length;
    }

    #endregion

    #region Row text

    [Benchmark(Description = "Row text: GetScrollbackLine for all 10k rows")]
    public int RowText_Scrollback()
    {
        var buffer = Scrollback();
        int total = 0;
        int count = buffer.ScrollbackCount;
        for (int i = 0; i < count; i++)
            total += buffer.GetScrollbackLine(i).Length;
        return total;
    }

    #endregion
}
