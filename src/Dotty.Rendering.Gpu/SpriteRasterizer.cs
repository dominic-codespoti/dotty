using System;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Pure rasteriser of built-in cell glyphs (<c>font.builtinGlyphs</c>) into A8 sprites, so prompts and TUIs draw
/// pixel-exact without a font: box drawing U+2500-257F (light, heavy, double, dashed, rounded arcs, diagonals),
/// block elements U+2580-259F (eighths, halves, shades, quadrants), braille U+2800-28FF, sextants
/// U+1FB00-1FB3B, octants U+1CD00-1CDE5 and Powerline U+E0B0-E0BF.
/// <para>
/// Integer edge rules make neighbouring cells meet exactly: a stroke of thickness <c>t</c> starts at
/// <c>(extent - t) / 2</c> on its axis and reaches the cell edge, eighths/quarters/thirds of the cell are
/// <c>round(k × extent / n)</c>, and every pattern depends only on the cell size. The light stroke is
/// <see cref="LightThickness"/> = max(1, round(cellWidth / 8)); heavy is twice that; a double line is two light
/// strokes with a light-stroke gap.
/// </para>
/// </summary>
public static class SpriteRasterizer
{
    // Box drawing U+2500-257F, generated from the Unicode 16 character names: arm weights up (bits 0-1), right
    // (2-3), down (4-5), left (6-7) with 0 none, 1 light, 2 heavy, 3 double; dash count (bits 8-10); rounded arc
    // (bit 11); diagonals (bits 12-13: 1 = upper right to lower left, 2 = upper left to lower right).
    private static ReadOnlySpan<ushort> BoxTable => new ushort[]
    {
        0x0044, 0x0088, 0x0011, 0x0022, 0x0344, 0x0388, 0x0311, 0x0322,
        0x0444, 0x0488, 0x0411, 0x0422, 0x0014, 0x0018, 0x0024, 0x0028,
        0x0050, 0x0090, 0x0060, 0x00A0, 0x0005, 0x0009, 0x0006, 0x000A,
        0x0041, 0x0081, 0x0042, 0x0082, 0x0015, 0x0019, 0x0016, 0x0025,
        0x0026, 0x001A, 0x0029, 0x002A, 0x0051, 0x0091, 0x0052, 0x0061,
        0x0062, 0x0092, 0x00A1, 0x00A2, 0x0054, 0x0094, 0x0058, 0x0098,
        0x0064, 0x00A4, 0x0068, 0x00A8, 0x0045, 0x0085, 0x0049, 0x0089,
        0x0046, 0x0086, 0x004A, 0x008A, 0x0055, 0x0095, 0x0059, 0x0099,
        0x0056, 0x0065, 0x0066, 0x0096, 0x005A, 0x00A5, 0x0069, 0x009A,
        0x00A9, 0x00A6, 0x006A, 0x00AA, 0x0244, 0x0288, 0x0211, 0x0222,
        0x00CC, 0x0033, 0x001C, 0x0034, 0x003C, 0x00D0, 0x0070, 0x00F0,
        0x000D, 0x0007, 0x000F, 0x00C1, 0x0043, 0x00C3, 0x001D, 0x0037,
        0x003F, 0x00D1, 0x0073, 0x00F3, 0x00DC, 0x0074, 0x00FC, 0x00CD,
        0x0047, 0x00CF, 0x00DD, 0x0077, 0x00FF, 0x0814, 0x0850, 0x0841,
        0x0805, 0x1000, 0x2000, 0x3000, 0x0040, 0x0001, 0x0004, 0x0010,
        0x0080, 0x0002, 0x0008, 0x0020, 0x0048, 0x0021, 0x0084, 0x0012,
    };

