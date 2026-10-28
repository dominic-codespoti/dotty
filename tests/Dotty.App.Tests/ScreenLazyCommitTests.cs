using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// The screen arenas are lazily committed: all-zero bytes are a blank cell, and a dirty extent (at most two
/// physical-row intervals) bounds every row that may hold data. These tests pin that machinery against managed
/// models — a plain ring of rune arrays for <see cref="Screen"/>, and a <c>List&lt;string&gt;</c> of rows for
/// <see cref="TerminalBuffer"/> — so correctness does not depend on a recorded golden run of the old
/// (eagerly cleared) implementation.
/// </summary>
public sealed unsafe class ScreenLazyCommitTests
{
    private const int BytesPerCell = 12;

    // ------------------------------------------------------------------ managed ring model (Screen level)

    private sealed class RingModel
    {
        private readonly int _rows;
        private readonly int _cols;
        private readonly int _total;
        private readonly uint[][] _phys;

        public RingModel(int rows, int cols, int capacity)
        {
            _rows = rows;
            _cols = cols;
            _total = rows + capacity;
            _phys = new uint[_total][];
            for (int i = 0; i < _total; i++)
                _phys[i] = new uint[cols];
        }

        public int Head { get; private set; }

        public uint[] Row(int physical) => _phys[physical];

        private int Phys(int logical) => ((Head + logical) % _total + _total) % _total;

        private bool InWindow(int physical) => ((physical - Head) % _total + _total) % _total < _rows;

        public void Write(int row, int col, uint rune) => _phys[Phys(row)][col] = rune;

        public void ClearRow(int row) => Array.Clear(_phys[Phys(row)]);

        public void ClearColumns(int row, int start, int end)
        {
            for (int c = Math.Max(0, start); c < Math.Min(_cols, end); c++)
                _phys[Phys(row)][c] = 0;
        }

        public void ScrollUp(int lines)
        {
            int n = Math.Min(lines, _rows);
            Head = (Head + n) % _total;
            for (int i = 0; i < n; i++)
                Array.Clear(_phys[(Head + _rows - 1 - i + _total) % _total]);
        }

        public void ScrollDown(int lines)
        {
            int n = Math.Min(lines, _rows);
            Head = ((Head - n) % _total + _total) % _total;
        }

        public void ShiftUp(int top, int bottom, int lines)
        {
            int height = bottom - top + 1;
            if (lines >= height)
            {
                for (int r = top; r <= bottom; r++) ClearRow(r);
                return;
            }

            for (int r = top + lines; r <= bottom; r++)
                Array.Copy(_phys[Phys(r)], _phys[Phys(r - lines)], _cols);
            for (int r = bottom - lines + 1; r <= bottom; r++)
                ClearRow(r);
        }

        public void ShiftDown(int top, int bottom, int lines)
        {
            int height = bottom - top + 1;
            if (lines >= height)
            {
                for (int r = top; r <= bottom; r++) ClearRow(r);
                return;
            }

            for (int r = bottom; r >= top + lines; r--)
                Array.Copy(_phys[Phys(r - lines)], _phys[Phys(r)], _cols);
            for (int r = top; r < top + lines; r++)
                ClearRow(r);
        }

        public void Clear()
        {
            foreach (var row in _phys) Array.Clear(row);
        }

        public void DiscardHistory()
        {
            for (int p = 0; p < _total; p++)
                if (!InWindow(p)) Array.Clear(_phys[p]);
        }
    }

    private static void AssertRingMatches(Screen screen, RingModel model, string context)
    {
        Assert.True(model.Head == screen.Head, $"{context}: head {screen.Head} != model {model.Head}");
        int cols = screen.Columns;
        var hot = (CellHot*)screen.CellsPtr;
        for (int p = 0; p < screen.TotalRows; p++)
        {
            var expected = model.Row(p);
            for (int c = 0; c < cols; c++)
            {
                CellHot cell = hot[p * cols + c];
                if (cell.Rune != expected[c] || cell.StyleId != 0 || cell.PackedFlags != 0)
                    Assert.Fail($"{context}: physical row {p} col {c} holds U+{cell.Rune:X} (flags {cell.PackedFlags}), model has U+{expected[c]:X}");
            }

            var cold = new ReadOnlySpan<byte>((byte*)screen.ColdCellsPtr + (long)p * cols * 4, cols * 4);
            Assert.True(cold.IndexOfAnyExcept((byte)0) < 0, $"{context}: physical row {p} has a non-empty cold cell");
        }
    }

