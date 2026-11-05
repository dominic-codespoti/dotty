using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <inheritdoc cref="IRenderSource.TryGetScrollbackRow"/>
    /// <remarks>Live view: the caller holds <see cref="SyncRoot"/> while it reads the spans.</remarks>
    public bool TryGetScrollbackRow(int index, out ReadOnlySpan<CellHot> hot, out ReadOnlySpan<ColdCell> cold)
    {
        int count = ScrollbackCount;
        if ((uint)index >= (uint)count)
        {
            hot = default;
            cold = default;
            return false;
        }

        // Same index flip as GetScrollbackLine: row 0 is the oldest, the screen ring counts from the newest. Indices past
        // the ring (a MaxScrollback override larger than the capacity) hold no real history and read as empty rows.
        int scrollbackIndex = count - 1 - index;
        if (scrollbackIndex >= ActiveBuffer.ScrollbackCapacity)
        {
            hot = default;
            cold = default;
            return true;
        }

        ActiveBuffer.TryReadScrollbackRow(scrollbackIndex, out hot, out cold);
        return true;
    }

    /// <summary>
    /// Captures what the search overlay needs: the visible rows plus a compact copy of every scrollback row's occupied
    /// cells, so <c>SearchEngine.FindMatches</c> can find matches above the viewport. Unlike
    /// <see cref="CaptureRenderSnapshotVisible"/> (per-frame, visible rows only) this takes <see cref="SyncRoot"/> itself
    /// for the duration of the copy, so callers need not hold it. Dispose the snapshot to return the pooled copy.
    /// </summary>
    public RenderSnapshot CaptureRenderSnapshotForSearch()
    {
        lock (SyncRoot)
        {
            RenderSnapshot snapshot = CaptureRenderSnapshotVisible();
            try
            {
                snapshot.CaptureScrollbackRows(ActiveBuffer, snapshot.ScrollbackCount);
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
            return snapshot;
        }
    }
}
