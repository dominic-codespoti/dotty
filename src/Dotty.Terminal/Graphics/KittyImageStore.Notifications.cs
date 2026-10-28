using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;

namespace Dotty.Terminal.Graphics;

/// <summary>Registers an anchor with the reflow and returns its slot; <paramref name="logicalRow"/> is relative to the visible row 0 (negative = history).</summary>
internal delegate int AnchorRegistrar(int logicalRow, int column);

/// <summary>
/// Reads where a registered anchor landed: <paramref name="row"/> is the chronological row index in the reflowed
/// output (0 = oldest retained history row), <paramref name="column"/> the 0-based column. False when the anchor's
/// line was trimmed away.
/// </summary>
internal delegate bool AnchorResolver(int slot, out long row, out int column);

// Notifications the terminal buffer sends when rows move. All of them are pure bookkeeping over absolute rows,
// take no screen, and return the number of placements they deleted (or changed, as documented per method) so the
// caller can decide whether the display needs a refresh.
internal sealed partial class KittyImageStore
{
    private readonly List<(KittyPlacement Placement, int Slot)> _anchors = new();

    /// <summary>
    /// The screen's ring head rotated by <paramref name="delta"/> rows (a full-screen scroll up): every placement
    /// moves up with the text (<see cref="ViewTop"/> advances) and placements whose last row is older than the
    /// ring's history capacity (<c>EndRow &lt;= ViewTop - scrollbackCapacity</c>) are deleted. A cached lower
    /// bound of the end rows makes the common case O(1).
    /// </summary>
    /// <returns>Placements deleted.</returns>
    public int OnRingRotatedUp(long delta, int scrollbackCapacity)
    {
        if (delta <= 0)
            return 0;
        _viewTop += delta;
        long floor = _viewTop - Math.Max(scrollbackCapacity, 0);
        return _minEndRow > floor ? 0 : PruneEndingAtOrBefore(floor);
    }

    /// <summary>
    /// The ring head rotated back by <paramref name="delta"/> rows (a full-screen scroll down that pulled history
    /// back): every placement moves down with the text and those that now start below the last of
    /// <paramref name="rows"/> visible rows are deleted (Dotty's ring loses the content pushed off the bottom).
    /// </summary>
    /// <returns>Placements deleted.</returns>
    public int OnRingRotatedDown(int delta, int rows)
    {
        if (delta <= 0)
            return 0;
        _viewTop -= delta;
        long limit = _viewTop + Math.Max(rows, 0);
        _scratch.Clear();
        long min = long.MaxValue;
        foreach (var p in _placements.Values)
        {
            if (p.Kind == PlacementKind.Virtual)
                continue;
            if (p.AnchorRow >= limit)
                _scratch.Add(p);
            else if (p.EndRow < min)
                min = p.EndRow;
        }
        _minEndRow = min;
        return RemoveScratchPlacements();
    }

    /// <summary>
    /// A scroll of the region of rows <c>[top, bottom]</c> (visible rows, inclusive) by <paramref name="delta"/>
    /// rows that did not rotate the ring (<paramref name="delta"/> &gt; 0 scrolls up, &lt; 0 down). Kitty margin
    /// semantics (the design plan's "only placements entirely inside the margins move, are cropped when they cross a
    /// margin and deleted when fully outside" read as a sequence: inside → moves → crop/delete): only placements
    /// wholly inside the region move; a moved placement that leaves the region is cropped at the margin
    /// (<see cref="PlacementGeometry.CropTop"/>/<see cref="PlacementGeometry.CropBottom"/>, the source rectangle
    /// shrinks proportionally, the cropped part never grows back) and deleted when nothing of it stays inside.
    /// Placements that are not wholly inside before the scroll are left alone: those wholly outside the region
    /// (above, below or in the history), and those that straddle a margin (they overhang it, so they were not
    /// "inside" to begin with; this includes a tall image placed at the bottom margin, which therefore stays put
    /// while the text under it scrolls).
    /// </summary>
    /// <returns>Placements moved, cropped or deleted.</returns>
    public int OnRegionScrolled(int top, int bottom, int delta, int rows)
    {
        if (delta == 0 || _placements.Count == 0)
            return 0;
        top = Math.Max(top, 0);
        bottom = Math.Min(bottom, rows - 1);
        if (top > bottom)
            return 0;

        long regionTop = _viewTop + top;
        long regionEnd = _viewTop + bottom + 1;
        int affected = 0;
        _scratch.Clear();
        foreach (var p in _placements.Values)
        {
            if (p.Kind == PlacementKind.Virtual)
                continue;
            if (p.AnchorRow < regionTop || p.EndRow > regionEnd)
                continue;

            affected++;
            var geometry = p.Geometry;
            long newAnchor = p.AnchorRow - delta;
            if (delta > 0)
            {
                long lost = regionTop - newAnchor;
                if (lost >= geometry.EffRows)
                {
                    _scratch.Add(p);
                    continue;
                }
                if (lost > 0)
                {
                    geometry = PlacementGeometry.CropTop(in geometry, (int)lost);
                    newAnchor = regionTop;
                }
            }
            else
            {
                long over = newAnchor + geometry.EffRows - regionEnd;
                if (over >= geometry.EffRows)
                {
                    _scratch.Add(p);
                    continue;
                }
                if (over > 0)
                    geometry = PlacementGeometry.CropBottom(in geometry, (int)over);
            }

            if (geometry.IsEmpty)
            {
                _scratch.Add(p);
                continue;
            }
            p.Geometry = geometry;
            p.AnchorRow = newAnchor;
            NoteEndRow(p.EndRow);
        }
        RemoveScratchPlacements();
        return affected;
    }