    private static void AssertClean(Screen screen, string context)
    {
        var violations = screen.ValidateInvariants();
        Assert.True(violations.Count == 0, $"{context}: " + string.Join("; ", violations));
    }

    private static void WriteCell(Screen screen, RingModel model, int row, int col, char ch)
    {
        screen.GetCellRef(row, col).Rune = ch;
        screen.UpdateRowMaxCol(row, col);
        screen.MarkRowEnd(row, col);
        model.Write(row, col, ch);
    }

    [Theory]
    [InlineData(5, 7, 11, 1)]
    [InlineData(5, 7, 11, 2)]
    [InlineData(5, 7, 11, 3)]
    [InlineData(3, 5, 0, 4)]
    [InlineData(4, 6, 1, 5)]
    [InlineData(2, 3, 2, 6)]
    [InlineData(8, 5, 40, 7)]
    [InlineData(24, 9, 60, 8)]
    public void RandomOperationsMatchAManagedRingModel(int rows, int cols, int capacity, int seed)
    {
        var random = new Random(seed);
        using var screen = new Screen(rows, cols, capacity);
        var model = new RingModel(rows, cols, capacity);
        AssertRingMatches(screen, model, "initial");
        AssertClean(screen, "initial");

        for (int step = 0; step < 2500; step++)
        {
            int op = random.Next(100);
            string what;
            if (op < 35)
            {
                int row = random.Next(rows), col = random.Next(cols);
                char ch = (char)('a' + random.Next(26));
                WriteCell(screen, model, row, col, ch);
                what = $"write r{row} c{col}";
            }
            else if (op < 55 && rows >= 2)
            {
                int n = 1 + random.Next(rows);
                screen.ScrollUpRegion(0, rows - 1, n);
                model.ScrollUp(n);
                what = $"scroll up {n}";
            }
            else if (op < 65 && rows >= 2)
            {
                int n = 1 + random.Next(rows);
                screen.ScrollDownRegion(0, rows - 1, n);
                model.ScrollDown(n);
                what = $"scroll down {n}";
            }
            else if (op < 70 && rows >= 3)
            {
                int top = random.Next(rows - 1);
                int bottom = top + 1 + random.Next(rows - top - 1);
                int n = 1 + random.Next(bottom - top + 1);
                screen.ShiftRegionUp(top, bottom, n);
                model.ShiftUp(top, bottom, n);
                what = $"shift up [{top},{bottom}] {n}";
            }
            else if (op < 75 && rows >= 3)
            {
                int top = random.Next(rows - 1);
                int bottom = top + 1 + random.Next(rows - top - 1);
                int n = 1 + random.Next(bottom - top + 1);
                screen.ShiftRegionDown(top, bottom, n);
                model.ShiftDown(top, bottom, n);
                what = $"shift down [{top},{bottom}] {n}";
            }
            else if (op < 80)
            {
                int row = random.Next(rows);
                screen.ClearRow(row);
                model.ClearRow(row);
                what = $"clear row {row}";
            }
            else if (op < 86)
            {
                int row = random.Next(rows);
                int start = random.Next(cols), end = start + random.Next(cols - start + 1);
                screen.ClearColumns(row, start, end);
                model.ClearColumns(row, start, end);
                what = $"clear cols r{row} [{start},{end})";
            }
            else if (op < 89)
            {
                screen.Clear();
                model.Clear();
                what = "clear";
            }
            else if (op < 92)
            {
                screen.DiscardHistory();
                model.DiscardHistory();
                what = "discard history";
            }
            else if (rows >= 2)
            {
                int burst = random.Next(1, 3 * (rows + capacity));
                for (int i = 0; i < burst; i++)
                {
                    screen.ScrollUpRegion(0, rows - 1, 1);
                    model.ScrollUp(1);
                    if (i % 4 == 0)
                        WriteCell(screen, model, rows - 1, random.Next(cols), 'z');
                }
                what = $"scroll burst {burst}";
            }
            else
            {
                what = "no-op";
            }

            string context = $"seed {seed} step {step} ({what})";
            AssertRingMatches(screen, model, context);
            AssertClean(screen, context);
            Assert.InRange(screen.DirtyRowCount, rows, screen.TotalRows);
        }
    }

