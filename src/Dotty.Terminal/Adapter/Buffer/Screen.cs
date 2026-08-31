using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

public unsafe partial class Screen : IDisposable
{
    public IntPtr CellsPtr => _cellsPtr;
    public IntPtr ColdCellsPtr => _coldCellsPtr;
    private IntPtr _cellsPtr;
    private IntPtr _coldCellsPtr;
    private int _cellCount;
    public int Head => _head;
    private int _head;
    public int ScrollbackCapacity => _scrollbackCapacity;
    private readonly int _scrollbackCapacity;
    public int TotalRows => _scrollbackCapacity + Rows;

    public int[] RowMaxCol => _rowMaxCol;
    private int[] _rowMaxCol;
    // Per-physical-row flag: tracks whether any cell has hyperlinks or graphemes.
    // Allows BufferTextWriter to skip the per-cell cold-reset loop entirely on clean rows.
    public bool[] RowColdFlags => _rowColdFlags;
    private bool[] _rowColdFlags;
    public bool[] RowContinuesPrevious { get; }
    public int[] RowEndCol { get; }

    public int GetRowMaxCol(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return -1;
        return _rowMaxCol[GetPhysicalRow(logicalRow)];
    }

    public void UpdateRowMaxCol(int logicalRow, int col)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        int pRow = GetPhysicalRow(logicalRow);
        if (col > _rowMaxCol[pRow])
            _rowMaxCol[pRow] = col;
    }

    public void ResetRowMaxCol(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        _rowMaxCol[GetPhysicalRow(logicalRow)] = -1;
    }
 
    public int GetRowEndCol(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return -1;
        return RowEndCol[GetPhysicalRow(logicalRow)];
    }

    public bool GetRowContinuesPrevious(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return false;
        return RowContinuesPrevious[GetPhysicalRow(logicalRow)];
    }

    internal void MarkRowEnd(int logicalRow, int col)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        int pRow = GetPhysicalRow(logicalRow);
        if (col > RowEndCol[pRow])
            RowEndCol[pRow] = Math.Min(col, Columns - 1);
    }

    internal void SetRowContinuation(int logicalRow, bool continues)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        RowContinuesPrevious[GetPhysicalRow(logicalRow)] = continues;
    }

    internal void ClearRowContinuation(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        RowContinuesPrevious[GetPhysicalRow(logicalRow)] = false;
    }

    public void MarkRender() { }

    public int Rows { get; private set; }
    public int Columns { get; private set; }

    public Screen(int rows, int columns, int scrollbackCapacity = 10000)
    {
        Rows = rows;
        Columns = columns;
        _scrollbackCapacity = scrollbackCapacity;
        _head = 0;
        int total = scrollbackCapacity + rows;
        _cellCount = total * columns;
        _cellsPtr = Marshal.AllocHGlobal(_cellCount * Unsafe.SizeOf<CellHot>());
        _coldCellsPtr = Marshal.AllocHGlobal(_cellCount * Unsafe.SizeOf<ColdCell>());
        new Span<CellHot>((void*)_cellsPtr, _cellCount).Clear();
        var coldSpan = new Span<ColdCell>((void*)_coldCellsPtr, _cellCount);
        for (int i = 0; i < _cellCount; i++)
            coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
        _rowMaxCol = new int[total];
        Array.Fill(_rowMaxCol, -1);
        _rowColdFlags = new bool[total];
        RowContinuesPrevious = new bool[total];
        RowEndCol = new int[total];
        Array.Fill(RowEndCol, -1);
    }

    public void Dispose()
    {
        if (_cellsPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_cellsPtr);
            _cellsPtr = IntPtr.Zero;
        }
        if (_coldCellsPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_coldCellsPtr);
            _coldCellsPtr = IntPtr.Zero;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ref T UnsafeAsRef<T>(IntPtr ptr, int index) where T : unmanaged
        => ref ((T*)ptr)[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetPhysicalRow(int logicalRow)
    {
        int idx = _head + logicalRow;
        if (idx < 0 || idx >= _scrollbackCapacity + Rows)
            idx = (idx % (_scrollbackCapacity + Rows) + (_scrollbackCapacity + Rows)) % (_scrollbackCapacity + Rows);
        return idx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref CellHot GetCellRef(int logicalRow, int col)
    {
        return ref UnsafeAsRef<CellHot>(_cellsPtr, GetPhysicalRow(logicalRow) * Columns + col);
    }

    /// <summary>
    /// Zero-copy read-only view of one logical row's hot cells. Bounds-checked
    /// once per row; the renderer's classification uses this instead of
    /// per-cell <see cref="GetCell(int,int)"/> (which re-checks bounds and runs
    /// the mutating continuation-repair on every call).
    /// </summary>
    public ReadOnlySpan<CellHot> GetRowCells(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return default;
        int pRow = GetPhysicalRow(logicalRow);
        return new ReadOnlySpan<CellHot>((void*)(_cellsPtr + (nint)pRow * Columns * Unsafe.SizeOf<CellHot>()), Columns);
    }

    /// <summary>
    /// Zero-copy read-only view of one logical row's cold cells.
    /// </summary>
    public ReadOnlySpan<ColdCell> GetRowColdCells(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return default;
        int pRow = GetPhysicalRow(logicalRow);
        return new ReadOnlySpan<ColdCell>((void*)(_coldCellsPtr + (nint)pRow * Columns * Unsafe.SizeOf<ColdCell>()), Columns);
    }

    public CellHot GetCell(int logicalRow, int col)
    {
        if (logicalRow < 0 || logicalRow >= Rows || col < 0 || col >= Columns)
        {
            var c = new CellHot();
            c.Rune = 32;
            return c;
        }

        int pRow = GetPhysicalRow(logicalRow);
        int offset = pRow * Columns;
        ref var cell = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + col);
        if (cell.IsContinuation && cell.Rune != 0)
        {
            cell.Rune = 0;
        }
        if (!cell.IsContinuation && cell.Rune != 0 && cell.Width > 1)
        {
            int w = Math.Max(1, (int)cell.Width);
            bool missing = false;
            for (int i = 1; i < w; i++)
            {
                int cc = col + i;
                if (cc >= Columns) { missing = true; break; }
                var cont = UnsafeAsRef<CellHot>(_cellsPtr, offset + cc);
                if (!cont.IsContinuation) { missing = true; break; }
            }
            if (missing)
            {
                for (int i = 1; i < w; i++)
                {
                    int cc = col + i;
                    if (cc >= Columns) break;
                    ref var cont = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + cc);
                    cont.Reset();
                    cont.IsContinuation = true;
                }
            }
        }
        return UnsafeAsRef<CellHot>(_cellsPtr, offset + col);
    }

    public ColdCell GetColdCell(int logicalRow, int col)
    {
        if (logicalRow < 0 || logicalRow >= Rows || col < 0 || col >= Columns)
            return default;
        return UnsafeAsRef<ColdCell>(_coldCellsPtr, GetPhysicalRow(logicalRow) * Columns + col);
    }

    public ref ColdCell GetColdCellRef(int logicalRow, int col)
    {
        return ref UnsafeAsRef<ColdCell>(_coldCellsPtr, GetPhysicalRow(logicalRow) * Columns + col);
    }

    public void SetColdHyperlink(int logicalRow, int col, ushort hyperlinkId)
    {
        int pRow = GetPhysicalRow(logicalRow);
        int offset = pRow * Columns + col;
        UnsafeAsRef<ColdCell>(_coldCellsPtr, offset).HyperlinkId = hyperlinkId;
        UnsafeAsRef<CellHot>(_cellsPtr, offset).HasHyperlink = hyperlinkId != 0;
        if (hyperlinkId != 0) _rowColdFlags[pRow] = true;
    }

    public void SetColdGraphemeIndex(int logicalRow, int col, short graphemeIndex)
    {
        int pRow = GetPhysicalRow(logicalRow);
        int offset = pRow * Columns + col;
        UnsafeAsRef<ColdCell>(_coldCellsPtr, offset).GraphemeIndex = graphemeIndex;
        UnsafeAsRef<CellHot>(_cellsPtr, offset).HasGrapheme = graphemeIndex > 0;
        if (graphemeIndex >= 0) _rowColdFlags[pRow] = true;
    }

    public CellHot[] ExtractRow(int logicalRow)
    {
        var result = new CellHot[Columns];
        if (logicalRow >= 0 && logicalRow < Rows)
        {
            int offset = GetPhysicalRow(logicalRow) * Columns;
            int byteCount = Columns * Unsafe.SizeOf<CellHot>();
            fixed (CellHot* pResult = result)
            {
                System.Buffer.MemoryCopy(
                    (void*)(_cellsPtr + offset * Unsafe.SizeOf<CellHot>()),
                    pResult,
                    byteCount,
                    byteCount);
            }
        }
        else
        {
            for (int i = 0; i < Columns; i++)
                result[i] = new CellHot();
        }
        return result;
    }

    public string GetScrollbackRow(int scrollbackIndex)
    {
        int total = _scrollbackCapacity + Rows;
        int pRow = (_head - 1 - scrollbackIndex + total * 2) % total;
        int maxCol = _rowMaxCol[pRow];
        int endCol = RowEndCol[pRow] >= 0 ? RowEndCol[pRow] : maxCol;

        if (endCol < 0) return string.Empty;

        int offset = pRow * Columns;
        using var sb = ZStr.CreateStringBuilder(endCol + 1);
        for (int i = 0; i <= endCol; i++)
        {
            ref var cell = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + i);
            if (cell.IsContinuation || cell.Rune == 0)
            {
                sb.Append(' ');
                continue;
            }

            // Resolve through the cold cell so multi-char graphemes are not
            // reduced to their first rune (or U+FFFD for supplementary planes).
            ref var cold = ref UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + i);
            var grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex);
            if (string.IsNullOrEmpty(grapheme))
                sb.Append(' ');
            else
                sb.Append(grapheme);
        }
        return sb.ToString();
    }

    public int GetScrollbackRowLength(int scrollbackIndex)
    {
        int total = _scrollbackCapacity + Rows;
        int pRow = (_head - 1 - scrollbackIndex + total * 2) % total;
        int endCol = RowEndCol[pRow] >= 0 ? RowEndCol[pRow] : _rowMaxCol[pRow];
        return Math.Max(0, endCol + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearPhysicalRow(int physicalRow)
    {
        new Span<CellHot>((void*)(_cellsPtr + physicalRow * Columns * Unsafe.SizeOf<CellHot>()), Columns).Clear();
        // Span.Fill vectorizes (4-byte struct, constant pattern) — the
        // per-element loop measured 195ms per 500K-line flood, ~2x the fill.
        new Span<ColdCell>((void*)(_coldCellsPtr + physicalRow * Columns * Unsafe.SizeOf<ColdCell>()), Columns)
            .Fill(new ColdCell { GraphemeIndex = -1 });
        _rowMaxCol[physicalRow] = -1;
        _rowColdFlags[physicalRow] = false;
        RowContinuesPrevious[physicalRow] = false;
        RowEndCol[physicalRow] = -1;
    }
 
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyRowMetadata(int destinationPhysicalRow, int sourcePhysicalRow)
    {
        _rowMaxCol[destinationPhysicalRow] = _rowMaxCol[sourcePhysicalRow];
        _rowColdFlags[destinationPhysicalRow] = _rowColdFlags[sourcePhysicalRow];
        RowContinuesPrevious[destinationPhysicalRow] = RowContinuesPrevious[sourcePhysicalRow];
        RowEndCol[destinationPhysicalRow] = RowEndCol[sourcePhysicalRow];
    }

    public void ClearRow(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        ClearPhysicalRow(GetPhysicalRow(logicalRow));
    }

    public void Clear()
    {
        new Span<CellHot>((void*)_cellsPtr, _cellCount).Clear();
        var coldSpan = new Span<ColdCell>((void*)_coldCellsPtr, _cellCount);
        for (int i = 0; i < _cellCount; i++)
            coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
        Array.Fill(_rowMaxCol, -1);
        Array.Fill(_rowColdFlags, false);
        Array.Fill(RowContinuesPrevious, false);
        Array.Fill(RowEndCol, -1);
    }

    public void RecalculateRowMaxCol(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        int pRow = GetPhysicalRow(logicalRow);
        int offset = pRow * Columns;
        int maxCol = -1;
        for (int j = Columns - 1; j >= 0; j--)
        {
            var cell = UnsafeAsRef<CellHot>(_cellsPtr, offset + j);
            if (!cell.IsContinuation && cell.Rune != 0 && cell.Rune != 32)
            {
                maxCol = j;
                break;
            }
        }
        _rowMaxCol[pRow] = maxCol;
    }
 
    public void RecalculateRowEndCol(int logicalRow)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        int pRow = GetPhysicalRow(logicalRow);
        int offset = pRow * Columns;
        int end = -1;
        for (int col = Columns - 1; col >= 0; col--)
        {
            ref var cell = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + col);
            ref var cold = ref UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + col);
            if (cell.Rune != 0 || cell.PackedFlags != 0 || cell.StyleId != 0
                || cold.HyperlinkId != 0 || cold.GraphemeIndex >= 0)
            {
                end = col;
                break;
            }
        }
        RowEndCol[pRow] = end;
    }

    /// <summary>
    /// Scans every physical row (visible + scrollback ring) for invariant violations:
    /// 1. Continuation cells carry no rune.
    /// 2. Every width-2 base cell has its continuation cells set.
    /// 3. <see cref="RowColdFlags"/> is never false while cold metadata
    ///    (hyperlink / grapheme index) is present — the property the bulk
    ///    ASCII writer relies on to skip cold-cell cleanup.
    /// 4. <see cref="RowMaxCol"/> is an upper bound on the row's last
    ///    non-space content column.
    /// 5. <see cref="RowEndCol"/> bounds all written cell metadata.
    /// 6. Continuation-row metadata never points at an orphan row.
    /// Test/debug aid only; O(rows × columns), never called on the live path.
    /// </summary>
    public List<string> ValidateInvariants()
    {
        var violations = new List<string>();
        int total = TotalRows;
        for (int p = 0; p < total; p++)
        {
            int offset = p * Columns;
            bool rowHasCold = false;
            int lastContentCol = -1;
            int lastDataCol = -1;
            for (int c = 0; c < Columns; c++)
            {
                ref var cell = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + c);
                ref var cold = ref UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + c);
                if (cold.HyperlinkId != 0 || cold.GraphemeIndex >= 0)
                    rowHasCold = true;
                if (cell.Rune != 0 || cell.PackedFlags != 0 || cell.StyleId != 0
                    || cold.HyperlinkId != 0 || cold.GraphemeIndex >= 0)
                    lastDataCol = c;
                if (cell.IsContinuation)
                {
                    if (cell.Rune != 0)
                        violations.Add($"phys row {p} col {c}: continuation carries Rune=0x{cell.Rune:X}");
                }
                else if (cell.Rune != 0 && cell.Rune != 32)
                {
                    lastContentCol = c;
                    int w = Math.Max(1, (int)cell.Width);
                    for (int i = 1; i < w; i++)
                    {
                        int cc = c + i;
                        if (cc >= Columns)
                        {
                            violations.Add($"phys row {p} col {c}: width {w} exceeds row bounds");
                            break;
                        }
                        if (!UnsafeAsRef<CellHot>(_cellsPtr, offset + cc).IsContinuation)
                            violations.Add($"phys row {p} col {c}: width {w} missing continuation at col {cc}");
                    }
                }
            }
            if (RowEndCol[p] < -1 || RowEndCol[p] >= Columns)
                violations.Add($"phys row {p}: RowEndCol {RowEndCol[p]} is outside row bounds");
            if (RowEndCol[p] >= 0 && lastDataCol > RowEndCol[p])
                violations.Add($"phys row {p}: RowEndCol {RowEndCol[p]} below data column {lastDataCol}");
            if (RowContinuesPrevious[p])
            {
                int previous = (p + total - 1) % total;
                if (RowEndCol[p] < 0 || RowEndCol[previous] < 0)
                    violations.Add($"phys row {p}: orphan continuation-row metadata");
            }
            if (!_rowColdFlags[p] && rowHasCold)
                violations.Add($"phys row {p}: cold metadata present while RowColdFlags is false");
            if (lastContentCol >= 0 && _rowMaxCol[p] < lastContentCol)
                violations.Add($"phys row {p}: RowMaxCol {_rowMaxCol[p]} below content max {lastContentCol}");
        }
        return violations;
    }

    internal CellHot[,] GetCellsForTests()
    {
        var result = new CellHot[Rows, Columns];
        for (int r = 0; r < Rows; r++)
        {
            int offset = GetPhysicalRow(r) * Columns;
            for (int c = 0; c < Columns; c++)
                result[r, c] = UnsafeAsRef<CellHot>(_cellsPtr, offset + c);
        }
        return result;
    }

    internal ColdCell[,] GetColdCellsForTests()
    {
        var result = new ColdCell[Rows, Columns];
        for (int r = 0; r < Rows; r++)
        {
            int offset = GetPhysicalRow(r) * Columns;
            for (int c = 0; c < Columns; c++)
                result[r, c] = UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + c);
        }
        return result;
    }

    public void ClearCell(int logicalRow, int col)
    {
        if (logicalRow < 0 || logicalRow >= Rows || col < 0 || col >= Columns) return;

        int pRow = GetPhysicalRow(logicalRow);
        int offset = pRow * Columns;

        int baseCol = col;
        if (UnsafeAsRef<CellHot>(_cellsPtr, offset + baseCol).IsContinuation)
        {
            while (baseCol > 0 && UnsafeAsRef<CellHot>(_cellsPtr, offset + baseCol).IsContinuation)
                baseCol--;
        }
        else
        {
            int scan = baseCol - 1;
            while (scan >= 0)
            {
                ref var cand = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + scan);
                int w = Math.Max(1, (int)cand.Width);
                if (!cand.IsContinuation && w > 1 && scan + w > col)
                {
                    baseCol = scan;
                    break;
                }
                scan--;
            }
        }

        UnsafeAsRef<CellHot>(_cellsPtr, offset + baseCol).Reset();
        UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + baseCol).Reset();
        int c = baseCol + 1;
        while (c < Columns)
        {
            ref var nxt = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + c);
            if (!nxt.IsContinuation) break;
            nxt.Reset();
            UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + c).Reset();
            c++;
        }

        RecalculateRowMaxCol(logicalRow);
        RecalculateRowEndCol(logicalRow);
        RowContinuesPrevious[pRow] = false;
    }

    public void ScrollUpRegion(int top, int bottom, int lines)
    {
        if (lines <= 0) return;
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (top >= bottom) return;

        int regionHeight = bottom - top + 1;
        int total = _scrollbackCapacity + Rows;

        // Full-screen fast path: rotate ring-buffer head, works for any lines count
        if (top == 0 && bottom == Rows - 1)
        {
            int clampedLines = Math.Min(lines, regionHeight);
            _head = (_head + clampedLines) % total;
            for (int i = 0; i < clampedLines; i++)
            {
                int phys = (_head + Rows - 1 - i + total) % total;
                ClearPhysicalRow(phys);
            }
            return;
        }

        if (lines >= regionHeight)
        {
            for (int r = top; r <= bottom; r++)
                ClearPhysicalRow(GetPhysicalRow(r));
            return;
        }

        // Non-full-screen single-line: memory copy
        if (lines == 1)
        {
            for (int r = top; r < bottom; r++)
            {
                int srcPhys = GetPhysicalRow(r + 1);
                int dstPhys = GetPhysicalRow(r);
                int rowBytes = Columns * Unsafe.SizeOf<CellHot>();
                System.Buffer.MemoryCopy(
                    (void*)(_cellsPtr + srcPhys * Columns * Unsafe.SizeOf<CellHot>()),
                    (void*)(_cellsPtr + dstPhys * Columns * Unsafe.SizeOf<CellHot>()),
                    rowBytes, rowBytes);
                int coldRowBytes = Columns * Unsafe.SizeOf<ColdCell>();
                System.Buffer.MemoryCopy(
                    (void*)(_coldCellsPtr + srcPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                    (void*)(_coldCellsPtr + dstPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                    coldRowBytes, coldRowBytes);
                CopyRowMetadata(dstPhys, srcPhys);
            }
            ClearPhysicalRow(GetPhysicalRow(bottom));
            return;
        }


        for (int r = top + lines; r <= bottom; r++)
        {
            int srcPhys = GetPhysicalRow(r);
            int dstPhys = GetPhysicalRow(r - lines);
            int rowBytes = Columns * Unsafe.SizeOf<CellHot>();
            System.Buffer.MemoryCopy(
                (void*)(_cellsPtr + srcPhys * Columns * Unsafe.SizeOf<CellHot>()),
                (void*)(_cellsPtr + dstPhys * Columns * Unsafe.SizeOf<CellHot>()),
                rowBytes, rowBytes);
            int coldRowBytes = Columns * Unsafe.SizeOf<ColdCell>();
            System.Buffer.MemoryCopy(
                (void*)(_coldCellsPtr + srcPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                (void*)(_coldCellsPtr + dstPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                coldRowBytes, coldRowBytes);
            CopyRowMetadata(dstPhys, srcPhys);
        }

        for (int l = 0; l < lines; l++)
            ClearPhysicalRow(GetPhysicalRow(bottom - lines + 1 + l));
    }

    public void ScrollDownRegion(int top, int bottom, int lines)
    {
        if (lines <= 0) return;
        if (top < 0) top = 0;
        if (bottom >= Rows) bottom = Rows - 1;
        if (top > bottom) return;

        int regionHeight = bottom - top + 1;

        // Full-screen fast path: rotate ring-buffer head backward
        if (top == 0 && bottom == Rows - 1)
        {
            int clampedLines = Math.Min(lines, regionHeight);
            int total = _scrollbackCapacity + Rows;
            _head = (_head - clampedLines + total * (clampedLines / total + 1)) % total;
            return;
        }

        if (lines >= regionHeight)
        {
            for (int r = top; r <= bottom; r++)
                ClearPhysicalRow(GetPhysicalRow(r));
            return;
        }

        int[] savedMaxCols = new int[regionHeight];
        bool[] savedColdFlags = new bool[regionHeight];
        bool[] savedContinuations = new bool[regionHeight];
        int[] savedEndCols = new int[regionHeight];
        var savedCells = new CellHot[regionHeight * Columns];
        var savedCold = new ColdCell[regionHeight * Columns];
        int rowSizeBytes = Columns * Unsafe.SizeOf<CellHot>();
        int coldRowSizeBytes = Columns * Unsafe.SizeOf<ColdCell>();
        for (int r = top; r <= bottom; r++)
        {
            int srcPhys = GetPhysicalRow(r);
            int savedIndex = r - top;
            savedMaxCols[savedIndex] = _rowMaxCol[srcPhys];
            savedColdFlags[savedIndex] = _rowColdFlags[srcPhys];
            savedContinuations[savedIndex] = RowContinuesPrevious[srcPhys];
            savedEndCols[savedIndex] = RowEndCol[srcPhys];
            fixed (CellHot* pDst = &savedCells[(r - top) * Columns])
            {
                System.Buffer.MemoryCopy(
                    (void*)(_cellsPtr + srcPhys * Columns * Unsafe.SizeOf<CellHot>()),
                    pDst,
                    rowSizeBytes, rowSizeBytes);
            }
            fixed (ColdCell* pDst = &savedCold[(r - top) * Columns])
            {
                System.Buffer.MemoryCopy(
                    (void*)(_coldCellsPtr + srcPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                    pDst,
                    coldRowSizeBytes, coldRowSizeBytes);
            }
        }

        for (int r = bottom; r >= top + lines; r--)
        {
            int srcIndex = r - lines - top;
            int dstPhys = GetPhysicalRow(r);
            fixed (CellHot* pSrc = &savedCells[srcIndex * Columns])
            {
                System.Buffer.MemoryCopy(
                    pSrc,
                    (void*)(_cellsPtr + dstPhys * Columns * Unsafe.SizeOf<CellHot>()),
                    rowSizeBytes,
                    rowSizeBytes);
            }
            fixed (ColdCell* pSrc = &savedCold[srcIndex * Columns])
            {
                System.Buffer.MemoryCopy(
                    pSrc,
                    (void*)(_coldCellsPtr + dstPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                    coldRowSizeBytes,
                    coldRowSizeBytes);
            }
            _rowMaxCol[dstPhys] = savedMaxCols[srcIndex];
            _rowColdFlags[dstPhys] = savedColdFlags[srcIndex];
            RowContinuesPrevious[dstPhys] = savedContinuations[srcIndex];
            RowEndCol[dstPhys] = savedEndCols[srcIndex];
        }

        for (int r = top; r < top + lines; r++)
            ClearPhysicalRow(GetPhysicalRow(r));
    }

    public void ClearFromColumn(int logicalRow, int startCol)
    {
        if (logicalRow < 0 || logicalRow >= Rows) return;
        if (startCol < 0) startCol = 0;
        if (startCol >= Columns) return;
        for (int c = startCol; c < Columns; c++)
            ClearCell(logicalRow, c);
    }

    public Screen Resize(int rows, int columns)
    {
        rows = Math.Max(1, rows);
        columns = Math.Max(1, columns);
        if (rows == Rows && columns == Columns)
            return this;

        return ReflowWithOptions(
            rows,
            columns,
            new ReflowCursorAnchor(0, 0),
            out _,
            scrollbackRows: _scrollbackCapacity,
            includeScrollback: true);
    }

    public void CopyTo(Screen destination)
    {
        int rows = Math.Min(Rows, destination.Rows);
        int cols = Math.Min(Columns, destination.Columns);
        int rowSizeBytes = cols * Unsafe.SizeOf<CellHot>();
        int coldRowSizeBytes = cols * Unsafe.SizeOf<ColdCell>();

        // Copy visible rows.
        for (int r = 0; r < rows; r++)
        {
            int srcPhys = GetPhysicalRow(r);
            int dstPhys = destination.GetPhysicalRow(r);
            System.Buffer.MemoryCopy(
                (void*)(_cellsPtr + srcPhys * Columns * Unsafe.SizeOf<CellHot>()),
                (void*)(destination._cellsPtr + dstPhys * destination.Columns * Unsafe.SizeOf<CellHot>()),
                rowSizeBytes, rowSizeBytes);
            System.Buffer.MemoryCopy(
                (void*)(_coldCellsPtr + srcPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                (void*)(destination._coldCellsPtr + dstPhys * destination.Columns * Unsafe.SizeOf<ColdCell>()),
                coldRowSizeBytes, coldRowSizeBytes);
            destination._rowMaxCol[dstPhys] = Math.Min(_rowMaxCol[srcPhys], cols - 1);
            destination._rowColdFlags[dstPhys] = _rowColdFlags[srcPhys];
            destination.RowContinuesPrevious[dstPhys] = RowContinuesPrevious[srcPhys];
            destination.RowEndCol[dstPhys] = Math.Min(RowEndCol[srcPhys], cols - 1);
            // Narrowing cuts the continuation column of a wide glyph at the new
            // edge; drop the dangling base so no raw cell scan sees an
            // unterminated width.
            if (Columns > destination.Columns)
                destination.ClearTruncatedWideGlyph(dstPhys, destination.Columns - 1);
        }

        // Compatibility copy for callers that need the raw cell arena. The
        // TerminalBuffer resize path uses Reflow so logical soft wraps remain
        // cell-preserving.
        {
            int srcTotal = _scrollbackCapacity + Rows;
            int dstTotal = destination._scrollbackCapacity + destination.Rows;
            int maxSbCopy = Math.Min(_scrollbackCapacity, destination._scrollbackCapacity);
            // Use the narrower column count so MemoryCopy never reads past a row boundary.
            int copyCols = Math.Min(Columns, destination.Columns);
            int hotRowBytes = copyCols * Unsafe.SizeOf<CellHot>();
            int coldRowBytes = copyCols * Unsafe.SizeOf<ColdCell>();

            for (int i = 0; i < maxSbCopy; i++)
            {
                int srcPhys = (_head - 1 - i + srcTotal * 2) % srcTotal;

                // Destination uses _head = 0; scrollback slots run from dstTotal-1 downward.
                int dstPhys = dstTotal - 1 - i;
                System.Buffer.MemoryCopy(
                    (void*)(_cellsPtr + (long)srcPhys * Columns * Unsafe.SizeOf<CellHot>()),
                    (void*)(destination._cellsPtr + (long)dstPhys * destination.Columns * Unsafe.SizeOf<CellHot>()),
                    hotRowBytes, hotRowBytes);
                System.Buffer.MemoryCopy(
                    (void*)(_coldCellsPtr + (long)srcPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                    (void*)(destination._coldCellsPtr + (long)dstPhys * destination.Columns * Unsafe.SizeOf<ColdCell>()),
                    coldRowBytes, coldRowBytes);
                // Clamp maxCol to the new width in case we just truncated the row.
                destination._rowMaxCol[dstPhys] = Math.Min(_rowMaxCol[srcPhys], destination.Columns - 1);
                destination._rowColdFlags[dstPhys] = _rowColdFlags[srcPhys];
                destination.RowContinuesPrevious[dstPhys] = RowContinuesPrevious[srcPhys];
                destination.RowEndCol[dstPhys] = Math.Min(RowEndCol[srcPhys], destination.Columns - 1);
                if (Columns > destination.Columns)
                    destination.ClearTruncatedWideGlyph(dstPhys, destination.Columns - 1);
            }
        }
    }

    /// <summary>
    /// Resize narrowing cuts the continuation column of a wide glyph whose
    /// base sits on the new last column, leaving a dangling width-2 base.
    /// Drop the whole glyph so raw cell scans (renderer, validator) never see
    /// an unterminated width. Operates on a physical row directly.
    /// </summary>
    private void ClearTruncatedWideGlyph(int dstPhys, int edgeCol)
    {
        int offset = dstPhys * Columns;
        ref var edge = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + edgeCol);
        if (edge.IsContinuation)
        {
            // Defensive: a continuation survived at the edge — walk back to its
            // base and clear the whole glyph.
            int baseCol = edgeCol;
            while (baseCol > 0 && UnsafeAsRef<CellHot>(_cellsPtr, offset + baseCol).IsContinuation)
                baseCol--;
            int c = baseCol;
            while (c < Columns && (c == baseCol || UnsafeAsRef<CellHot>(_cellsPtr, offset + c).IsContinuation))
            {
                UnsafeAsRef<CellHot>(_cellsPtr, offset + c).Reset();
                UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + c).Reset();
                c++;
            }
        }
        else if (edge.Rune != 0 && edge.Width > 1)
        {
            edge.Reset();
            UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + edgeCol).Reset();
        }
        else
        {
            return;
        }

        // Recompute the physical row's max column (space counts as empty).
        int maxCol = -1;
        for (int j = Columns - 1; j >= 0; j--)
        {
            var cell = UnsafeAsRef<CellHot>(_cellsPtr, offset + j);
            if (!cell.IsContinuation && cell.Rune != 0 && cell.Rune != 32)
            {
                maxCol = j;
                break;
            }
        }
        _rowMaxCol[dstPhys] = maxCol;
        int endCol = -1;
        for (int j = Columns - 1; j >= 0; j--)
        {
            var cell = UnsafeAsRef<CellHot>(_cellsPtr, offset + j);
            var cold = UnsafeAsRef<ColdCell>(_coldCellsPtr, offset + j);
            if (cell.Rune != 0 || cell.PackedFlags != 0 || cell.StyleId != 0
                || cold.HyperlinkId != 0 || cold.GraphemeIndex >= 0)
            {
                endCol = j;
                break;
            }
        }
        RowEndCol[dstPhys] = endCol;
    }

    public void ReadSnapshot(ref CellHot[] cellsSnapshot, ref ColdCell[] coldSnapshot, ref int[] rowMapSnapshot)
    {
        int hotByteCount = _cellCount * Unsafe.SizeOf<CellHot>();
        int coldByteCount = _cellCount * Unsafe.SizeOf<ColdCell>();

        if (cellsSnapshot == null || cellsSnapshot.Length != _cellCount)
            cellsSnapshot = new CellHot[_cellCount];
        if (coldSnapshot == null || coldSnapshot.Length != _cellCount)
            coldSnapshot = new ColdCell[_cellCount];
        if (rowMapSnapshot == null || rowMapSnapshot.Length != Rows)
            rowMapSnapshot = new int[Rows];

        for (int row = 0; row < Rows; row++)
            rowMapSnapshot[row] = GetPhysicalRow(row);

        fixed (CellHot* pCells = cellsSnapshot)
        fixed (ColdCell* pCold = coldSnapshot)
        {
            System.Buffer.MemoryCopy((void*)_cellsPtr, pCells, hotByteCount, hotByteCount);
            System.Buffer.MemoryCopy((void*)_coldCellsPtr, pCold, coldByteCount, coldByteCount);
        }
    }
}