    /// <summary>
    /// History rows were discarded or renumbered other than by the ring wrapping (the buffer's
    /// <see cref="HistoryDiscardReason"/>). <paramref name="floorAbsoluteRow"/> is in <see cref="ViewTop"/>
    /// coordinates: the first row that is still retained.
    /// <list type="bullet">
    /// <item><see cref="HistoryDiscardReason.Reset"/> (RIS, clear screen): every placement is deleted.</item>
    /// <item><see cref="HistoryDiscardReason.Cleared"/> (CSI 3 J, ED 2) and
    /// <see cref="HistoryDiscardReason.CapacityShrunk"/>: placements wholly above the floor
    /// (<c>EndRow &lt;= floor</c>) are deleted. CSI 3 J passes <c>ViewTop</c> as the floor, so only placements in the
    /// history go and the visible ones stay: a deliberate divergence from kitty, which deletes all of them on ED 3
    /// (Dotty's CSI 3 J does not clear the visible screen either). A caller whose operation also wipes the visible
    /// rows (ED 2 clears the whole ring) passes <see cref="long.MaxValue"/>.</item>
    /// <item><see cref="HistoryDiscardReason.Rebased"/>: rows were only renumbered (the reflow rebases the anchors
    /// itself); nothing is deleted.</item>
    /// </list>
    /// Virtual placements have no row and survive everything except <see cref="HistoryDiscardReason.Reset"/>.
    /// </summary>
    /// <returns>Placements deleted.</returns>
    public int OnHistoryInvalidated(HistoryDiscardReason reason, long floorAbsoluteRow)
    {
        switch (reason)
        {
            case HistoryDiscardReason.Reset:
                return ClearAll();
            case HistoryDiscardReason.Cleared:
            case HistoryDiscardReason.CapacityShrunk:
                return PruneEndingAtOrBefore(floorAbsoluteRow);
            default:
                return 0;
        }
    }

