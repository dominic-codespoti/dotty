using System;
using System.Buffers;
using Dotty.Abstractions.Config;
namespace Dotty.Terminal.Adapter;

/// <summary>
/// Immutable, caller-owned copy of the buffer state the renderer needs.
/// Captured under <c>SyncRoot</c> (one bounded memcpy hold) and rasterized
/// outside the lock, so the UI thread never blocks the PTY writer for the
/// whole raster and the raster never races a partial parse. Large arrays are
/// rented from the shared pool and returned via <see cref="Dispose"/>.
/// </summary>
public sealed class RenderSnapshot : IRenderSource, IDisposable
{
    public CellHot[] Cells = Array.Empty<CellHot>();
    public ColdCell[] Cold = Array.Empty<ColdCell>();
    public int[] RowMap = Array.Empty<int>();        // physical row per visible logical row
    public int[] RowMaxCol = Array.Empty<int>();     // per physical row
    public int Head;
    public int TotalRows;
    public ulong[] RowGenerationsArray = Array.Empty<ulong>(); // per visible logical row

    /// <summary>Per-visible-row identity generations (bump-only).</summary>
    public ReadOnlySpan<ulong> RowGenerations => RowGenerationsArray;
    public CellAttributes[] Styles = Array.Empty<CellAttributes>();
    public int ScrollbackCount { get; set; }
    public int Rows { get; set; }
    public int Columns { get; set; }
    public bool IsAlternateScreenActive { get; set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public ulong GlobalGeneration;
    public TerminalCursorShape CursorShape { get; set; } = TerminalCursorShape.Block;
    public bool CursorBlinking { get; set; } = true;

    /// <summary>Materialized scrollback text for the visible range, newest = 0.</summary>
    public string[] ScrollbackText = Array.Empty<string>();
    /// <summary>Scrollback row index of <see cref="ScrollbackText"/>[0] (0 = newest).</summary>
    public int CapturedSbStart;

    /// <summary>
    /// Per-visible-row offset into the compact <see cref="Cells"/>/<see cref="Cold"/>
    /// arrays (visible-row capture only). Empty when the full-arena capture was used.
    /// </summary>
    public int[] RowOffsets = Array.Empty<int>();

    /// <summary>
    /// Compact per-frame capture: copies only the visible rows' cell slices
    /// (plus metadata) instead of the whole scrollback arena. The raster reads
    /// ~41 rows x columns; a full-arena memcpy (12 MB at 5k scrollback) is
    /// pure overhead per content frame.
    /// </summary>
    public static RenderSnapshot CaptureVisible(
        Screen screen,
        ulong[] rowGenerations,
        CellAttributes[] styles,
        int scrollbackCount,
        bool altActive,
        int cursorRow,
        int cursorCol,
        TerminalCursorShape cursorShape = TerminalCursorShape.Block,
        bool cursorBlinking = true,
        int scrollOffset = 0)
    {
        int adjustedCursorRow = scrollOffset > 0 ? (cursorRow + scrollOffset) : cursorRow;
        var snap = new RenderSnapshot
        {
            Rows = screen.Rows,
            Columns = screen.Columns,
            TotalRows = screen.TotalRows,
            Head = screen.Head,
            ScrollbackCount = scrollbackCount,
            IsAlternateScreenActive = altActive,
            CursorRow = adjustedCursorRow,
            CursorCol = cursorCol,
            CursorShape = cursorShape,
            CursorBlinking = cursorBlinking,
            Styles = styles,
        };
        int rows = screen.Rows;
        int cols = screen.Columns;
        int visibleCellCount = rows * cols;
        snap.Cells = ArrayPool<CellHot>.Shared.Rent(visibleCellCount);
        snap.Cold = ArrayPool<ColdCell>.Shared.Rent(visibleCellCount);
        snap.RowOffsets = ArrayPool<int>.Shared.Rent(Math.Max(1, rows));
        snap.RowMap = ArrayPool<int>.Shared.Rent(Math.Max(1, rows));
        snap.RowGenerationsArray = ArrayPool<ulong>.Shared.Rent(Math.Max(1, rows));

        unsafe
        {
            fixed (CellHot* pCells = snap.Cells)
            fixed (ColdCell* pCold = snap.Cold)
            {
                var dstHot = (byte*)pCells;
                var dstCold = (byte*)pCold;
                long hotRowBytes = (long)cols * sizeof(CellHot);
                long coldRowBytes = (long)cols * sizeof(ColdCell);
                for (int r = 0; r < rows; r++)
                {
                    int logicalRow = r - scrollOffset;
                    int pRow = screen.GetPhysicalRow(logicalRow);
                    snap.RowMap[r] = pRow;
                    snap.RowOffsets[r] = r * cols;
                    System.Buffer.MemoryCopy(
                        (void*)(screen.CellsPtr + (nint)pRow * cols * sizeof(CellHot)),
                        dstHot + (nint)r * cols * sizeof(CellHot),
                        (ulong)hotRowBytes, (ulong)hotRowBytes);
                    System.Buffer.MemoryCopy(
                        (void*)(screen.ColdCellsPtr + (nint)pRow * cols * sizeof(ColdCell)),
                        dstCold + (nint)r * cols * sizeof(ColdCell),
                        (ulong)coldRowBytes, (ulong)coldRowBytes);
                }
            }
        }

        int copyGens = Math.Min(rowGenerations.Length, rows);
        Array.Copy(rowGenerations, snap.RowGenerationsArray, copyGens);

        return snap;
    }

    public static RenderSnapshot Capture(
        Screen screen,
        ulong[] rowGenerations,
        CellAttributes[] styles,
        int scrollbackCount,
        bool altActive,
        int cursorRow,
        int cursorCol,
        TerminalCursorShape cursorShape = TerminalCursorShape.Block,
        bool cursorBlinking = true)
    {
        var snap = new RenderSnapshot
        {
            Rows = screen.Rows,
            Columns = screen.Columns,
            TotalRows = screen.TotalRows,
            Head = screen.Head,
            ScrollbackCount = scrollbackCount,
            IsAlternateScreenActive = altActive,
            CursorRow = cursorRow,
            CursorCol = cursorCol,
            CursorShape = cursorShape,
            CursorBlinking = cursorBlinking,
            Styles = styles,
        };

        int cellCount = screen.TotalRows * screen.Columns;
        snap.Cells = ArrayPool<CellHot>.Shared.Rent(cellCount);
        snap.Cold = ArrayPool<ColdCell>.Shared.Rent(cellCount);
        snap.RowMaxCol = ArrayPool<int>.Shared.Rent(screen.TotalRows);
        snap.RowMap = ArrayPool<int>.Shared.Rent(screen.Rows);
        snap.RowGenerationsArray = ArrayPool<ulong>.Shared.Rent(Math.Max(1, screen.Rows));

        unsafe
        {
            fixed (CellHot* pCells = snap.Cells)
            fixed (ColdCell* pCold = snap.Cold)
            {
                System.Buffer.MemoryCopy((void*)screen.CellsPtr, pCells, (long)cellCount * sizeof(CellHot), (long)cellCount * sizeof(CellHot));
                System.Buffer.MemoryCopy((void*)screen.ColdCellsPtr, pCold, (long)cellCount * sizeof(ColdCell), (long)cellCount * sizeof(ColdCell));
            }
        }

        Array.Copy(screen.RowMaxCol, snap.RowMaxCol, screen.TotalRows);
        for (int row = 0; row < screen.Rows; row++)
            snap.RowMap[row] = screen.GetPhysicalRow(row);
        int copyGens = Math.Min(rowGenerations.Length, screen.Rows);
        Array.Copy(rowGenerations, snap.RowGenerationsArray, copyGens);

        return snap;
    }

    public void Dispose()
    {
        if (Cells.Length > 0) ArrayPool<CellHot>.Shared.Return(Cells);
        if (Cold.Length > 0) ArrayPool<ColdCell>.Shared.Return(Cold);
        if (RowMaxCol.Length > 0) ArrayPool<int>.Shared.Return(RowMaxCol);
        if (RowMap.Length > 0) ArrayPool<int>.Shared.Return(RowMap);
        if (RowGenerationsArray.Length > 0) ArrayPool<ulong>.Shared.Return(RowGenerationsArray);
        if (RowOffsets.Length > 0) ArrayPool<int>.Shared.Return(RowOffsets);
        Cells = Array.Empty<CellHot>();
        Cold = Array.Empty<ColdCell>();
        RowMap = Array.Empty<int>();
        RowMaxCol = Array.Empty<int>();
        RowGenerationsArray = Array.Empty<ulong>();
        RowOffsets = Array.Empty<int>();
        ScrollbackText = Array.Empty<string>();
    }

    public ReadOnlySpan<CellHot> GetRowCells(int row)
    {
        if (row < 0 || row >= Rows) return default;
        if (RowOffsets.Length > 0)
            return new ReadOnlySpan<CellHot>(Cells, RowOffsets[row], Columns);
        int pRow = RowMap[row];
        return new ReadOnlySpan<CellHot>(Cells, pRow * Columns, Columns);
    }

    public ReadOnlySpan<ColdCell> GetRowColdCells(int row)
    {
        if (row < 0 || row >= Rows) return default;
        if (RowOffsets.Length > 0)
            return new ReadOnlySpan<ColdCell>(Cold, RowOffsets[row], Columns);
        int pRow = RowMap[row];
        return new ReadOnlySpan<ColdCell>(Cold, pRow * Columns, Columns);
    }

    public ulong GetRowGeneration(int row)
    {
        if (row < 0 || row >= RowGenerationsArray.Length) return 0;
        return RowGenerationsArray[row];
    }

    public ref readonly CellAttributes GetStyle(ushort styleId)
    {
        if (styleId >= Styles.Length)
            return ref CellAttributes.Default;
        return ref Styles[styleId];
    }

    public string GetScrollbackLineText(int index)
    {
        int offset = index - CapturedSbStart;
        if (offset < 0 || offset >= ScrollbackText.Length)
            return string.Empty;
        return ScrollbackText[offset];
    }

    /// <summary>
    /// Grapheme-aware text of one scrollback line from the copied arena
    /// (newest = 0). Used by search so it never touches the live buffer.
    /// </summary>
    public string GetScrollbackRowText(int scrollbackIndex)
    {
        if (scrollbackIndex < 0 || scrollbackIndex >= ScrollbackCount) return string.Empty;
        int total = TotalRows;
        int pRow = (Head - 1 - scrollbackIndex + total * 2) % total;
        int maxCol = RowMaxCol[pRow];
        if (maxCol < 0) return string.Empty;
        return BuildRowText(pRow, maxCol);
    }

    /// <summary>
    /// Grapheme-aware text of one visible row from the copied arena.
    /// </summary>
    public string GetVisibleRowText(int row)
    {
        if (row < 0 || row >= Rows || Columns <= 0) return string.Empty;
        // CaptureVisible compacts rows and records their offsets; the full
        // capture keeps the physical ring map and uses arena row indices.
        int rowIndex = RowOffsets.Length > 0 ? RowOffsets[row] / Columns : RowMap[row];
        return BuildRowText(rowIndex, Columns - 1);
    }

    private string BuildRowText(int pRow, int maxCol)
    {
        int offset = pRow * Columns;
        using var sb = ZStr.CreateStringBuilder(maxCol + 1);
        for (int i = 0; i <= maxCol; i++)
        {
            var cell = Cells[offset + i];
            if (cell.IsContinuation || cell.Rune == 0)
            {
                sb.Append(' ');
                continue;
            }
            var cold = Cold[offset + i];
            var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
            if (string.IsNullOrEmpty(grapheme))
                sb.Append(' ');
            else
                sb.Append(grapheme);
        }
        return sb.ToString();
    }

    public string GetDebugInfo()
        => $"snapshot rows={Rows} cols={Columns} sb={ScrollbackCount} gen={GlobalGeneration} alt={IsAlternateScreenActive}";
}
