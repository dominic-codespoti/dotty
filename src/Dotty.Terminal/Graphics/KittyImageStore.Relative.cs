using System;

namespace Dotty.Terminal.Graphics;

// Relative placements (P/Q/H/V): validation and the parent-chain walk. A relative placement stores only its parent
// link and offsets; its position is derived from the chain each time it is captured (see Snapshot.cs), so the scroll,
// reflow and prune notifications never move or crop it, and it can never be moved twice. Its lifetime is the
// parent's: RemovePlacement cascades.
internal sealed partial class KittyImageStore
{
    /// <summary>Longest parent chain: a placement may be this many links below its root (a root has 0).</summary>
    public const int MaxRelativeDepth = 8;

    /// <summary>
    /// Creates a placement of <paramref name="image"/> positioned relative to the placement
    /// <c>(P image id, Q placement id)</c> by <c>H</c> columns and <c>V</c> rows (either sign) from the parent's
    /// top-left cell. <c>Q</c> = 0 selects the oldest placement of the parent image. Never moves the cursor. As
    /// <see cref="TryPlace"/>, an existing <c>(image, p)</c> placement is replaced and keeps its children.
    /// </summary>
    /// <returns>
    /// <see cref="KittyStatus.Ok"/>; <see cref="KittyStatus.ImageNotFound"/>;
    /// <see cref="KittyStatus.VirtualRelative"/> for <c>U=1</c>; <see cref="KittyStatus.SelfParent"/> when the parent
    /// is the placement being created (same image id and placement id);
    /// <see cref="KittyStatus.NoParent"/> when the parent image is unknown, has no placement, or has none with
    /// id <c>Q</c>; <see cref="KittyStatus.Cycle"/> when the parent descends from the placement being replaced;
    /// <see cref="KittyStatus.TooDeep"/> when the new chain (including the replaced placement's own descendants,
    /// which move below the new placement) would exceed <see cref="MaxRelativeDepth"/> links; otherwise the
    /// errors of <see cref="TryPlace"/>.
    /// </returns>
    public KittyStatus TryPlaceRelative(KittyImage image, in KittyGraphicsCommand cmd, int cellPixelWidth, int cellPixelHeight,
        out KittyPlacement? placement)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        placement = null;
        if (!_byInternalId.TryGetValue(image.InternalId, out var live) || !ReferenceEquals(live, image))
            return KittyStatus.ImageNotFound;
        if (cmd.Unicode == 1)
            return KittyStatus.VirtualRelative;
        if (image.ClientId != 0 && cmd.ParentImage == image.ClientId && cmd.ParentPlacement == cmd.PlacementId)
            return KittyStatus.SelfParent;

        var parentImage = FindByClientId(cmd.ParentImage);
        if (parentImage == null || parentImage.PlacementCount == 0)
            return KittyStatus.NoParent;
        var parent = cmd.ParentPlacement != 0 ? FindPlacement(parentImage, cmd.ParentPlacement) : OldestPlacementOf(parentImage);
        if (parent == null)
            return KittyStatus.NoParent;

        uint placementId = image.ClientId != 0 ? cmd.PlacementId : 0;
        var replaced = FindPlacement(image, placementId);
        if (replaced != null && IsSelfOrAncestor(replaced, parent))
            return KittyStatus.Cycle;

        int parentDepth = 0;
        for (var step = parent; step.Parent != null && parentDepth <= MaxRelativeDepth; step = step.Parent)
            parentDepth++;
        int below = replaced != null ? SubtreeHeight(replaced, 0) : 0;
        if (parentDepth + 1 + below > MaxRelativeDepth)
            return KittyStatus.TooDeep;

        return TryPlaceCore(image, in cmd, PlacementKind.Relative, parent, cmd.ParentH, cmd.ParentV, 0, 0,
            cellPixelWidth, cellPixelHeight, out placement);
    }

    private static KittyPlacement? OldestPlacementOf(KittyImage image)
    {
        KittyPlacement? best = null;
        var list = image.PlacementList;
        if (list == null)
            return null;
        for (int i = 0; i < list.Count; i++)
        {
            if (best == null || list[i].InternalId < best.InternalId)
                best = list[i];
        }
        return best;
    }

    /// <summary>Links from <paramref name="p"/> down to its deepest descendant (0 for a leaf).</summary>
    private static int SubtreeHeight(KittyPlacement p, int guard)
    {
        var children = p.Children;
        if (children == null || guard > MaxRelativeDepth + 1)
            return 0;
        int height = 0;
        for (int i = 0; i < children.Count; i++)
            height = Math.Max(height, 1 + SubtreeHeight(children[i], guard + 1));
        return height;
    }

    /// <summary>
    /// Walks <paramref name="p"/>'s parent chain (at most <see cref="MaxRelativeDepth"/> links) and returns its root,
    /// with the summed offsets: the placement's top-left cell is the root's plus (<paramref name="dx"/>,
    /// <paramref name="dy"/>). A placement that is not relative is its own root with zero offsets. Null when the
    /// chain is longer than the limit (cannot happen for a store that passed validation).
    /// </summary>
    private static KittyPlacement? FindRelativeRoot(KittyPlacement p, out long dx, out long dy)
    {
        dx = 0;
        dy = 0;
        var step = p;
        for (int hops = 0; step.Parent != null; hops++)
        {
            if (hops == MaxRelativeDepth)
                return null;
            dx += step.ParentDx;
            dy += step.ParentDy;
            step = step.Parent;
        }
        return step;
    }
}