    // ------------------------------------------------------------------ dirty extent bookkeeping

    [Fact]
    public void FreshScreenReservesTheRingButOnlyTheVisibleWindowIsDirty()
    {
        using var screen = new Screen(24, 80, 5000);

        Assert.Equal(24, screen.DirtyRowCount);
        screen.GetDirtyExtents(out int lo0, out int hi0, out int lo1, out int hi1);
        Assert.Equal((0, 24, 0, 0), (lo0, hi0, lo1, hi1));
        Assert.Equal(24L * 80 * BytesPerCell, screen.CommittedBoundBytes);
        Assert.Equal((5000L + 24) * 80 * BytesPerCell, screen.ReservedBytes);
        AssertClean(screen, "fresh");
    }

    [Fact]
    public void ScrollingExtendsTheDirtyExtentOneRowAtATime()
    {
        using var screen = new Screen(24, 80, 5000);

        for (int i = 1; i <= 100; i++)
        {
            screen.ScrollUpRegion(0, 23, 1);
            Assert.Equal(24 + i, screen.DirtyRowCount);
        }

        screen.GetDirtyExtents(out int lo0, out int hi0, out int lo1, out int hi1);
        Assert.Equal((0, 124, 0, 0), (lo0, hi0, lo1, hi1));
        AssertClean(screen, "after 100 scrolls");
    }

    [Fact]
    public void ScrollingPastTheCapacityMakesEveryRowDirtyAndItStaysThatWay()
    {
        using var screen = new Screen(5, 7, 11);

        for (int i = 0; i < 100; i++)
            screen.ScrollUpRegion(0, 4, 1);

        Assert.Equal(16, screen.DirtyRowCount);
        screen.GetDirtyExtents(out int lo0, out int hi0, out int lo1, out int hi1);
        Assert.Equal((0, 16, 0, 0), (lo0, hi0, lo1, hi1));
        AssertClean(screen, "full ring");
    }

    [Fact]
    public void ReverseScrollWrappingBelowRowZeroTracksBothEndsOfTheRing()
    {
        using var screen = new Screen(5, 7, 11);

        screen.ScrollDownRegion(0, 4, 3); // head 0 -> 13: window is [13,16) + [0,2)

        Assert.Equal(13, screen.Head);
        screen.GetDirtyExtents(out int lo0, out int hi0, out int lo1, out int hi1);
        Assert.Equal((0, 5, 13, 16), (lo0, hi0, lo1, hi1));
        Assert.Equal(8, screen.DirtyRowCount);
        AssertClean(screen, "wrapped reverse scroll");
    }

    [Fact]
    public void ClearZeroesTheRingAndShrinksTheExtentToTheVisibleWindow()
    {
        using var screen = new Screen(5, 7, 11);
        var model = new RingModel(5, 7, 11);
        for (int i = 0; i < 8; i++)
        {
            for (int r = 0; r < 5; r++)
                WriteCell(screen, model, r, (i + r) % 7, (char)('a' + i));
            screen.ScrollUpRegion(0, 4, 1);
            model.ScrollUp(1);
        }

        screen.Clear();
        model.Clear();

        AssertRingMatches(screen, model, "after clear");
        Assert.Equal(5, screen.DirtyRowCount);
        screen.GetDirtyExtents(out int lo0, out int hi0, out int lo1, out int hi1);
        Assert.Equal((8, 13, 0, 0), (lo0, hi0, lo1, hi1));
        AssertClean(screen, "after clear");
    }

