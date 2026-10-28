using System;
using System.Collections.Generic;

namespace Dotty.Terminal.Graphics;

internal sealed partial class KittyImageStore
{
    private readonly List<KittyImage> _matchedImages = new();

    /// <summary>
    /// Executes a delete command (<c>a=d</c>, key <c>d</c>). Lower-case values delete placements only. Upper-case
    /// values additionally free an image, but only one that matched the selection and has no placement left
    /// afterwards; a placement-specific delete (<c>p</c> given) that finds no placement matches nothing and frees
    /// nothing. Selections by position (<c>a c p q x y z</c>) look at normal placements only; virtual placements
    /// are reachable through <c>i I r R n N</c> alone. Coordinates <c>x</c>/<c>y</c> are 1-based (0 matches nothing).
    /// </summary>
    /// <param name="cursorRow">Cursor row, 0-based, visible-screen relative.</param>
    /// <param name="cursorCol">Cursor column, 0-based.</param>
    /// <param name="rows">Visible rows.</param>
    /// <returns>Placements deleted plus images freed (0: nothing changed).</returns>
    public int ApplyDelete(in KittyGraphicsCommand cmd, int cursorRow, int cursorCol, int rows)
    {
        var spec = KittyDelete.Parse(cmd.DeleteWhat);
        if (spec.Kind is KittyDeleteKind.Unknown or KittyDeleteKind.Frames)
            return 0;

        _scratch.Clear();
        _matchedImages.Clear();
        switch (spec.Kind)
        {
            case KittyDeleteKind.Image:
                SelectByImage(FindByClientId(cmd.ImageId), cmd.PlacementId);
                break;
            case KittyDeleteKind.Number:
                SelectByImage(FindByNumber(cmd.ImageNumber), cmd.PlacementId);
                break;
            case KittyDeleteKind.IdRange:
                SelectIdRange(cmd.SrcX, cmd.SrcY);
                break;
            default:
                SelectByPosition(spec.Kind, in cmd, cursorRow, cursorCol, rows);
                break;
        }

        int removed = RemoveScratchPlacementsKeepingSweep();
        int freed = 0;
        if (spec.FreeImages)
        {
            for (int i = 0; i < _matchedImages.Count; i++)
            {
                var image = _matchedImages[i];
                if (!image.IsDeleted && image.PlacementCount == 0 && Delete(image))
                    freed++;
            }
        }
        _matchedImages.Clear();
        SweepUnreferenced();
        return removed + freed;
    }

    private void SelectByImage(KittyImage? image, uint placementId)
    {
        if (image == null)
            return;
        if (placementId == 0)
        {
            _matchedImages.Add(image);
            if (image.PlacementList != null)
                _scratch.AddRange(image.PlacementList);
            return;
        }
        var placement = FindPlacement(image, placementId);
        if (placement == null)
            return;
        _matchedImages.Add(image);
        _scratch.Add(placement);
    }

    private void SelectIdRange(uint first, uint last)
    {
        if (first > last)
            return;
        foreach (var image in _byInternalId.Values)
        {
            if (image.ClientId == 0 || image.ClientId < first || image.ClientId > last)
                continue;
            _matchedImages.Add(image);
            if (image.PlacementList != null)
                _scratch.AddRange(image.PlacementList);
        }
    }

    private void SelectByPosition(KittyDeleteKind kind, in KittyGraphicsCommand cmd, int cursorRow, int cursorCol, int rows)
    {
        long cellCol = (long)cmd.SrcX - 1;   // x, 1-based; -1 for 0
        long cellRow = (long)cmd.SrcY - 1;   // y, 1-based; -1 for 0
        foreach (var p in _placements.Values)
        {
            if (p.Kind != PlacementKind.Normal)
                continue;
            long viewRow = p.AnchorRow - _viewTop;
            bool hit = kind switch
            {
                KittyDeleteKind.Visible => viewRow + p.EffRows > 0 && viewRow < rows,
                KittyDeleteKind.Cursor => Covers(p, viewRow, cursorRow, cursorCol),
                KittyDeleteKind.Cell => cellCol >= 0 && cellRow >= 0 && Covers(p, viewRow, cellRow, cellCol),
                KittyDeleteKind.CellZ => cellCol >= 0 && cellRow >= 0 && p.Z == cmd.Z && Covers(p, viewRow, cellRow, cellCol),
                KittyDeleteKind.Column => cellCol >= 0 && cellCol >= p.AnchorCol && cellCol < (long)p.AnchorCol + p.EffCols,
                KittyDeleteKind.Row => cellRow >= 0 && cellRow >= viewRow && cellRow < viewRow + p.EffRows,
                KittyDeleteKind.ZIndex => p.Z == cmd.Z,
                _ => false,
            };
            if (!hit)
                continue;
            _scratch.Add(p);
            _matchedImages.Add(p.Image);
        }
    }

    private static bool Covers(KittyPlacement p, long viewRow, long row, long col) =>
        row >= viewRow && row < viewRow + p.EffRows && col >= p.AnchorCol && col < (long)p.AnchorCol + p.EffCols;

    /// <summary>
    /// Removes the placements in the scratch list; images left unreferenced and anonymous stay queued for the
    /// caller's final <see cref="SweepUnreferenced"/> so uppercase deletes can decide about them first.
    /// </summary>
    private int RemoveScratchPlacementsKeepingSweep()
    {
        int count = _scratch.Count;
        for (int i = 0; i < count; i++)
            RemovePlacement(_scratch[i]);
        _scratch.Clear();
        return count;
    }
}