    /// <summary>
    /// The cell size changed: placements resolved at another size are re-resolved at the new one (the cell box of
    /// native-size and one-dimension placements follows the pixel size, in-cell offsets are re-clamped below the new
    /// cell size). The current, possibly cropped, source rectangle and cell box are the input, so a crop survives.
    /// </summary>
    /// <returns>Placements whose geometry was re-resolved.</returns>
    public int OnCellPixelSizeChanged(int cellPixelWidth, int cellPixelHeight)
    {
        int cw = Math.Max(cellPixelWidth, 1), ch = Math.Max(cellPixelHeight, 1);
        int changed = 0;
        foreach (var p in _placements.Values)
        {
            var g = p.Geometry;
            if (g.BasisCellW == cw && g.BasisCellH == ch)
                continue;
            var root = p.Image.Root;
            var resolved = PlacementGeometry.Resolve(
                (uint)root.Width, (uint)root.Height,
                (uint)g.SrcX, (uint)g.SrcY, (uint)g.SrcW, (uint)g.SrcH,
                (uint)g.XOff, (uint)g.YOff,
                p.RequestedCols != 0 ? (uint)g.EffCols : 0u,
                p.RequestedRows != 0 ? (uint)g.EffRows : 0u,
                cw, ch);
            if (resolved.IsEmpty)
                continue;
            p.Geometry = resolved;
            if (p.Kind != PlacementKind.Virtual)
                NoteEndRow(p.EndRow);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// First half of a reflow rebase (mirrors the buffer's prompt-mark capture): registers every anchored placement
    /// whose row is still held (<c>-scrollbackRows &lt;= AnchorRow - ViewTop &lt; rows</c>) through
    /// <paramref name="register"/>. Placements not registered are deleted by <see cref="RebaseAnchors"/>.
    /// </summary>
    /// <returns>Anchors registered.</returns>
    public int CaptureAnchors(int scrollbackRows, int rows, AnchorRegistrar register)
    {
        ArgumentNullException.ThrowIfNull(register);
        _anchors.Clear();
        foreach (var p in _placements.Values)
        {
            if (p.Kind == PlacementKind.Virtual)
                continue;
            long logical = p.AnchorRow - _viewTop;
            if (logical < -(long)scrollbackRows || logical >= rows)
                continue;
            _anchors.Add((p, register((int)logical, p.AnchorCol)));
        }
        return _anchors.Count;
    }

    /// <summary>
    /// Gives up a reflow rebase that cannot complete (an exception between <see cref="CaptureAnchors"/> and
    /// <see cref="RebaseAnchors"/>): the captured anchors are dropped and every placement is deleted, because the
    /// rows they refer to are in an unknown state. The images stay.
    /// </summary>
    /// <returns>Placements deleted.</returns>
    public int AbandonAnchors()
    {
        _anchors.Clear();
        return ClearAll();
    }

    /// <summary>
    /// Second half of a reflow rebase: <see cref="ViewTop"/> becomes <paramref name="newScrollbackRows"/> and each
    /// captured placement moves to the position <paramref name="resolve"/> reports (row = chronological index, so
    /// the visible row 0 is index <c>newScrollbackRows</c>). Placements that were not captured, whose line was
    /// trimmed, or that land outside <c>[0, newScrollbackRows + newRows)</c> are deleted. Cell extents are kept;
    /// the renderer clips to the new width.
    /// </summary>
    /// <returns>Placements deleted.</returns>
    public int RebaseAnchors(int newScrollbackRows, int newRows, AnchorResolver resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        long total = (long)newScrollbackRows + newRows;
        foreach (var p in _placements.Values)
            p.Rebased = false;
        foreach (var (p, slot) in _anchors)
        {
            if (!_placements.ContainsKey(p.InternalId))
                continue;
            if (resolve(slot, out long row, out int column) && row >= 0 && row < total)
            {
                p.AnchorRow = row;
                p.AnchorCol = Math.Max(column, 0);
                p.Rebased = true;
            }
        }
        _anchors.Clear();
        _viewTop = newScrollbackRows;

        _scratch.Clear();
        long min = long.MaxValue;
        foreach (var p in _placements.Values)
        {
            if (p.Kind == PlacementKind.Virtual)
                continue;
            if (!p.Rebased)
                _scratch.Add(p);
            else if (p.EndRow < min)
                min = p.EndRow;
        }
        _minEndRow = min;
        return RemoveScratchPlacements();
    }

    /// <summary>
    /// Deletes every placement of this screen (all kinds). The images stay; anonymous ones that were only displayed
    /// by the deleted placements go with them. Use <see cref="Clear"/> to delete the images too.
    /// </summary>
    /// <returns>Placements deleted.</returns>
    public int ClearAll()
    {
        if (_placements.Count == 0)
            return 0;
        _scratch.Clear();
        _scratch.AddRange(_placements.Values);
        _minEndRow = long.MaxValue;
        return RemoveScratchPlacements();
    }

    /// <summary>Deletes placements whose last row is at or above <paramref name="floor"/> and recomputes the end-row bound.</summary>
    private int PruneEndingAtOrBefore(long floor)
    {
        _scratch.Clear();
        long min = long.MaxValue;
        foreach (var p in _placements.Values)
        {
            if (p.Kind == PlacementKind.Virtual)
                continue;
            if (p.EndRow <= floor)
                _scratch.Add(p);
            else if (p.EndRow < min)
                min = p.EndRow;
        }
        _minEndRow = min;
        return RemoveScratchPlacements();
    }
}