    // Octants U+1CD00-1CDE5 (Unicode 16, generated from the names "BLOCK OCTANT-n…"): bit k-1 set for octant k,
    // numbered 1 2 / 3 4 / 5 6 / 7 8 from the top. Patterns that already exist elsewhere (blocks, quadrants,
    // sextant-equivalent halves) have no code point here.
    private static ReadOnlySpan<byte> OctantTable => new byte[]
    {
        0x04, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x0C, 0x0D, 0x0E, 0x10, 0x11, 0x12, 0x13, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
        0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x51, 0x52, 0x53, 0x54, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C, 0x5D,
        0x5E, 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E,
        0x6F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E,
        0x7F, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
        0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
        0xA1, 0xA2, 0xA3, 0xA4, 0xA6, 0xA7, 0xA8, 0xA9, 0xAB, 0xAC, 0xAD, 0xAE, 0xB0, 0xB1, 0xB2, 0xB3,
        0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC1, 0xC2, 0xC3, 0xC4,
        0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF, 0xD0, 0xD1, 0xD2, 0xD3, 0xD4,
        0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE0, 0xE1, 0xE2, 0xE3, 0xE4,
        0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF, 0xF1, 0xF2, 0xF3, 0xF4, 0xF6,
        0xF7, 0xF8, 0xF9, 0xFB, 0xFD, 0xFE,
    };

    private const int Light = 1, Heavy = 2, Double = 3;

    /// <summary>True for code points this rasteriser draws.</summary>
    public static bool IsSupported(int codePoint) =>
        codePoint is >= 0x2500 and <= 0x259F
        || codePoint is >= 0x2800 and <= 0x28FF
        || codePoint is >= 0x1FB00 and <= 0x1FB3B
        || codePoint is >= 0x1CD00 and <= 0x1CDE5
        || codePoint is >= 0xE0B0 and <= 0xE0BF;

    /// <summary>Light stroke thickness for a cell width: max(1, round(width / 8)).</summary>
    public static int LightThickness(int cellWidth) => Math.Max(1, (cellWidth + 4) / 8);

    /// <summary>
    /// Rasterises <paramref name="codePoint"/> into a <paramref name="width"/> × <paramref name="height"/> sprite placed
    /// at the cell origin, or returns null when the code point is not supported or the size is empty.
    /// <paramref name="cellWidth"/> (default: <paramref name="width"/>) sets the stroke thickness, so a glyph
    /// spanning two cells keeps the one-cell strokes.
    /// </summary>
    public static SpriteBitmap? Rasterize(int codePoint, int width, int height, int cellWidth = 0)
    {
        if (width <= 0 || height <= 0 || !IsSupported(codePoint))
            return null;
        var sprite = new SpriteBitmap(width, height);
        int light = LightThickness(cellWidth > 0 ? cellWidth : width);
        if (codePoint <= 0x257F)
            DrawBox(sprite, BoxTable[codePoint - 0x2500], light);
        else if (codePoint <= 0x259F)
            DrawBlock(sprite, codePoint);
        else if (codePoint <= 0x28FF)
            DrawBraille(sprite, codePoint - 0x2800);
        else if (codePoint >= 0x1FB00)
            DrawSextant(sprite, SextantMask(codePoint - 0x1FB00));
        else if (codePoint >= 0x1CD00)
            DrawOctant(sprite, OctantTable[codePoint - 0x1CD00]);
        else
            DrawPowerline(sprite, codePoint, light);
        return sprite;
    }

    /// <summary>Bit mask (bit k-1 = sextant k, numbered 1 2 / 3 4 / 5 6) of U+1FB00 + <paramref name="index"/>.</summary>
    public static int SextantMask(int index)
    {
        // U+1FB00-1FB3B enumerate the 64 masks in order, skipping empty, full and the two half columns (21, 42),
        // which already exist as space, U+2588, U+258C and U+2590.
        int mask = index + 1;
        if (mask >= 21) mask++;
        if (mask >= 42) mask++;
        return mask;
    }

    /// <summary>Bit mask (bit k-1 = octant k) of U+1CD00 + <paramref name="index"/>.</summary>
    public static int OctantMask(int index) => OctantTable[index];

    /// <summary>round(k × extent / n), the shared integer division edge.</summary>
    public static int Edge(int extent, int k, int n) => (2 * k * extent + n) / (2 * n);

    // ---- box drawing -------------------------------------------------------------------------------------------