    [Theory]
    [InlineData(8)]  // window in the middle of the ring
    [InlineData(14)] // window wraps past the end
    [InlineData(0)]  // nothing scrolled yet
    public void DiscardHistoryBlanksEverythingOutsideTheVisibleWindow(int scrolls)
    {
        using var screen = new Screen(5, 7, 11);
        var model = new RingModel(5, 7, 11);
        for (int i = 0; i < scrolls; i++)
        {
            for (int r = 0; r < 5; r++)
                WriteCell(screen, model, r, (i + r) % 7, (char)('a' + i % 26));
            screen.ScrollUpRegion(0, 4, 1);
            model.ScrollUp(1);
        }
        for (int r = 0; r < 5; r++)
            WriteCell(screen, model, r, 0, (char)('A' + r));
        string[] visibleBefore = Enumerable.Range(0, 5).Select(r => VisibleText(screen, r)).ToArray();

        screen.DiscardHistory();
        model.DiscardHistory();

        AssertRingMatches(screen, model, "after discard history");
        Assert.Equal(visibleBefore, Enumerable.Range(0, 5).Select(r => VisibleText(screen, r)).ToArray());
        Assert.Equal(5, screen.DirtyRowCount);
        for (int i = 0; i < 11; i++)
            Assert.Equal(string.Empty, screen.GetScrollbackRow(i));
        AssertClean(screen, "after discard history");
    }

    private static string VisibleText(Screen screen, int row)
    {
        var chars = new char[screen.Columns];
        for (int c = 0; c < chars.Length; c++)
        {
            uint rune = screen.GetCell(row, c).Rune;
            chars[c] = rune == 0 ? ' ' : (char)rune;
        }
        return new string(chars);
    }

    [Fact]
    public void ResetForReuseParksAScreenWithNothingDirty()
    {
        using var screen = new Screen(5, 7, 11);
        var model = new RingModel(5, 7, 11);
        for (int i = 0; i < 20; i++)
        {
            WriteCell(screen, model, 4, i % 7, 'q');
            screen.ScrollUpRegion(0, 4, 1);
            model.ScrollUp(1);
        }

        screen.ResetForReuse(6, 9, 11);

        Assert.Equal(0, screen.DirtyRowCount);
        Assert.Equal(0, screen.CommittedBoundBytes);
        Assert.Equal(6, screen.Rows);
        Assert.Equal(9, screen.Columns);
        Assert.Equal(0, screen.Head);
        AssertClean(screen, "parked");

        // Becoming a reflow destination re-establishes the visible window.
        screen.ResetForReflow(6, 9, 11);
        Assert.Equal(6, screen.DirtyRowCount);
        AssertClean(screen, "reused");
    }

    [Fact]
    public void DisposeIsIdempotentAndReleasesTheAccounting()
    {
        var screen = new Screen(5, 7, 11);
        Assert.True(screen.ReservedBytes > 0);

        screen.Dispose();
        screen.Dispose();

        Assert.Equal(0, screen.ReservedBytes);
        Assert.Equal(0, screen.CommittedBoundBytes);
        Assert.Equal(IntPtr.Zero, screen.CellsPtr);
        Assert.Equal(IntPtr.Zero, screen.ColdCellsPtr);
    }

    [Fact]
    public void ReservationOfAHugeCapacityIsVirtualOnly()
    {
        using var screen = new Screen(24, 80, 50_000);

        Assert.Equal((50_000L + 24) * 80 * BytesPerCell, screen.ReservedBytes);
        Assert.Equal(24L * 80 * BytesPerCell, screen.CommittedBoundBytes);
        screen.ScrollUpRegion(0, 23, 10);
        Assert.Equal(34, screen.DirtyRowCount);
        AssertClean(screen, "huge capacity");
    }

