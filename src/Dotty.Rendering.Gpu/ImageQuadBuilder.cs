using System;
using System.Collections.Generic;
using Dotty.Terminal.Graphics;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Pane/scene geometry for image quads (framebuffer pixels). <see cref="PaneColOffset"/>/<see cref="PaneRowOffset"/>
/// are whole cells from the padded origin (the composer's tab-bar rows are part of <see cref="PaneRowOffset"/>).
/// The clip rectangle (left/top inclusive, right/bottom exclusive) is the pane rect ∩ framebuffer.
/// </summary>
public readonly record struct ImageSceneGeometry(
    float PaddingLeft, float PaddingTop,
    int PaneColOffset, int PaneRowOffset,
    float CellW, float CellH,
    float ClipLeft, float ClipTop, float ClipRight, float ClipBottom);

/// <summary>
/// Turns <see cref="KittyRenderPlacement"/>s into clipped, sorted, texture-batched quads. Allocation-free once the
/// list's buffers have grown. Sorting is an in-place heapsort (no delegate/comparer allocation, unlike
/// <c>Array.Sort</c> with an <see cref="IComparer{T}"/>) over a total order whose final key is the input index, so
/// equal (Layer, Z, ImageId, PlacementId) keep input order despite the sort being unstable.
/// </summary>
public static class ImageQuadBuilder
{
    internal const int BelowBackgroundZ = -1_073_741_824;

    internal struct SortEntry
    {
        public int Layer;
        public int Z;
        public uint ImageId;
        public uint PlacementId;
        public int Index;
        public ImagePixels Pixels;
        public long Serial;
        public ImageQuadInstance Quad;
    }

    private static int Compare(in SortEntry a, in SortEntry b)
    {
        if (a.Layer != b.Layer) return a.Layer < b.Layer ? -1 : 1;
        if (a.Z != b.Z) return a.Z < b.Z ? -1 : 1;
        if (a.ImageId != b.ImageId) return a.ImageId < b.ImageId ? -1 : 1;
        if (a.PlacementId != b.PlacementId) return a.PlacementId < b.PlacementId ? -1 : 1;
        return a.Index.CompareTo(b.Index);
    }

    private static void SortEntries(SortEntry[] a, int n)
    {
        for (int i = n / 2 - 1; i >= 0; i--)
            SiftDown(a, i, n);
        for (int end = n - 1; end > 0; end--)
        {
            (a[0], a[end]) = (a[end], a[0]);
            SiftDown(a, 0, end);
        }
    }

    private static void SiftDown(SortEntry[] a, int root, int n)
    {
        while (true)
        {
            int child = 2 * root + 1;
            if (child >= n) return;
            if (child + 1 < n && Compare(in a[child], in a[child + 1]) < 0) child++;
            if (Compare(in a[root], in a[child]) >= 0) return;
            (a[root], a[child]) = (a[child], a[root]);
            root = child;
        }
    }

    public static ImageLayer LayerOf(int z) => z < BelowBackgroundZ ? ImageLayer.BelowBackground : z < 0 ? ImageLayer.BelowText : ImageLayer.AboveText;

