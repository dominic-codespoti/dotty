using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Lazily committed arena bookkeeping. The hot and cold cell planes are reserved from <see cref="VirtualArena"/>
/// (zero-filled, not resident until written) and all-zero bytes are a valid blank cell (<see cref="ColdCell"/>
/// defaults to "empty"), so a screen needs no initialisation pass. To keep it that way the screen tracks a
/// <em>dirty extent</em>: at most two physical-row intervals that may hold non-default cells. Every row outside
/// them is guaranteed all-zero (and carries the initial row metadata). Rows only become dirty through the
/// visible window (writes are confined to it; the head moving brings new rows into it) or a reflow destination,
/// so the extent is updated at exactly those points. Resets (<see cref="Clear"/>, <see cref="DiscardHistory"/>,
/// <see cref="PrepareForReuse"/>, <see cref="Dispose"/>) discard only the extent, returning its pages to the OS.
/// Collapsing to a superset (even "everything is dirty") is always safe; it only costs memory.
/// </summary>
public unsafe partial class Screen
{
    // Physical-row intervals [lo, hi). Non-empty intervals are sorted with a strict gap between them; a lone
    // interval lives in slot 0 and empty slots are (0, 0).
    private int _dirtyLo0;
    private int _dirtyHi0;
    private int _dirtyLo1;
    private int _dirtyHi1;
    private int _dirtyRowCount;
    private long _reportedCommittedBytes;

    /// <summary>Physical rows that may hold non-default cells (an upper bound of the committed rows).</summary>
    internal int DirtyRowCount => _dirtyRowCount;

    /// <summary>Upper bound of the arena bytes backed by committed pages: dirty rows × columns × 12.</summary>
    internal long CommittedBoundBytes => (long)_dirtyRowCount * Columns * BytesPerCell;

    /// <summary>Virtual bytes reserved for the two arenas.</summary>
    internal long ReservedBytes => (long)_reservedCells * BytesPerCell;

    private static int BytesPerCell => Unsafe.SizeOf<CellHot>() + Unsafe.SizeOf<ColdCell>();

    internal void GetDirtyExtents(out int lo0, out int hi0, out int lo1, out int hi1)
    {
        lo0 = _dirtyLo0;
        hi0 = _dirtyHi0;
        lo1 = _dirtyLo1;
        hi1 = _dirtyHi1;
    }

    // ---- reservation ---------------------------------------------------------------------------------------

    private void ReserveArenas(int cells)
    {
        long oldBytes = _cellsPtr != IntPtr.Zero && _coldCellsPtr != IntPtr.Zero ? ReservedBytes : 0;
        nuint hotBytes = (nuint)cells * (nuint)Unsafe.SizeOf<CellHot>();
        nuint coldBytes = (nuint)cells * (nuint)Unsafe.SizeOf<ColdCell>();

        void* hot = VirtualArena.Reserve(hotBytes);
        void* cold;
        try
        {
            cold = VirtualArena.Reserve(coldBytes);
        }
        catch
        {
            VirtualArena.Release(hot, hotBytes);
            throw;
        }

        ReleaseArenas();
        _cellsPtr = (IntPtr)hot;
        _coldCellsPtr = (IntPtr)cold;
        _reservedCells = cells;

        // The new reservation is zero everywhere.
        SetDirtyExtents(0, 0, 0, 0);
        UpdateCommittedBound();
        BufferMemoryMetrics.ChangeArenaCapacity(oldBytes, (long)cells * BytesPerCell);
    }

    private void ReleaseArenas()
    {
        if (_cellsPtr != IntPtr.Zero)
            VirtualArena.Release((void*)_cellsPtr, (nuint)_reservedCells * (nuint)Unsafe.SizeOf<CellHot>());
        if (_coldCellsPtr != IntPtr.Zero)
            VirtualArena.Release((void*)_coldCellsPtr, (nuint)_reservedCells * (nuint)Unsafe.SizeOf<ColdCell>());
        _cellsPtr = IntPtr.Zero;
        _coldCellsPtr = IntPtr.Zero;
    }