    [Fact]
    public void ArenaSizeBeyondTheIntegerCellLimitFailsCleanly()
    {
        // 100_024 rows x 30_000 columns = 3.0e9 cells: past int.MaxValue, which previously overflowed silently.
        Assert.Throws<OutOfMemoryException>(() => new Screen(24, 30_000, 100_000));
    }

    // ------------------------------------------------------------------ TerminalBuffer level

    private static void WriteLine(TerminalBuffer buffer, string text)
    {
        buffer.CarriageReturn();
        buffer.WriteText(text.AsSpan(), CellAttributes.Default);
        buffer.CarriageReturn();
        buffer.LineFeed();
    }

    /// <summary>A managed picture of a buffer: history rows then exactly <c>rows</c> visible rows.</summary>
    private sealed class RowListModel
    {
        private readonly int _rows;
        private readonly int _capacity;

        public RowListModel(int rows, int capacity)
        {
            _rows = rows;
            _capacity = capacity;
            for (int i = 0; i < rows; i++) Stream.Add(string.Empty);
        }

        public List<string> Stream { get; } = new();
        public int CursorRow { get; private set; }
        public int History => Stream.Count - _rows;

        public void WriteLine(string text)
        {
            Stream[History + CursorRow] = text;
            if (CursorRow == _rows - 1)
            {
                Stream.Add(string.Empty);
                if (History > _capacity)
                    Stream.RemoveAt(0);
            }
            else
            {
                CursorRow++;
            }
        }

        /// <summary>ED 2: the screen and the ring (history included) are blanked; the history count is kept.</summary>
        public void EraseDisplay()
        {
            for (int i = 0; i < Stream.Count; i++) Stream[i] = string.Empty;
            CursorRow = 0;
        }

        /// <summary>CSI 3 J: history is dropped, the screen stays.</summary>
        public void ClearScrollback() => Stream.RemoveRange(0, History);
    }

    private static void AssertBufferMatches(TerminalBuffer buffer, RowListModel model, string context)
    {
        Assert.True(model.History == buffer.ScrollbackCount, $"{context}: scrollback count {buffer.ScrollbackCount} != model {model.History}");
        for (int i = 0; i < model.History; i++)
            Assert.True(model.Stream[i] == buffer.GetScrollbackLine(i).ToString(),
                $"{context}: history line {i} is '{buffer.GetScrollbackLine(i)}', model has '{model.Stream[i]}'");
        for (int r = 0; r < buffer.Rows; r++)
            Assert.True(model.Stream[model.History + r] == buffer.GetRowText(r).TrimEnd(),
                $"{context}: visible row {r} is '{buffer.GetRowText(r).TrimEnd()}', model has '{model.Stream[model.History + r]}'");
        Assert.True(model.CursorRow == buffer.CursorRow, $"{context}: cursor row {buffer.CursorRow} != model {model.CursorRow}");
        var violations = buffer.ValidateInvariants();
        Assert.True(violations.Count == 0, $"{context}: " + string.Join("; ", violations));
    }

    [Theory]
    [InlineData(5, 24, 12, 11)]
    [InlineData(5, 24, 12, 12)]
    [InlineData(3, 16, 4, 13)]
    [InlineData(8, 30, 100, 14)]
    public void LinesEraseClearScrollbackAndAlternateScreenMatchAManagedRowList(int rows, int columns, int capacity, int seed)
    {
        var random = new Random(seed);
        using var buffer = new TerminalBuffer(rows, columns, capacity);
        var model = new RowListModel(rows, capacity);
        int counter = 0;

        for (int step = 0; step < 600; step++)
        {
            int op = random.Next(100);
            string what;
            if (op < 72)
            {
                string text = $"L{counter++}-{new string('x', random.Next(0, columns / 2))}";
                WriteLine(buffer, text);
                model.WriteLine(text);
                what = "line";
            }
            else if (op < 82)
            {
                buffer.EraseDisplay(2);
                model.EraseDisplay();
                what = "ED 2";
            }
            else if (op < 90)
            {
                buffer.ClearScrollback();
                model.ClearScrollback();
                what = "CSI 3 J";
            }
            else
            {
                buffer.SetAlternateScreen(true);
                int alternateLines = random.Next(0, 3 * rows);
                for (int i = 0; i < alternateLines; i++)
                    WriteLine(buffer, $"alt{i}");
                buffer.SetAlternateScreen(false);
                what = $"alternate screen with {alternateLines} lines";
            }

            AssertBufferMatches(buffer, model, $"seed {seed} step {step} ({what})");
        }
    }