    /// <summary>
    /// Appends quads for <paramref name="src"/> to <paramref name="dst"/>, keeping <see cref="ImageDrawList.Draws"/>
    /// grouped by layer across multiple calls (e.g. one per pane). Each emitted <see cref="ImageDraw"/> holds one
    /// <see cref="ImagePixels.Retain"/> lease.
    /// </summary>
    public static void Append(ImageDrawList dst, ReadOnlySpan<KittyRenderPlacement> src, in ImageSceneGeometry g)
    {
        ArgumentNullException.ThrowIfNull(dst);
        if (src.IsEmpty || !(g.CellW > 0f) || !(g.CellH > 0f) || !float.IsFinite(g.CellW) || !float.IsFinite(g.CellH)
            || !float.IsFinite(g.PaddingLeft) || !float.IsFinite(g.PaddingTop))
            return;

        if (dst.Scratch.Length < src.Length)
            dst.Scratch = new SortEntry[Math.Max(src.Length, dst.Scratch.Length * 2)];
        SortEntry[] scratch = dst.Scratch;

        int n = 0;
        for (int i = 0; i < src.Length; i++)
        {
            ref readonly KittyRenderPlacement p = ref src[i];
            if (p.Pixels is null || !TryBuildQuad(in p, in g, out ImageQuadInstance quad))
                continue;
            scratch[n++] = new SortEntry
            {
                Layer = (int)LayerOf(p.Z), Z = p.Z, ImageId = p.ImageId, PlacementId = p.PlacementId,
                Index = i, Pixels = p.Pixels, Serial = p.ImageSerial, Quad = quad,
            };
        }
        if (n == 0) return;

        if (n > 1)
            SortEntries(scratch, n);

        if (dst.Quads.Length < dst.QuadCount + n)
            Array.Resize(ref dst.Quads, Math.Max(dst.QuadCount + n, dst.Quads.Length * 2));

        int runStart = 0;
        for (int i = 0; i < n; i++)
        {
            dst.Quads[dst.QuadCount + i] = scratch[i].Quad;
            bool endOfRun = i == n - 1
                || scratch[i + 1].Layer != scratch[i].Layer
                || scratch[i + 1].Serial != scratch[i].Serial;
            if (!endOfRun) continue;
            InsertDraw(dst, new ImageDraw(scratch[runStart].Pixels.Retain(), dst.QuadCount + runStart, i - runStart + 1, (ImageLayer)scratch[runStart].Layer));
            runStart = i + 1;
        }
        dst.QuadCount += n;
        Array.Clear(scratch, 0, n); // drop pixel references held by scratch
    }

    private static void InsertDraw(ImageDrawList dst, in ImageDraw draw)
    {
        if (dst.DrawCount == dst.Draws.Length)
            Array.Resize(ref dst.Draws, dst.Draws.Length * 2);
        int layer = (int)draw.Layer;
        int pos = dst.LayerDrawStart[layer + 1];
        if (pos < dst.DrawCount)
            Array.Copy(dst.Draws, pos, dst.Draws, pos + 1, dst.DrawCount - pos);
        dst.Draws[pos] = draw;
        dst.DrawCount++;
        for (int j = layer + 1; j < dst.LayerDrawStart.Length; j++)
            dst.LayerDrawStart[j]++;
    }

    private static bool TryBuildQuad(in KittyRenderPlacement p, in ImageSceneGeometry g, out ImageQuadInstance quad)
    {
        quad = default;
        float cw = g.CellW, ch = g.CellH;

        double w, h;
        if (p.NativeSize)
        {
            if (p.BasisCellW <= 0 || p.BasisCellH <= 0) return false;
            w = (double)p.NativeW * cw / p.BasisCellW;
            h = (double)p.NativeH * ch / p.BasisCellH;
        }
        else
        {
            w = (double)p.Cols * cw;
            h = (double)p.Rows * ch;
        }

        double x = Math.Floor(g.PaddingLeft + ((double)g.PaneColOffset + p.Col) * cw + (double)p.OffXCells * cw);
        double y = Math.Floor(g.PaddingTop + ((double)g.PaneRowOffset + p.ViewRow) * ch + (double)p.OffYCells * ch);

        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
            return false;
        if (!float.IsFinite(p.U0) || !float.IsFinite(p.V0) || !float.IsFinite(p.U1) || !float.IsFinite(p.V1))
            return false;

        double x0 = Math.Max(x, g.ClipLeft), x1 = Math.Min(x + w, g.ClipRight);
        double y0 = Math.Max(y, g.ClipTop), y1 = Math.Min(y + h, g.ClipBottom);
        if (!(x1 > x0) || !(y1 > y0)) return false;

        double du = (double)p.U1 - p.U0, dv = (double)p.V1 - p.V0;
        quad = new ImageQuadInstance
        {
            X = (float)x0, Y = (float)y0, W = (float)(x1 - x0), H = (float)(y1 - y0),
            U0 = (float)(p.U0 + du * ((x0 - x) / w)),
            U1 = (float)(p.U0 + du * ((x1 - x) / w)),
            V0 = (float)(p.V0 + dv * ((y0 - y) / h)),
            V1 = (float)(p.V0 + dv * ((y1 - y) / h)),
            Alpha = 1f,
        };
        return quad.W > 0f && quad.H > 0f && float.IsFinite(quad.X) && float.IsFinite(quad.Y)
            && float.IsFinite(quad.W) && float.IsFinite(quad.H);
    }
}
