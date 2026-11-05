using System;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;

namespace Dotty.Terminal.Graphics;

/// <summary>
/// The cell data <see cref="PlaceholderResolver"/> reads: rows of hot and cold cells plus the style table. A
/// <see cref="RenderSnapshot"/> is one (through <see cref="RenderSnapshotPlaceholderSource"/>); tests supply plain arrays.
/// </summary>
public interface IPlaceholderSource
{
    int Rows { get; }
    int Columns { get; }

    /// <summary>Hot cells of one visible row (empty for an out-of-range row).</summary>
    ReadOnlySpan<CellHot> GetRowCells(int row);

    /// <summary>Cold cells of one visible row, parallel to <see cref="GetRowCells"/> (may be shorter; missing cells are plain).</summary>
    ReadOnlySpan<ColdCell> GetRowColdCells(int row);

    ref readonly CellAttributes GetStyle(ushort styleId);
}

/// <summary><see cref="IPlaceholderSource"/> over a captured <see cref="RenderSnapshot"/>.</summary>
public readonly struct RenderSnapshotPlaceholderSource : IPlaceholderSource
{
    private readonly RenderSnapshot _snapshot;

    public RenderSnapshotPlaceholderSource(RenderSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public int Rows => _snapshot.Rows;
    public int Columns => _snapshot.Columns;
    public ReadOnlySpan<CellHot> GetRowCells(int row) => _snapshot.GetRowCells(row);
    public ReadOnlySpan<ColdCell> GetRowColdCells(int row) => _snapshot.GetRowColdCells(row);
    public ref readonly CellAttributes GetStyle(ushort styleId) => ref _snapshot.GetStyle(styleId);
}

/// <summary>
/// Turns Unicode-placeholder cells (<c>U+10EEEE</c> + up to three diacritics) into image quads. Runs on the snapshot
/// copy, outside the buffer lock, and does nothing at all when the snapshot holds no virtual placement.
/// </summary>
/// <remarks>
/// <para>Identity of a cell. Image id = the foreground colour (palette index, or 24-bit RGB) with the high byte
/// <c>msb &lt;&lt; 24</c> from the third diacritic; placement id = the underline colour (index or RGB, none = 0, which
/// selects the virtual placement of the image with the lowest placement id). A cell that names no image (default
/// foreground, id 0, high byte above 255) or no virtual placement draws nothing.</para>
/// <para>Inheritance, applied left to right against the placeholder cell directly to the left (kitty's three
/// rules). With the same foreground and underline colours as that cell: (1) no diacritics: same row, column + 1,
/// same high byte; (2) only the row: when the rows are equal, column + 1 and the same high byte; (3) row and column
/// only: when the rows are equal and the column is the left cell's + 1, the same high byte. Whatever remains missing
/// is 0.</para>
/// <para>Drawing. Cell <c>(row, col)</c> of the <c>c × r</c> grid shows the part of the image that lies in that
/// cell when the source rectangle is fitted into the grid with its aspect ratio kept and centred
/// (<see cref="PlacementGeometry.FitIntoBox"/>); cells outside the grid, or outside the fitted rectangle, draw nothing.
/// Horizontally adjacent cells of one grid row become one quad. Quads sit at z 0 (above the text) and use the
/// native-size encoding of <see cref="KittyRenderPlacement"/> with <see cref="Basis"/> as basis cell size, which lets a
/// quad cover a fraction of a cell.</para>
/// </remarks>
public static class PlaceholderResolver
{
    /// <summary>Basis cell size used to encode fractional cell extents in <see cref="KittyRenderPlacement.NativeW"/>/<see cref="KittyRenderPlacement.NativeH"/>.</summary>
    public const int Basis = 4096;

    private const uint KindIndexed = 1u << 24;
    private const uint KindRgb = 2u << 24;

    /// <summary>Resolves the placeholders of a captured <see cref="RenderSnapshot"/>; see the generic overload.</summary>
    public static int Resolve(RenderSnapshot source, KittyRenderSnapshot graphics, float cellWidth, float cellHeight,
        List<KittyRenderPlacement> output)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Resolve(new RenderSnapshotPlaceholderSource(source), graphics, cellWidth, cellHeight, output);
    }

    /// <summary>
    /// Appends the quads for every placeholder cell of <paramref name="source"/> that names one of
    /// <paramref name="graphics"/>'s virtual placements, followed by the relative placements rooted at those virtual
    /// placements (positioned from the screen position of the grid's cell (0, 0), inferred from every visible placeholder cell of the
    /// grid so it does not move while the grid's top rows scroll off; none when no placeholder was found).
    /// The appended placements reference pixels the snapshot keeps alive.
    /// </summary>
    /// <param name="cellWidth">Cell width in pixels (only its ratio to the height matters).</param>
    /// <param name="cellHeight">Cell height in pixels.</param>
    /// <returns>Placements appended to <paramref name="output"/>.</returns>
    public static int Resolve<TSource>(TSource source, KittyRenderSnapshot graphics, float cellWidth, float cellHeight,
        List<KittyRenderPlacement> output)
        where TSource : IPlaceholderSource
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(output);
        var virtuals = graphics.Virtuals;
        if (virtuals.IsEmpty || !(cellWidth > 0f) || !(cellHeight > 0f) || !float.IsFinite(cellWidth) || !float.IsFinite(cellHeight))
            return 0;

        int start = output.Count;
        int rows = source.Rows, columns = source.Columns;
        var children = graphics.VirtualChildren;
        int[]? originRow = null, originCol = null;
        if (!children.IsEmpty)
        {
            // Reused per thread: the resolver runs once per pane per frame, so nothing is allocated per frame.
            originRow = s_originRow;
            if (originRow == null || originRow.Length < virtuals.Length)
                s_originRow = originRow = new int[Math.Max(virtuals.Length, 8)];
            originCol = s_originCol;
            if (originCol == null || originCol.Length < virtuals.Length)
                s_originCol = originCol = new int[Math.Max(virtuals.Length, 8)];
            Array.Fill(originRow, int.MaxValue, 0, virtuals.Length);
            Array.Fill(originCol, int.MaxValue, 0, virtuals.Length);
        }

        uint cachedImage = 0, cachedPlacement = 0;
        int cachedIndex = -2;
        for (int row = 0; row < rows; row++)
        {
            var cells = source.GetRowCells(row);
            var cold = source.GetRowColdCells(row);
            int width = Math.Min(columns, cells.Length);
            var run = new Run { V = -1 };
            var inherit = default(Inherit);

            for (int column = 0; column < width; column++)
            {
                ref readonly CellHot cell = ref cells[column];
                if (cell.Rune != PlaceholderTable.PlaceholderRune)
                {
                    inherit.Have = false;
                    continue;
                }

                DecodedCell decoded = Decode(ref inherit, in source.GetStyle(cell.StyleId), cold, column);
                int r = decoded.Row, c = decoded.Col;

                if (decoded.FgKind == ColorKind.Default || decoded.Msb > 255)
                {
                    FlushRun(ref run, virtuals, cellWidth, cellHeight, output);
                    continue;
                }
                uint imageId = decoded.FgValue | ((uint)decoded.Msb << 24);
                if (imageId == 0)
                {
                    FlushRun(ref run, virtuals, cellWidth, cellHeight, output);
                    continue;
                }
                uint placementId = decoded.UlValue;

                int v;
                if (cachedIndex != -2 && imageId == cachedImage && placementId == cachedPlacement)
                    v = cachedIndex;
                else
                {
                    v = FindVirtual(virtuals, imageId, placementId);
                    cachedImage = imageId;
                    cachedPlacement = placementId;
                    cachedIndex = v;
                }
                if (v < 0)
                {
                    FlushRun(ref run, virtuals, cellWidth, cellHeight, output);
                    continue;
                }

                ref readonly KittyVirtualPlacement virtualPlacement = ref virtuals[v];
                if ((uint)r >= (uint)virtualPlacement.Rows || (uint)c >= (uint)virtualPlacement.Cols)
                {
                    FlushRun(ref run, virtuals, cellWidth, cellHeight, output);
                    continue;
                }
                if (originRow != null && originCol != null)
                {
                    // Where the grid's cell (0, 0) is (or would be) on this screen, from every visible cell of the
                    // grid; the minima are taken independently. Stable while the top rows of the grid scroll away.
                    if (row - r < originRow[v]) originRow[v] = row - r;
                    if (column - c < originCol[v]) originCol[v] = column - c;
                }

                if (run.V == v && run.GridRow == r && run.GridCol + run.Count == c && run.Col + run.Count == column)
                {
                    run.Count++;
                    continue;
                }
                FlushRun(ref run, virtuals, cellWidth, cellHeight, output);
                run = new Run { V = v, Row = row, Col = column, GridRow = r, GridCol = c, Count = 1 };
            }
            FlushRun(ref run, virtuals, cellWidth, cellHeight, output);
        }

        if (originRow != null && originCol != null)
        {
            for (int i = 0; i < children.Length; i++)
            {
                int v = IndexOfInternalId(virtuals, children[i].VirtualInternalId);
                if (v < 0 || originRow[v] == int.MaxValue)
                    continue;
                KittyRenderPlacement placement = children[i].Placement;
                long viewRow = (long)originRow[v] + placement.ViewRow;
                long col = (long)originCol[v] + placement.Col;
                if (viewRow + placement.Rows <= 0 || viewRow >= rows)
                    continue;
                output.Add(placement with
                {
                    ViewRow = (int)viewRow,
                    Col = (int)Math.Clamp(col, -(1L << 30), 1L << 30),
                });
            }
        }
        return output.Count - start;
    }

    /// <summary>
    /// Adds to <paramref name="imageIds"/> the id of every image some placeholder cell of <paramref name="source"/>
    /// names (identity and inheritance exactly as <see cref="Resolve{TSource}"/>; whether a virtual placement, its
    /// grid or its placement id exists is not looked at). Lets the store tell which animated images a screen shows
    /// through placeholders.
    /// </summary>
    public static void CollectImageIds<TSource>(TSource source, HashSet<uint> imageIds)
        where TSource : IPlaceholderSource
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        int rows = source.Rows, columns = source.Columns;
        for (int row = 0; row < rows; row++)
        {
            var cells = source.GetRowCells(row);
            var cold = source.GetRowColdCells(row);
            int width = Math.Min(columns, cells.Length);
            var inherit = default(Inherit);
            for (int column = 0; column < width; column++)
            {
                ref readonly CellHot cell = ref cells[column];
                if (cell.Rune != PlaceholderTable.PlaceholderRune)
                {
                    inherit.Have = false;
                    continue;
                }
                DecodedCell decoded = Decode(ref inherit, in source.GetStyle(cell.StyleId), cold, column);
                if (decoded.FgKind == ColorKind.Default || decoded.Msb > 255)
                    continue;
                uint imageId = decoded.FgValue | ((uint)decoded.Msb << 24);
                if (imageId != 0)
                    imageIds.Add(imageId);
            }
        }
    }

    [ThreadStatic] private static int[]? s_originRow;
    [ThreadStatic] private static int[]? s_originCol;

    /// <summary>The placeholder cell to the left, as far as inheritance needs it.</summary>
    private struct Inherit
    {
        public bool Have;
        public int Row, Col, Msb;
        public uint Fg, Ul;
    }

    private struct DecodedCell
    {
        public int Row, Col, Msb;
        public ColorKind FgKind;
        public uint FgValue, UlValue;
    }

    /// <summary>
    /// Identity of one placeholder cell: its diacritics, the colours, and the three inheritance rules against
    /// <paramref name="left"/> (which is updated to this cell). A non-placeholder cell must clear
    /// <see cref="Inherit.Have"/> instead.
    /// </summary>
    private static DecodedCell Decode(ref Inherit left, in CellAttributes style, ReadOnlySpan<ColdCell> cold, int column)
    {
        PlaceholderIndices indices = PlaceholderIndices.None;
        if (column < cold.Length)
        {
            short code = cold[column].GraphemeIndex;
            if (code <= PlaceholderTable.AbsentCode)
                PlaceholderTable.TryGet(code, out indices);
        }

        ColorKind fgKind = style.ForegroundKind;
        ColorKind ulKind = style.UnderlineColorKind;
        uint fgValue = ColorValue(fgKind, style.ForegroundIndex, style.Foreground.Argb);
        uint ulValue = ColorValue(ulKind, style.UnderlineColorIndex, style.UnderlineColor.Argb);
        uint fgKey = fgValue | KindOf(fgKind);
        uint ulKey = ulValue | KindOf(ulKind);

        int r = indices.HasRow ? indices.Row : 0;
        int c = indices.HasCol ? indices.Col : 0;
        int m = indices.HasMsb ? indices.Msb : 0;
        if (left.Have && fgKey == left.Fg && ulKey == left.Ul)
        {
            if (!indices.HasRow && !indices.HasCol && !indices.HasMsb)
            {
                r = left.Row;
                c = left.Col + 1;
                m = left.Msb;
            }
            else if (indices.HasRow && !indices.HasCol && !indices.HasMsb)
            {
                if (left.Row == r)
                {
                    c = left.Col + 1;
                    m = left.Msb;
                }
            }
            else if (indices.HasRow && indices.HasCol && !indices.HasMsb)
            {
                if (left.Row == r && left.Col + 1 == c)
                    m = left.Msb;
            }
        }
        left = new Inherit { Have = true, Row = r, Col = c, Msb = m, Fg = fgKey, Ul = ulKey };
        return new DecodedCell { Row = r, Col = c, Msb = m, FgKind = fgKind, FgValue = fgValue, UlValue = ulValue };
    }

    private struct Run
    {
        public int V;
        public int Row, Col;
        public int GridRow, GridCol, Count;
    }

    private static uint KindOf(ColorKind kind) => kind switch
    {
        ColorKind.Indexed => KindIndexed,
        ColorKind.Rgb => KindRgb,
        _ => 0u,
    };

    /// <summary>The colour's numeric value: palette index, 24-bit RGB, or 0 for the default.</summary>
    private static uint ColorValue(ColorKind kind, int index, uint argb) => kind switch
    {
        ColorKind.Indexed => (uint)index,
        ColorKind.Rgb => argb & 0xFFFFFFu,
        _ => 0u,
    };

    /// <summary>
    /// Index of the virtual placement for (<paramref name="imageId"/>, <paramref name="placementId"/>) in the sorted
    /// <paramref name="virtuals"/>; with placement id 0 the one of the image with the lowest placement id. -1 when none.
    /// </summary>
    private static int FindVirtual(ReadOnlySpan<KittyVirtualPlacement> virtuals, uint imageId, uint placementId)
    {
        int lo = 0, hi = virtuals.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ref readonly KittyVirtualPlacement v = ref virtuals[mid];
            if (v.ImageId < imageId || (v.ImageId == imageId && v.PlacementId < placementId))
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo < virtuals.Length && virtuals[lo].ImageId == imageId && (placementId == 0 || virtuals[lo].PlacementId == placementId))
            return lo;
        return -1;
    }

    private static int IndexOfInternalId(ReadOnlySpan<KittyVirtualPlacement> virtuals, long internalId)
    {
        for (int i = 0; i < virtuals.Length; i++)
        {
            if (virtuals[i].InternalId == internalId)
                return i;
        }
        return -1;
    }

    private static void FlushRun(ref Run run, ReadOnlySpan<KittyVirtualPlacement> virtuals, float cellWidth, float cellHeight,
        List<KittyRenderPlacement> output)
    {
        if (run.V < 0)
            return;
        EmitRun(output, in virtuals[run.V], run.Row, run.Col, run.GridRow, run.GridCol, run.Count, cellWidth, cellHeight);
        run.V = -1;
    }

    /// <summary>
    /// One quad for <paramref name="count"/> horizontally adjacent grid cells starting at grid cell
    /// (<paramref name="gridRow"/>, <paramref name="gridCol"/>), drawn at screen cell (<paramref name="viewRow"/>,
    /// <paramref name="screenCol"/>): the intersection of that cell span with the fitted image rectangle.
    /// </summary>
    private static void EmitRun(List<KittyRenderPlacement> output, in KittyVirtualPlacement v, int viewRow, int screenCol,
        int gridRow, int gridCol, int count, float cellWidth, float cellHeight)
    {
        FitRect fit = PlacementGeometry.FitIntoBox(v.SrcW, v.SrcH, v.Cols, v.Rows, cellWidth, cellHeight);
        double fx = fit.X, fy = fit.Y, fw = fit.W, fh = fit.H;
        if (!(fw > 0) || !(fh > 0))
            return;

        double x0 = (double)gridCol * cellWidth, x1 = x0 + (double)count * cellWidth;
        double y0 = (double)gridRow * cellHeight, y1 = y0 + cellHeight;
        double ix0 = Math.Max(x0, fx), ix1 = Math.Min(x1, fx + fw);
        double iy0 = Math.Max(y0, fy), iy1 = Math.Min(y1, fy + fh);
        if (!(ix1 > ix0) || !(iy1 > iy0))
            return;

        double du = (double)v.U1 - v.U0, dv = (double)v.V1 - v.V0;
        double wCells = (ix1 - ix0) / cellWidth, hCells = (iy1 - iy0) / cellHeight;
        output.Add(new KittyRenderPlacement
        {
            Pixels = v.Pixels,
            ImageSerial = v.ImageSerial,
            ViewRow = viewRow,
            Col = screenCol,
            Cols = count,
            Rows = 1,
            OffXCells = (float)((ix0 - x0) / cellWidth),
            OffYCells = (float)((iy0 - y0) / cellHeight),
            U0 = (float)(v.U0 + du * ((ix0 - fx) / fw)),
            U1 = (float)(v.U0 + du * ((ix1 - fx) / fw)),
            V0 = (float)(v.V0 + dv * ((iy0 - fy) / fh)),
            V1 = (float)(v.V0 + dv * ((iy1 - fy) / fh)),
            NativeSize = true,
            NativeW = Math.Max(1, (int)Math.Round(wCells * Basis)),
            NativeH = Math.Max(1, (int)Math.Round(hCells * Basis)),
            BasisCellW = Basis,
            BasisCellH = Basis,
            Z = 0,
            ImageId = v.ImageId,
            PlacementId = v.PlacementId,
        });
    }
}