    private static int Thickness(int weight, int light) => weight switch
    {
        Heavy => 2 * light,
        Double => 3 * light,
        Light => light,
        _ => 0,
    };

    private static void DrawBox(SpriteBitmap s, ushort spec, int light)
    {
        int up = spec & 3, right = (spec >> 2) & 3, down = (spec >> 4) & 3, left = (spec >> 6) & 3;
        int dashes = (spec >> 8) & 7;
        bool arc = (spec & 0x800) != 0;
        int diagonal = (spec >> 12) & 3;
        int w = s.Width, h = s.Height;

        if (diagonal != 0)
        {
            if ((diagonal & 1) != 0) StrokeLine(s, w, 0, 0, h, light);
            if ((diagonal & 2) != 0) StrokeLine(s, 0, 0, w, h, light);
            return;
        }
        if (arc)
        {
            DrawArc(s, right != 0, down != 0, light);
            return;
        }
        if (dashes != 0)
        {
            if (left != 0)
                DrawDashes(s, horizontal: true, dashes, Thickness(left, light));
            else
                DrawDashes(s, horizontal: false, dashes, Thickness(up, light));
            return;
        }

        // The perpendicular block an arm runs into: the vertical strokes for horizontal arms and vice versa.
        // Without perpendicular arms the arms meet in their own centre block.
        int vertical = Math.Max(up, down), horizontal = Math.Max(left, right);
        int vt = Thickness(vertical != 0 ? vertical : horizontal, light);
        int ht = Thickness(horizontal != 0 ? horizontal : vertical, light);
        int vx0 = (w - vt) / 2, vx1 = vx0 + vt;
        int hy0 = (h - ht) / 2, hy1 = hy0 + ht;
        bool doubleCross = vertical == Double && horizontal == Double;

        if (right != 0)
            Arm(s, right, light, horizontal: true, start: vx0, end: w,
                doubleCross ? (up != 0 ? vx0 + 2 * light : vx0) : vx0,
                doubleCross ? (down != 0 ? vx0 + 2 * light : vx0) : vx0, isStart: true);
        if (left != 0)
            Arm(s, left, light, horizontal: true, start: 0, end: vx1,
                doubleCross ? (up != 0 ? vx0 + light : vx1) : vx1,
                doubleCross ? (down != 0 ? vx0 + light : vx1) : vx1, isStart: false);
        if (down != 0)
            Arm(s, down, light, horizontal: false, start: hy0, end: h,
                doubleCross ? (left != 0 ? hy0 + 2 * light : hy0) : hy0,
                doubleCross ? (right != 0 ? hy0 + 2 * light : hy0) : hy0, isStart: true);
        if (up != 0)
            Arm(s, up, light, horizontal: false, start: 0, end: hy1,
                doubleCross ? (left != 0 ? hy0 + light : hy1) : hy1,
                doubleCross ? (right != 0 ? hy0 + light : hy1) : hy1, isStart: false);
    }

    // An arm from `start` to `end` along its axis, centred across it. A double arm's two strokes (top/left
    // `first`, bottom/right `second`) get their own inner end, replacing `start` (isStart) or `end`.
    private static void Arm(SpriteBitmap s, int weight, int light, bool horizontal,
        int start, int end, int first, int second, bool isStart)
    {
        int across = horizontal ? s.Height : s.Width;
        if (weight == Double)
        {
            int offset = (across - 3 * light) / 2;
            FillSpan(s, isStart ? first : start, isStart ? end : first, offset, light, horizontal);
            FillSpan(s, isStart ? second : start, isStart ? end : second, offset + 2 * light, light, horizontal);
            return;
        }
        int t = Thickness(weight, light);
        FillSpan(s, start, end, (across - t) / 2, t, horizontal);
    }

    private static void FillSpan(SpriteBitmap s, int from, int to, int offset, int thickness, bool horizontal)
    {
        if (to <= from)
            return;
        if (horizontal)
            s.FillRect(from, offset, to - from, thickness);
        else
            s.FillRect(offset, from, thickness, to - from);
    }

