using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dotty.Terminal.Adapter;

public unsafe class Screen : IDisposable
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
        int offset = GetPhysicalRow(logicalRow) * Columns + col;
        UnsafeAsRef<ColdCell>(_coldCellsPtr, offset).HyperlinkId = hyperlinkId;
        UnsafeAsRef<CellHot>(_cellsPtr, offset).HasHyperlink = hyperlinkId != 0;
    }

    public void SetColdGraphemeIndex(int logicalRow, int col, short graphemeIndex)
    {
        int offset = GetPhysicalRow(logicalRow) * Columns + col;
        UnsafeAsRef<ColdCell>(_coldCellsPtr, offset).GraphemeIndex = graphemeIndex;
        UnsafeAsRef<CellHot>(_cellsPtr, offset).HasGrapheme = graphemeIndex > 0;
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

        if (maxCol < 0) return string.Empty;

        int offset = pRow * Columns;
        var chars = new char[maxCol + 1];
        for (int i = 0; i <= maxCol; i++)
        {
            ref var cell = ref UnsafeAsRef<CellHot>(_cellsPtr, offset + i);
            if (cell.IsContinuation || cell.Rune == 0)
                chars[i] = ' ';
            else if (cell.Rune <= 0xFFFF)
                chars[i] = (char)cell.Rune;
            else
                chars[i] = '\uFFFD';
        }
        return new string(chars);
    }

    public int GetScrollbackRowLength(int scrollbackIndex)
    {
        int total = _scrollbackCapacity + Rows;
        int pRow = (_head - 1 - scrollbackIndex + total * 2) % total;
        return Math.Max(0, _rowMaxCol[pRow] + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearPhysicalRow(int physicalRow)
    {
        new Span<CellHot>((void*)(_cellsPtr + physicalRow * Columns * Unsafe.SizeOf<CellHot>()), Columns).Clear();
        var coldSpan = new Span<ColdCell>((void*)(_coldCellsPtr + physicalRow * Columns * Unsafe.SizeOf<ColdCell>()), Columns);
        for (int i = 0; i < Columns; i++)
            coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
        _rowMaxCol[physicalRow] = -1;
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
            {
                int pRow = GetPhysicalRow(r);
                new Span<CellHot>((void*)(_cellsPtr + pRow * Columns * Unsafe.SizeOf<CellHot>()), Columns).Clear();
                var coldSpan = new Span<ColdCell>((void*)(_coldCellsPtr + pRow * Columns * Unsafe.SizeOf<ColdCell>()), Columns);
                for (int i = 0; i < Columns; i++)
                    coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
                _rowMaxCol[pRow] = -1;
            }
            return;
        }

        // Non-full-screen single-line: memory copy
        if (lines == 1)
        {
            int physicalTop = GetPhysicalRow(top);
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
                _rowMaxCol[dstPhys] = _rowMaxCol[srcPhys];
            }
            int newBot = GetPhysicalRow(bottom);
            new Span<CellHot>((void*)(_cellsPtr + newBot * Columns * Unsafe.SizeOf<CellHot>()), Columns).Clear();
            var coldSpan = new Span<ColdCell>((void*)(_coldCellsPtr + newBot * Columns * Unsafe.SizeOf<ColdCell>()), Columns);
            for (int i = 0; i < Columns; i++)
                coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
            _rowMaxCol[newBot] = -1;
            return;
        }

        int[] savedRows = new int[lines];
        for (int i = 0; i < lines; i++)
            savedRows[i] = GetPhysicalRow(top + i);

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
            _rowMaxCol[dstPhys] = _rowMaxCol[srcPhys];
        }

        for (int l = 0; l < lines; l++)
        {
            int phys = GetPhysicalRow(bottom - lines + 1 + l);
            new Span<CellHot>((void*)(_cellsPtr + phys * Columns * Unsafe.SizeOf<CellHot>()), Columns).Clear();
            var coldSpan = new Span<ColdCell>((void*)(_coldCellsPtr + phys * Columns * Unsafe.SizeOf<ColdCell>()), Columns);
            for (int i = 0; i < Columns; i++)
                coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
            _rowMaxCol[phys] = -1;
        }
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
            {
                int pRow = GetPhysicalRow(r);
                new Span<CellHot>((void*)(_cellsPtr + pRow * Columns * Unsafe.SizeOf<CellHot>()), Columns).Clear();
                var coldSpan = new Span<ColdCell>((void*)(_coldCellsPtr + pRow * Columns * Unsafe.SizeOf<ColdCell>()), Columns);
                for (int i = 0; i < Columns; i++)
                    coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
                _rowMaxCol[pRow] = -1;
            }
            return;
        }

        int[] savedMaxCols = new int[regionHeight];
        var savedCells = new CellHot[regionHeight * Columns];
        var savedCold = new ColdCell[regionHeight * Columns];
        int rowSizeBytes = Columns * Unsafe.SizeOf<CellHot>();
        int coldRowSizeBytes = Columns * Unsafe.SizeOf<ColdCell>();
        for (int r = top; r <= bottom; r++)
        {
            int srcPhys = GetPhysicalRow(r);
            savedMaxCols[r - top] = _rowMaxCol[srcPhys];
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
            int srcPhys = GetPhysicalRow(r - lines);
            int dstPhys = GetPhysicalRow(r);
            System.Buffer.MemoryCopy(
                (void*)(_cellsPtr + srcPhys * Columns * Unsafe.SizeOf<CellHot>()),
                (void*)(_cellsPtr + dstPhys * Columns * Unsafe.SizeOf<CellHot>()),
                rowSizeBytes, rowSizeBytes);
            System.Buffer.MemoryCopy(
                (void*)(_coldCellsPtr + srcPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                (void*)(_coldCellsPtr + dstPhys * Columns * Unsafe.SizeOf<ColdCell>()),
                coldRowSizeBytes, coldRowSizeBytes);
            _rowMaxCol[dstPhys] = _rowMaxCol[srcPhys];
        }

        for (int r = top; r < top + lines; r++)
        {
            int pRow = GetPhysicalRow(r);
            new Span<CellHot>((void*)(_cellsPtr + pRow * Columns * Unsafe.SizeOf<CellHot>()), Columns).Clear();
            var coldSpan = new Span<ColdCell>((void*)(_coldCellsPtr + pRow * Columns * Unsafe.SizeOf<ColdCell>()), Columns);
            for (int i = 0; i < Columns; i++)
                coldSpan[i] = new ColdCell { GraphemeIndex = -1 };
            _rowMaxCol[pRow] = -1;
        }
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

        var resized = new Screen(rows, columns, _scrollbackCapacity);
        CopyTo(resized);
        return resized;
    }

    public void CopyTo(Screen destination)
    {
        int rows = Math.Min(Rows, destination.Rows);
        int cols = Math.Min(Columns, destination.Columns);
        int rowSizeBytes = cols * Unsafe.SizeOf<CellHot>();
        int coldRowSizeBytes = cols * Unsafe.SizeOf<ColdCell>();
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
        }
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