    [Fact]
    public void EraseDisplayKeepsTheScrollbackCountButBlanksTheHistory()
    {
        using var buffer = new TerminalBuffer(5, 20, 50);
        for (int i = 0; i < 30; i++)
            WriteLine(buffer, $"line-{i}");
        Assert.Equal(26, buffer.ScrollbackCount);

        buffer.EraseDisplay(2);

        Assert.Equal(26, buffer.ScrollbackCount);
        for (int i = 0; i < 26; i++)
            Assert.Equal(string.Empty, buffer.GetScrollbackLine(i).ToString());
        for (int r = 0; r < 5; r++)
            Assert.Equal(string.Empty, buffer.GetRowText(r).TrimEnd());
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(5, buffer.ActiveBuffer.DirtyRowCount);
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void ClearScrollbackDropsTheHistoryAndKeepsTheScreen()
    {
        using var buffer = new TerminalBuffer(5, 20, 50);
        for (int i = 0; i < 30; i++)
            WriteLine(buffer, $"line-{i}");
        string[] visible = Enumerable.Range(0, 5).Select(r => buffer.GetRowText(r).TrimEnd()).ToArray();

        buffer.ClearScrollback();

        Assert.Equal(0, buffer.ScrollbackCount);
        Assert.Equal(visible, Enumerable.Range(0, 5).Select(r => buffer.GetRowText(r).TrimEnd()).ToArray());
        Assert.Equal(5, buffer.ActiveBuffer.DirtyRowCount);
        Assert.Empty(buffer.ValidateInvariants());

        // Reverse scrolling after the clear inserts blank lines instead of resurrecting old history.
        buffer.SetCursor(0, 0);
        buffer.ReverseIndex();
        Assert.Equal(string.Empty, buffer.GetRowText(0).TrimEnd());
        Assert.Equal(visible[0], buffer.GetRowText(1).TrimEnd());
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void ReverseIndexRevealsTheMostRecentHistoryLine()
    {
        using var buffer = new TerminalBuffer(5, 20, 50);
        for (int i = 0; i < 30; i++)
            WriteLine(buffer, $"line-{i}");
        Assert.Equal(26, buffer.ScrollbackCount);

        buffer.SetCursor(0, 0);
        buffer.ReverseIndex();

        Assert.Equal(25, buffer.ScrollbackCount);
        Assert.Equal("line-24", buffer.GetScrollbackLine(24).ToString());
        string[] expected = { "line-25", "line-26", "line-27", "line-28", "line-29" };
        Assert.Equal(expected, Enumerable.Range(0, 5).Select(r => buffer.GetRowText(r).TrimEnd()).ToArray());
        Assert.Empty(buffer.ValidateInvariants());
    }

    [Fact]
    public void ResizeReflowsHistoryIntoTheRingTailAndParksTheSpare()
    {
        using var buffer = new TerminalBuffer(5, 20, 50);
        int written = 0;
        for (; written < 30; written++)
            WriteLine(buffer, $"line-{written}");
        Assert.Equal(26, buffer.ScrollbackCount);

        buffer.Resize(5, 30);

        Assert.Equal(26, buffer.ScrollbackCount);
        for (int i = 0; i < 26; i++)
            Assert.Equal($"line-{i}", buffer.GetScrollbackLine(i).ToString());
        string[] visible = { "line-26", "line-27", "line-28", "line-29", string.Empty };
        Assert.Equal(visible, Enumerable.Range(0, 5).Select(r => buffer.GetRowText(r).TrimEnd()).ToArray());
        // Destination: visible window + 26 history rows at the end of the ring (55 rows in total).
        Assert.Equal(5 + 26, buffer.ActiveBuffer.DirtyRowCount);
        AssertSparesAreParked(buffer);
        Assert.Empty(buffer.ValidateInvariants());

        // Keep scrolling past the capacity: the ring wraps over the reflowed tail.
        buffer.SetCursor(4, 0);
        for (; written < 70; written++)
            WriteLine(buffer, $"line-{written}");
        Assert.Equal(50, buffer.ScrollbackCount);
        for (int i = 0; i < 50; i++)
            Assert.Equal($"line-{16 + i}", buffer.GetScrollbackLine(i).ToString());
        Assert.Equal(buffer.ActiveBuffer.TotalRows, buffer.ActiveBuffer.DirtyRowCount);
        Assert.Empty(buffer.ValidateInvariants());

        // A second resize reuses the parked spare.
        buffer.Resize(5, 20);

        Assert.Equal(50, buffer.ScrollbackCount);
        for (int i = 0; i < 50; i++)
            Assert.Equal($"line-{16 + i}", buffer.GetScrollbackLine(i).ToString());
        string[] tail = { "line-66", "line-67", "line-68", "line-69", string.Empty };
        Assert.Equal(tail, Enumerable.Range(0, 5).Select(r => buffer.GetRowText(r).TrimEnd()).ToArray());
        AssertSparesAreParked(buffer);
        Assert.Empty(buffer.ValidateInvariants());
    }

    private static void AssertSparesAreParked(TerminalBuffer buffer)
    {
        var spares = buffer.ScreenManagerForTests.SpareScreens;
        Assert.NotEmpty(spares);
        foreach (var spare in spares)
        {
            Assert.Equal(0, spare.DirtyRowCount);
            Assert.Equal(0, spare.CommittedBoundBytes);
            Assert.Empty(spare.ValidateInvariants());
        }
    }

    [Fact]
    public void LeavingTheAlternateScreenReturnsItsPagesAndKeepsTheMainScreen()
    {
        using var buffer = new TerminalBuffer(5, 20, 50);
        for (int i = 0; i < 30; i++)
            WriteLine(buffer, $"line-{i}");
        string[] mainBefore = Enumerable.Range(0, 5).Select(r => buffer.GetRowText(r).TrimEnd()).ToArray();
        string[] historyBefore = Enumerable.Range(0, buffer.ScrollbackCount).Select(i => buffer.GetScrollbackLine(i).ToString()).ToArray();

        buffer.SetAlternateScreen(true);
        for (int i = 0; i < 40; i++)
            WriteLine(buffer, $"alt-{i}");
        var alternate = buffer.ScreenManagerForTests.Alternate!;
        Assert.True(alternate.DirtyRowCount > 5);

        buffer.SetAlternateScreen(false);

        Assert.Equal(5, alternate.DirtyRowCount);
        Assert.Empty(alternate.ValidateInvariants());
        for (int p = 0; p < alternate.TotalRows; p++)
            for (int c = 0; c < alternate.Columns; c++)
                Assert.Equal(0u, ((CellHot*)alternate.CellsPtr)[p * alternate.Columns + c].Rune);
        Assert.Equal(mainBefore, Enumerable.Range(0, 5).Select(r => buffer.GetRowText(r).TrimEnd()).ToArray());
        Assert.Equal(historyBefore, Enumerable.Range(0, buffer.ScrollbackCount).Select(i => buffer.GetScrollbackLine(i).ToString()).ToArray());
        Assert.Empty(buffer.ValidateInvariants());

        // Re-entering shows a blank alternate screen.
        buffer.SetAlternateScreen(true);
        for (int r = 0; r < 5; r++)
            Assert.Equal(string.Empty, buffer.GetRowText(r).TrimEnd());
        buffer.SetAlternateScreen(false);
    }
}