    // ---- dirty extent --------------------------------------------------------------------------------------

    private void SetDirtyExtents(int lo0, int hi0, int lo1, int hi1)
    {
        bool has0 = hi0 > lo0;
        bool has1 = hi1 > lo1;
        if (!has0 && !has1)
        {
            lo0 = hi0 = lo1 = hi1 = 0;
        }
        else if (!has0)
        {
            lo0 = lo1;
            hi0 = hi1;
            lo1 = hi1 = 0;
        }
        else if (!has1)
        {
            lo1 = hi1 = 0;
        }
        else if (hi0 >= lo1)
        {
            // Touching or overlapping (inputs are sorted): one interval.
            hi0 = Math.Max(hi0, hi1);
            lo1 = hi1 = 0;
        }

        _dirtyLo0 = lo0;
        _dirtyHi0 = hi0;
        _dirtyLo1 = lo1;
        _dirtyHi1 = hi1;
        _dirtyRowCount = (hi0 - lo0) + (hi1 - lo1);
    }

    /// <summary>Adds physical rows <c>[lo, hi)</c> to the dirty extent.</summary>
    private void MarkDirty(int lo, int hi)
    {
        if (hi <= lo) return;

        // Already covered? (An empty slot is (0, 0) and can never contain a non-empty interval.)
        if ((lo >= _dirtyLo0 && hi <= _dirtyHi0) || (lo >= _dirtyLo1 && hi <= _dirtyHi1))
            return;

        int aLo = _dirtyLo0, aHi = _dirtyHi0, bLo = _dirtyLo1, bHi = _dirtyHi1;
        bool keepA = aHi > aLo;
        bool keepB = bHi > bLo;

        // Absorb every existing interval the new one touches or overlaps. A is below B with a strict gap, so
        // growing the candidate by B can never make it reach A when it did not already.
        if (keepA && lo <= aHi && hi >= aLo)
        {
            lo = Math.Min(lo, aLo);
            hi = Math.Max(hi, aHi);
            keepA = false;
        }

        if (keepB && lo <= bHi && hi >= bLo)
        {
            lo = Math.Min(lo, bLo);
            hi = Math.Max(hi, bHi);
            keepB = false;
        }

        if (!keepA && !keepB)
        {
            SetDirtyExtents(lo, hi, 0, 0);
        }
        else if (keepA && !keepB)
        {
            if (hi < aLo) SetDirtyExtents(lo, hi, aLo, aHi);
            else SetDirtyExtents(aLo, aHi, lo, hi);
        }
        else if (!keepA)
        {
            if (hi < bLo) SetDirtyExtents(lo, hi, bLo, bHi);
            else SetDirtyExtents(bLo, bHi, lo, hi);
        }
        else
        {
            // Three disjoint intervals: keep two by also covering the smaller of the two gaps.
            int x0Lo, x0Hi, x1Lo, x1Hi, x2Lo, x2Hi;
            if (hi < aLo)
            {
                x0Lo = lo; x0Hi = hi; x1Lo = aLo; x1Hi = aHi; x2Lo = bLo; x2Hi = bHi;
            }
            else if (lo > bHi)
            {
                x0Lo = aLo; x0Hi = aHi; x1Lo = bLo; x1Hi = bHi; x2Lo = lo; x2Hi = hi;
            }
            else
            {
                x0Lo = aLo; x0Hi = aHi; x1Lo = lo; x1Hi = hi; x2Lo = bLo; x2Hi = bHi;
            }

            if (x1Lo - x0Hi <= x2Lo - x1Hi)
                SetDirtyExtents(x0Lo, x1Hi, x2Lo, x2Hi);
            else
                SetDirtyExtents(x0Lo, x0Hi, x1Lo, x2Hi);
        }

        UpdateCommittedBound();
    }