    private static void DrawDashes(SpriteBitmap s, bool horizontal, int count, int thickness)
    {
        int extent = horizontal ? s.Width : s.Height;
        // Every dash needs a pixel and a gap pixel; a cell too narrow for that gets fewer dashes.
        count = Math.Min(count, Math.Max(1, extent / 2));
        int across = horizontal ? s.Height : s.Width;
        int offset = (across - thickness) / 2;
        for (int i = 0; i < count; i++)
        {
            int start = i * extent / count, end = (i + 1) * extent / count;
            int length = end - start;
            int gap = Math.Clamp((length + 2) / 4, length > 1 ? 1 : 0, Math.Max(0, length - 1));
            // Centre the dash in its segment so the gap is split across segment (and cell) boundaries.
            int from = start + gap / 2;
            FillSpan(s, from, from + length - gap, offset, thickness, horizontal);
        }
    }

    private static void DrawArc(SpriteBitmap s, bool right, bool down, int light)
    {
        int w = s.Width, h = s.Height;
        // Stroke centre lines, aligned with the straight light strokes of neighbouring cells.
        float cx = (w - light) / 2 + light * 0.5f;
        float cy = (h - light) / 2 + light * 0.5f;
        float dirX = right ? 1f : -1f, dirY = down ? 1f : -1f;
        float rx = right ? w - cx : cx, ry = down ? h - cy : cy;
        // One pixel short of the edge, so the edge column/row is a straight run identical to ─ and │.
        float r = MathF.Max(0f, MathF.Min(rx, ry) - 1f);
        float ax = cx + dirX * r, ay = cy + dirY * r;   // arc centre
        float half = light * 0.5f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                // Straight runs from the arc ends to the cell edges.
                float dh = DistanceToSegment(px, py, ax, cy, right ? w + 1f : -1f, cy);
                float dv = DistanceToSegment(px, py, cx, ay, cx, down ? h + 1f : -1f);
                float d = MathF.Min(dh, dv);
                float vx = px - ax, vy = py - ay;
                if (vx * dirX <= 0f && vy * dirY <= 0f)
                    d = MathF.Min(d, MathF.Abs(MathF.Sqrt(vx * vx + vy * vy) - r));
                s.Max(x, y, Coverage(half - d));
            }
        }
    }

    private static void StrokeLine(SpriteBitmap s, float x0, float y0, float x1, float y1, float thickness)
    {
        float half = thickness * 0.5f;
        float dx = x1 - x0, dy = y1 - y0;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        for (int y = 0; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                // Distance to the infinite line: diagonals continue straight into the diagonal neighbours.
                float d = MathF.Abs((x + 0.5f - x0) * dy - (y + 0.5f - y0) * dx) / length;
                s.Max(x, y, Coverage(half - d));
            }
        }
    }

    private static void StrokeSegment(SpriteBitmap s, float x0, float y0, float x1, float y1, float thickness)
    {
        float half = thickness * 0.5f;
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
                s.Max(x, y, Coverage(half - DistanceToSegment(x + 0.5f, y + 0.5f, x0, y0, x1, y1)));
    }

    private static float DistanceToSegment(float px, float py, float x0, float y0, float x1, float y1)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float lengthSquared = dx * dx + dy * dy;
        float t = lengthSquared <= 0f ? 0f : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / lengthSquared, 0f, 1f);
        float ex = px - (x0 + t * dx), ey = py - (y0 + t * dy);
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    // Coverage of a pixel whose centre lies `signedDistance` inside a stroke edge (box filter of width 1).
    private static byte Coverage(float signedDistance) =>
        (byte)MathF.Round(Math.Clamp(signedDistance + 0.5f, 0f, 1f) * 255f);

    // ---- blocks ------------------------------------------------------------------------------------------------

    private static void DrawBlock(SpriteBitmap s, int codePoint)
    {
        int w = s.Width, h = s.Height;
        int midX = Edge(w, 1, 2), midY = Edge(h, 1, 2);
        switch (codePoint)
        {
            case 0x2580: s.FillRect(0, 0, w, midY); return;
            case >= 0x2581 and <= 0x2588:
                {
                    int top = Edge(h, 8 - (codePoint - 0x2580), 8);
                    s.FillRect(0, top, w, h - top);
                    return;
                }
            case >= 0x2589 and <= 0x258F:
                s.FillRect(0, 0, Edge(w, 8 - (codePoint - 0x2588), 8), h);
                return;
            case 0x2590: s.FillRect(midX, 0, w - midX, h); return;
            case 0x2591: s.FillRect(0, 0, w, h, 0x40); return;
            case 0x2592: s.FillRect(0, 0, w, h, 0x80); return;
            case 0x2593: s.FillRect(0, 0, w, h, 0xC0); return;
            case 0x2594: s.FillRect(0, 0, w, Edge(h, 1, 8)); return;
            case 0x2595:
                {
                    int left = Edge(w, 7, 8);
                    s.FillRect(left, 0, w - left, h);
                    return;
                }
        }
        // Quadrants U+2596-259F: bit 0 upper left, 1 upper right, 2 lower left, 3 lower right.
        int quadrants = (codePoint - 0x2596) switch
        {
            0 => 0b0100,
            1 => 0b1000,
            2 => 0b0001,
            3 => 0b1101,
            4 => 0b1001,
            5 => 0b0111,
            6 => 0b1011,
            7 => 0b0010,
            8 => 0b0110,
            _ => 0b1110,
        };
        if ((quadrants & 1) != 0) s.FillRect(0, 0, midX, midY);
        if ((quadrants & 2) != 0) s.FillRect(midX, 0, w - midX, midY);
        if ((quadrants & 4) != 0) s.FillRect(0, midY, midX, h - midY);
        if ((quadrants & 8) != 0) s.FillRect(midX, midY, w - midX, h - midY);
    }

    private static void DrawSextant(SpriteBitmap s, int mask) => DrawGrid(s, mask, rows: 3);

    private static void DrawOctant(SpriteBitmap s, int mask) => DrawGrid(s, mask, rows: 4);

    // Fills the cells of a 2-column grid; bit 2r is the left cell of row r, bit 2r+1 the right one.
    private static void DrawGrid(SpriteBitmap s, int mask, int rows)
    {
        int midX = Edge(s.Width, 1, 2);
        for (int row = 0; row < rows; row++)
        {
            int top = Edge(s.Height, row, rows), bottom = Edge(s.Height, row + 1, rows);
            if ((mask & (1 << (2 * row))) != 0) s.FillRect(0, top, midX, bottom - top);
            if ((mask & (1 << (2 * row + 1))) != 0) s.FillRect(midX, top, s.Width - midX, bottom - top);
        }
    }

    /// <summary>Braille dot rectangle (cell coordinates) of dot <paramref name="dot"/> (1-8) in a cell.</summary>
    public static PixelRect BrailleDot(int dot, int width, int height)
    {
        // Dots 1-3 and 7 form the left column top to bottom, 4-6 and 8 the right one.
        int column = dot is >= 4 and <= 6 or 8 ? 1 : 0;
        int row = dot switch { 1 or 4 => 0, 2 or 5 => 1, 3 or 6 => 2, _ => 3 };
        int left = Edge(width, column, 2), right = Edge(width, column + 1, 2);
        int top = Edge(height, row, 4), bottom = Edge(height, row + 1, 4);
        int size = Math.Max(1, Math.Min(right - left, bottom - top) / 2);
        return new PixelRect(left + (right - left - size) / 2, top + (bottom - top - size) / 2, size, size);
    }

    private static void DrawBraille(SpriteBitmap s, int pattern)
    {
        for (int dot = 1; dot <= 8; dot++)
        {
            if ((pattern & (1 << (dot - 1))) == 0)
                continue;
            var rect = BrailleDot(dot, s.Width, s.Height);
            s.FillRect(rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    // ---- Powerline ---------------------------------------------------------------------------------------------

    private static void DrawPowerline(SpriteBitmap s, int codePoint, int light)
    {
        float w = s.Width, h = s.Height, mid = h * 0.5f;
        switch (codePoint)
        {
            case 0xE0B0: FillTriangle(s, 0, 0, w, mid, 0, h); break;
            case 0xE0B1: StrokeSegment(s, 0, 0, w, mid, light); StrokeSegment(s, w, mid, 0, h, light); break;
            case 0xE0B2: FillTriangle(s, w, 0, 0, mid, w, h); break;
            case 0xE0B3: StrokeSegment(s, w, 0, 0, mid, light); StrokeSegment(s, 0, mid, w, h, light); break;
            case 0xE0B4: HalfEllipse(s, centreX: 0, w, mid, filled: true, light); break;
            case 0xE0B5: HalfEllipse(s, centreX: 0, w, mid, filled: false, light); break;
            case 0xE0B6: HalfEllipse(s, centreX: w, w, mid, filled: true, light); break;
            case 0xE0B7: HalfEllipse(s, centreX: w, w, mid, filled: false, light); break;
            case 0xE0B8: FillTriangle(s, 0, 0, w, h, 0, h); break;      // lower left
            case 0xE0B9: StrokeLine(s, 0, 0, w, h, light); break;
            case 0xE0BA: FillTriangle(s, w, 0, w, h, 0, h); break;      // lower right
            case 0xE0BB: StrokeLine(s, w, 0, 0, h, light); break;
            case 0xE0BC: FillTriangle(s, 0, 0, w, 0, 0, h); break;      // upper left
            case 0xE0BD: StrokeLine(s, w, 0, 0, h, light); break;
            case 0xE0BE: FillTriangle(s, 0, 0, w, 0, w, h); break;      // upper right
            case 0xE0BF: StrokeLine(s, 0, 0, w, h, light); break;
        }
    }

    private const int Subsamples = 4;

    // 4 × 4 supersampled triangle coverage.
    private static void FillTriangle(SpriteBitmap s, float x0, float y0, float x1, float y1, float x2, float y2)
    {
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        for (int y = 0; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < Subsamples; sy++)
                {
                    for (int sx = 0; sx < Subsamples; sx++)
                    {
                        float px = x + (sx + 0.5f) / Subsamples, py = y + (sy + 0.5f) / Subsamples;
                        float a = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) * area;
                        float b = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) * area;
                        float c = ((x0 - px) * (y1 - py) - (x1 - px) * (y0 - py)) * area;
                        if (a >= 0f && b >= 0f && c >= 0f)
                            inside++;
                    }
                }
                s.Max(x, y, (byte)(inside * 255 / (Subsamples * Subsamples)));
            }
        }
    }

    // Half ellipse with radii (rx, ry) around (centreX, ry): filled, or an outline of the light thickness.
    private static void HalfEllipse(SpriteBitmap s, float centreX, float rx, float ry, bool filled, int light)
    {
        float half = light * 0.5f;
        for (int y = 0; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                if (filled)
                {
                    int inside = 0;
                    for (int sy = 0; sy < Subsamples; sy++)
                    {
                        for (int sx = 0; sx < Subsamples; sx++)
                        {
                            float nx = (x + (sx + 0.5f) / Subsamples - centreX) / rx;
                            float ny = (y + (sy + 0.5f) / Subsamples - ry) / ry;
                            if (nx * nx + ny * ny <= 1f)
                                inside++;
                        }
                    }
                    s.Max(x, y, (byte)(inside * 255 / (Subsamples * Subsamples)));
                }
                else
                {
                    float dx = x + 0.5f - centreX, dy = y + 0.5f - ry;
                    float nx = dx / rx, ny = dy / ry;
                    float r = MathF.Sqrt(nx * nx + ny * ny);
                    if (r <= 0f)
                        continue;
                    float gx = nx / (rx * r), gy = ny / (ry * r);
                    float d = MathF.Abs(r - 1f) / MathF.Max(1e-6f, MathF.Sqrt(gx * gx + gy * gy));
                    s.Max(x, y, Coverage(half - d));
                }
            }
        }
    }
}
