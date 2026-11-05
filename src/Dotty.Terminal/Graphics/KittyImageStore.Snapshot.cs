using System;

namespace Dotty.Terminal.Graphics;

internal sealed partial class KittyImageStore
{
    /// <summary>
    /// Appends what the renderer needs of this store's placements to <paramref name="dst"/> (which retains their
    /// pixels). Must run under the same lock hold as the cell capture it accompanies.
    /// <list type="bullet">
    /// <item>Normal placements: a placement whose top-left cell is at <c>viewRow = AnchorRow - ViewTop + scrollOffset</c>
    /// is included when <c>viewRow + EffRows &gt; 0 &amp;&amp; viewRow &lt; rows</c>.</item>
    /// <item>Relative placements: the parent chain is walked (at most <see cref="MaxRelativeDepth"/> links) and the
    /// position is the root's plus the summed <c>H</c>/<c>V</c> offsets (negatives allowed), tested for visibility
    /// like a normal placement. A chain rooted at a virtual placement is captured as a
    /// <see cref="KittyVirtualChild"/> for the placeholder resolver.</item>
    /// <item>Virtual placements (those of images with a client id; nothing else can name them) go to
    /// <see cref="KittyRenderSnapshot.Virtuals"/>, visible or not.</item>
    /// </list>
    /// </summary>
    /// <param name="rows">Visible rows.</param>
    /// <param name="scrollOffset">Rows the viewport is scrolled back into the history (0 = live screen).</param>
    /// <returns>Placements appended to <see cref="KittyRenderSnapshot.Placements"/> (virtual placements and their children are not counted).</returns>
    public int CaptureVisible(KittyRenderSnapshot dst, int rows, int scrollOffset)
    {
        ArgumentNullException.ThrowIfNull(dst);
        if (_placements.Count == 0 || rows <= 0)
            return 0;

        int added = 0;
        bool addedVirtual = false;
        foreach (var p in _placements.Values)
        {
            switch (p.Kind)
            {
                case PlacementKind.Normal:
                    {
                        long viewRow = p.AnchorRow - _viewTop + scrollOffset;
                        if (viewRow + p.Geometry.EffRows <= 0 || viewRow >= rows)
                            continue;
                        dst.Add(Render(p, viewRow, p.AnchorCol));
                        added++;
                        break;
                    }
                case PlacementKind.Relative:
                    {
                        var root = FindRelativeRoot(p, out long dx, out long dy);
                        if (root == null)
                            continue;
                        if (root.Kind == PlacementKind.Virtual)
                        {
                            if (root.Image.ClientId == 0)
                                continue;
                            dst.AddVirtualChild(new KittyVirtualChild
                            {
                                Placement = Render(p, ClampOffset(dy), ClampOffset(dx)),
                                VirtualInternalId = root.InternalId,
                            });
                            break;
                        }
                        long viewRow = root.AnchorRow - _viewTop + scrollOffset + dy;
                        if (viewRow + p.Geometry.EffRows <= 0 || viewRow >= rows)
                            continue;
                        dst.Add(Render(p, viewRow, ClampOffset(root.AnchorCol + dx)));
                        added++;
                        break;
                    }
                default:
                    {
                        if (p.Image.ClientId == 0)
                            continue;
                        var g = p.Geometry;
                        var pixels = p.Image.Root;
                        float iw = pixels.Width, ih = pixels.Height;
                        dst.AddVirtual(new KittyVirtualPlacement
                        {
                            Pixels = pixels,
                            ImageSerial = pixels.Serial,
                            ImageId = p.Image.ClientId,
                            PlacementId = p.ClientId,
                            InternalId = p.InternalId,
                            Cols = g.EffCols,
                            Rows = g.EffRows,
                            SrcW = g.SrcW,
                            SrcH = g.SrcH,
                            U0 = g.SrcX / iw,
                            V0 = g.SrcY / ih,
                            U1 = (g.SrcX + g.SrcW) / iw,
                            V1 = (g.SrcY + g.SrcH) / ih,
                        });
                        addedVirtual = true;
                        break;
                    }
            }
        }
        if (addedVirtual)
            dst.SortVirtuals();
        return added;
    }

    /// <summary>Keeps a summed offset far inside the int range (it is only ever added to small cell coordinates).</summary>
    private static int ClampOffset(long value) => (int)Math.Clamp(value, -(1L << 30), 1L << 30);

    private static KittyRenderPlacement Render(KittyPlacement p, long viewRow, int col)
    {
        var g = p.Geometry;
        var pixels = p.Image.Root;
        float iw = pixels.Width, ih = pixels.Height;
        return new KittyRenderPlacement
        {
            Pixels = pixels,
            ImageSerial = pixels.Serial,
            ViewRow = (int)viewRow,
            Col = col,
            Cols = g.EffCols,
            Rows = g.EffRows,
            OffXCells = (float)g.XOff / g.BasisCellW,
            OffYCells = (float)g.YOff / g.BasisCellH,
            U0 = g.SrcX / iw,
            V0 = g.SrcY / ih,
            U1 = (g.SrcX + g.SrcW) / iw,
            V1 = (g.SrcY + g.SrcH) / ih,
            NativeSize = g.NativeSize,
            NativeW = g.NativeSize ? (int)MathF.Round(g.DestW) : 0,
            NativeH = g.NativeSize ? (int)MathF.Round(g.DestH) : 0,
            BasisCellW = g.BasisCellW,
            BasisCellH = g.BasisCellH,
            Z = p.Z,
            ImageId = p.Image.ClientId,
            PlacementId = p.ClientId,
        };
    }
}