    /// <summary>
    /// Called after every head change: the rows now in the visible window may be written to. Costs one compare
    /// once the whole ring is dirty (the steady state of a long-running flood).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteVisibleWindowTouched()
    {
        if (_dirtyRowCount < _scrollbackCapacity + Rows)
            MarkVisibleWindowDirty();
    }

    private void MarkVisibleWindowDirty()
    {
        int total = _scrollbackCapacity + Rows;
        int end = _head + Rows;
        if (end <= total)
        {
            MarkDirty(_head, end);
        }
        else
        {
            MarkDirty(_head, total);
            MarkDirty(0, end - total);
        }
    }

    private void SetDirtyToVisibleWindow()
    {
        int total = _scrollbackCapacity + Rows;
        int end = _head + Rows;
        if (end <= total)
            SetDirtyExtents(_head, end, 0, 0);
        else
            SetDirtyExtents(0, end - total, _head, total);
        UpdateCommittedBound();
    }

    private void SetAllRowsDirty()
    {
        SetDirtyExtents(0, _scrollbackCapacity + Rows, 0, 0);
        UpdateCommittedBound();
    }

    /// <summary>
    /// A reflow destination has its history at the end of the ring (<c>historyRows</c> rows ending at the last
    /// physical row) in addition to the visible window established by <see cref="ResetForReflow"/>.
    /// </summary>
    internal void NoteReflowDestinationHistory(int historyRows)
    {
        if (historyRows <= 0) return;
        int total = _scrollbackCapacity + Rows;
        MarkDirty(Math.Max(0, total - historyRows), total);
    }

    private void UpdateCommittedBound()
    {
        long bytes = CommittedBoundBytes;
        if (bytes == _reportedCommittedBytes) return;
        BufferMemoryMetrics.ChangeArenaCommittedBound(_reportedCommittedBytes, bytes);
        _reportedCommittedBytes = bytes;
    }

    // ---- discarding ----------------------------------------------------------------------------------------

    /// <summary>
    /// Zeroes every dirty row, returning whole pages to the OS. Uses the <em>current</em> <see cref="Columns"/>
    /// stride, so it must run before the column count changes. Does not touch the extent or the row metadata.
    /// </summary>
    private void DiscardDirtyRows()
    {
        if (_cellsPtr == IntPtr.Zero || _coldCellsPtr == IntPtr.Zero) return;
        DiscardRowRange(_dirtyLo0, _dirtyHi0);
        DiscardRowRange(_dirtyLo1, _dirtyHi1);
    }

    private void DiscardRowRange(int loRow, int hiRow)
    {
        if (hiRow <= loRow) return;
        nuint hotRowBytes = (nuint)(Columns * Unsafe.SizeOf<CellHot>());
        nuint coldRowBytes = (nuint)(Columns * Unsafe.SizeOf<ColdCell>());
        nuint rowCount = (nuint)(hiRow - loRow);
        VirtualArena.Discard((byte*)_cellsPtr + (long)loRow * (long)hotRowBytes, rowCount * hotRowBytes);
        VirtualArena.Discard((byte*)_coldCellsPtr + (long)loRow * (long)coldRowBytes, rowCount * coldRowBytes);
    }

    private void DiscardClipped(int lo, int hi, int clipLo, int clipHi)
    {
        int a = Math.Max(lo, clipLo);
        int b = Math.Min(hi, clipHi);
        if (b <= a) return;
        DiscardRowRange(a, b);
        Array.Fill(_rowMaxCol, -1, a, b - a);
        Array.Clear(_rowColdFlags, a, b - a);
        Array.Clear(_rowContinuesPrevious, a, b - a);
        Array.Fill(_rowEndCol, -1, a, b - a);
    }

