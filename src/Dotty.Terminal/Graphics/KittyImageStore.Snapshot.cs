namespace Dotty.Terminal.Graphics;

internal sealed partial class KittyImageStore
{
    /// <summary>
    /// Appends the placements that intersect the viewport to <paramref name="dst"/> (which retains their pixels).
    /// A placement whose top-left cell is at <c>viewRow = AnchorRow - ViewTop + scrollOffset</c> is included when
    /// <c>viewRow + EffRows &gt; 0 &amp;&amp; viewRow &lt; rows</c>. Normal placements only: virtual and relative ones
    /// are resolved by later stages. Must run under the same lock hold as the cell capture it accompanies.
    /// </summary>
    /// <param name="rows">Visible rows.</param>
    /// <param name="scrollOffset">Rows the viewport is scrolled back into the history (0 = live screen).</param>
    /// <returns>Placements appended.</returns>
    public int CaptureVisible(KittyRenderSnapshot dst, int rows, int scrollOffset)
    {
        System.ArgumentNullException.ThrowIfNull(dst);
        if (_placements.Count == 0 || rows <= 0)
            return 0;

        int added = 0;
        foreach (var p in _placements.Values)
        {
            if (p.Kind != PlacementKind.Normal)
                continue;
            long viewRow = p.AnchorRow - _viewTop + scrollOffset;
            var g = p.Geometry;
            if (viewRow + g.EffRows <= 0 || viewRow >= rows)
                continue;

            var pixels = p.Image.Root;
            float iw = pixels.Width, ih = pixels.Height;
            dst.Add(new KittyRenderPlacement
            {
                Pixels = pixels,
                ImageSerial = pixels.Serial,
                ViewRow = (int)viewRow,
                Col = p.AnchorCol,
                Cols = g.EffCols,
                Rows = g.EffRows,
                OffXCells = (float)g.XOff / g.BasisCellW,
                OffYCells = (float)g.YOff / g.BasisCellH,
                U0 = g.SrcX / iw,
                V0 = g.SrcY / ih,
                U1 = (g.SrcX + g.SrcW) / iw,
                V1 = (g.SrcY + g.SrcH) / ih,
                NativeSize = g.NativeSize,
                NativeW = g.NativeSize ? (int)System.MathF.Round(g.DestW) : 0,
                NativeH = g.NativeSize ? (int)System.MathF.Round(g.DestH) : 0,
                BasisCellW = g.BasisCellW,
                BasisCellH = g.BasisCellH,
                Z = p.Z,
                ImageId = p.Image.ClientId,
                PlacementId = p.ClientId,
            });
            added++;
        }
        return added;
    }
}
