using System;
using System.Buffers;

namespace Dotty.Terminal.Adapter;

public sealed partial class RenderSnapshot
{
    // Compact copy of the scrollback rows' occupied cells, taken only by CaptureRenderSnapshotForSearch. Row i (0 = oldest)
    // owns [_sbOffsets[i], _sbOffsets[i + 1]) of the two cell arrays. The arrays come from ArrayPool and are returned on
    // Dispose, so a pooled snapshot never retains a scrollback-sized allocation after a search.
    private CellHot[]? _sbHot;
    private ColdCell[]? _sbCold;
    private int[]? _sbOffsets;
    private int _sbRowCount;

    /// <summary>
    /// Copies the occupied cells of the newest <paramref name="count"/> scrollback rows of <paramref name="screen"/>.
    /// The caller holds the buffer's SyncRoot, so both passes see the same rows.
    /// </summary>
    internal void CaptureScrollbackRows(Screen screen, int count)
    {
        ReleaseScrollbackRows();
        if (count <= 0)
            return;

        int capacity = screen.ScrollbackCapacity;
        int[] offsets = ArrayPool<int>.Shared.Rent(count + 1);
        offsets[0] = 0;
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            // Row i (0 = oldest) is scrollback index count - 1 - i (0 = newest) in the screen's ring. Indices past the
            // ring's capacity (a MaxScrollback override larger than the ring) hold no real history: capture them empty.
            int scrollbackIndex = count - 1 - i;
            if (scrollbackIndex < capacity && screen.TryReadScrollbackRow(scrollbackIndex, out var hot, out _))
                total += hot.Length;
            offsets[i + 1] = total;
        }

        CellHot[] hotStore = ArrayPool<CellHot>.Shared.Rent(Math.Max(1, total));
        ColdCell[] coldStore = ArrayPool<ColdCell>.Shared.Rent(Math.Max(1, total));
        for (int i = 0; i < count; i++)
        {
            int length = offsets[i + 1] - offsets[i];
            if (length == 0)
                continue;
            screen.TryReadScrollbackRow(count - 1 - i, out var hot, out var cold);
            hot.CopyTo(hotStore.AsSpan(offsets[i], length));
            cold.CopyTo(coldStore.AsSpan(offsets[i], length));
        }

        _sbHot = hotStore;
        _sbCold = coldStore;
        _sbOffsets = offsets;
        _sbRowCount = count;
    }

    /// <summary>
    /// Cells of one captured scrollback row (<paramref name="index"/> 0 = oldest, so its row number is
    /// <c>index - ScrollbackCount</c>), covering columns 0..the row's end column. Returns false when this snapshot did not
    /// capture scrollback cells or the index is out of range; an empty row returns true with empty spans.
    /// </summary>
    public bool TryGetScrollbackRow(int index, out ReadOnlySpan<CellHot> hot, out ReadOnlySpan<ColdCell> cold)
    {
        if (_sbOffsets is null || (uint)index >= (uint)_sbRowCount)
        {
            hot = default;
            cold = default;
            return false;
        }

        int start = _sbOffsets[index];
        int length = _sbOffsets[index + 1] - start;
        hot = new ReadOnlySpan<CellHot>(_sbHot!, start, length);
        cold = new ReadOnlySpan<ColdCell>(_sbCold!, start, length);
        return true;
    }

    private void ReleaseScrollbackRows()
    {
        _sbRowCount = 0;
        if (_sbOffsets is null)
            return;

        ArrayPool<int>.Shared.Return(_sbOffsets);
        ArrayPool<CellHot>.Shared.Return(_sbHot!);
        ArrayPool<ColdCell>.Shared.Return(_sbCold!);
        _sbOffsets = null;
        _sbHot = null;
        _sbCold = null;
    }
}