    /// <summary>
    /// Blanks every row outside the visible window (the scrollback) and returns its pages to the OS; the visible
    /// window is untouched. Used by CSI 3 J, where the scrollback is dropped but the screen stays.
    /// </summary>
    internal void DiscardHistory()
    {
        if (_cellsPtr == IntPtr.Zero || _coldCellsPtr == IntPtr.Zero) return;

        int total = _scrollbackCapacity + Rows;
        int end = _head + Rows;

        // The rows outside the window, as at most two physical intervals.
        int c0Lo, c0Hi, c1Lo, c1Hi;
        if (end <= total)
        {
            c0Lo = 0; c0Hi = _head;
            c1Lo = end; c1Hi = total;
        }
        else
        {
            c0Lo = end - total; c0Hi = _head;
            c1Lo = c1Hi = 0;
        }

        DiscardClipped(_dirtyLo0, _dirtyHi0, c0Lo, c0Hi);
        DiscardClipped(_dirtyLo0, _dirtyHi0, c1Lo, c1Hi);
        DiscardClipped(_dirtyLo1, _dirtyHi1, c0Lo, c0Hi);
        DiscardClipped(_dirtyLo1, _dirtyHi1, c1Lo, c1Hi);

        // The first visible row can no longer continue a row that no longer exists.
        _rowContinuesPrevious[_head] = false;
        SetDirtyToVisibleWindow();
    }

    /// <summary>
    /// Resets a spare screen for later reuse as a reflow destination: contents discarded, dimensions set, and
    /// nothing counted as dirty until <see cref="ResetForReflow"/> establishes the destination's window. Keeps the
    /// virtual reservation so the next resize does not allocate.
    /// </summary>
    internal void ResetForReuse(int rows, int columns)
    {
        PrepareForReuse(rows, columns);
        SetDirtyExtents(0, 0, 0, 0);
        UpdateCommittedBound();
    }

    // ---- invariants ----------------------------------------------------------------------------------------

    /// <summary>
    /// Lazy-commit invariants (appended to <see cref="ValidateInvariants"/>): the extent is well formed and every
    /// row outside it is all-zero with initial metadata.
    /// </summary>
    private void ValidateLazyCommit(List<string> violations)
    {
        int total = _scrollbackCapacity + Rows;
        bool has0 = _dirtyHi0 > _dirtyLo0;
        bool has1 = _dirtyHi1 > _dirtyLo1;
        if (_dirtyLo0 < 0 || _dirtyHi0 > total || _dirtyLo1 < 0 || _dirtyHi1 > total
            || (!has0 && has1) || (has0 && has1 && _dirtyHi0 >= _dirtyLo1))
        {
            violations.Add($"dirty extent [{_dirtyLo0},{_dirtyHi0}) [{_dirtyLo1},{_dirtyHi1}) is malformed for {total} rows");
            return;
        }

        if (_dirtyRowCount != (_dirtyHi0 - _dirtyLo0) + (_dirtyHi1 - _dirtyLo1))
            violations.Add($"dirty row count {_dirtyRowCount} does not match the extent");

        int hotRowBytes = Columns * Unsafe.SizeOf<CellHot>();
        int coldRowBytes = Columns * Unsafe.SizeOf<ColdCell>();
        for (int p = 0; p < total; p++)
        {
            if ((p >= _dirtyLo0 && p < _dirtyHi0) || (p >= _dirtyLo1 && p < _dirtyHi1))
                continue;

            var hot = new ReadOnlySpan<byte>((void*)(_cellsPtr + (nint)p * hotRowBytes), hotRowBytes);
            var cold = new ReadOnlySpan<byte>((void*)(_coldCellsPtr + (nint)p * coldRowBytes), coldRowBytes);
            if (hot.IndexOfAnyExcept((byte)0) >= 0 || cold.IndexOfAnyExcept((byte)0) >= 0)
                violations.Add($"phys row {p}: outside the dirty extent but holds non-default cells");
            if (_rowMaxCol[p] != -1 || _rowEndCol[p] != -1 || _rowColdFlags[p] || _rowContinuesPrevious[p])
                violations.Add($"phys row {p}: outside the dirty extent but its row metadata is not initial");
        }
    }
}
