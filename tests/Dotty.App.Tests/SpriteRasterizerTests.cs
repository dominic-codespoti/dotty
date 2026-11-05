using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Rendering.Gpu;
using Xunit;

namespace Dotty.App.Tests;

public sealed class SpriteRasterizerTests
{
    // Cell sizes 7..40 px wide at line heights 1.0-1.5 (DejaVu-like 1.164 em font box, 0.602 em advance).
    public static IEnumerable<object[]> Cells()
    {
        foreach (double lineHeight in new[] { 1.0, 1.25, 1.5 })
            for (int width = 7; width <= 40; width++)
                yield return new object[] { width, (int)Math.Round(width / 0.602 * Math.Max(1.164, lineHeight)) };
    }

    private static SpriteBitmap Draw(int codePoint, int width, int height) =>
        SpriteRasterizer.Rasterize(codePoint, width, height) ?? throw new InvalidOperationException($"U+{codePoint:X4}");

    private static byte[] Column(SpriteBitmap s, int x) => Enumerable.Range(0, s.Height).Select(y => s[x, y]).ToArray();

    private static byte[] Row(SpriteBitmap s, int y) => Enumerable.Range(0, s.Width).Select(x => s[x, y]).ToArray();

    // Reference strokes per arm (up, right, down, left): light, heavy, double.
    private static readonly (int Light, int Heavy, int Double)[] Arms =
    {
        (0x2502, 0x2503, 0x2551),   // up: │ ┃ ║ at the top row
        (0x2500, 0x2501, 0x2550),   // right: ─ ━ ═ at the right column
        (0x2502, 0x2503, 0x2551),   // down
        (0x2500, 0x2501, 0x2550),   // left
    };

    [Theory]
    [InlineData(7, 1)]
    [InlineData(11, 1)]
    [InlineData(12, 2)]
    [InlineData(19, 2)]
    [InlineData(20, 3)]
    [InlineData(40, 5)]
    public void LightThickness_IsAnEighthOfTheCellRounded(int width, int expected) =>
        Assert.Equal(expected, SpriteRasterizer.LightThickness(width));

    // Classifies a sprite's edge on one side: 0 empty, else the weight (1 light, 2 heavy, 3 double) of the straight
    // reference stroke whose edge it equals exactly; -1 when it matches none, i.e. a neighbour would not meet it.
    private static int EdgeWeight(SpriteBitmap sprite, int arm, Func<int, SpriteBitmap> reference)
    {
        byte[] Edge(SpriteBitmap s) => arm switch
        {
            0 => Row(s, 0),
            1 => Column(s, s.Width - 1),
            2 => Row(s, s.Height - 1),
            _ => Column(s, 0),
        };
        byte[] edge = Edge(sprite);
        if (edge.All(v => v == 0))
            return 0;
        for (int weight = 1; weight <= 3; weight++)
            if (Edge(reference(RefCode(arm, weight))).SequenceEqual(edge))
                return weight;
        return -1;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void BoxDrawing_EveryArmMeetsItsNeighbourExactly(int width, int height)
    {
        var references = new Dictionary<int, SpriteBitmap>();
        SpriteBitmap Reference(int cp) => references.TryGetValue(cp, out var s) ? s : references[cp] = Draw(cp, width, height);
        string[] names = { "up", "right", "down", "left" };
        for (int cp = 0x2500; cp <= 0x257F; cp++)
        {
            if (cp is >= 0x2504 and <= 0x250B or >= 0x254C and <= 0x254F or >= 0x2571 and <= 0x2573)
                continue;   // dashed lines and diagonals: covered separately
            var sprite = Draw(cp, width, height);
            for (int arm = 0; arm < 4; arm++)
                Assert.True(EdgeWeight(sprite, arm, Reference) >= 0, $"U+{cp:X4} {names[arm]} edge at {width}x{height}");
        }
    }

    [Theory]
    // code point, up, right, down, left (from the Unicode names)
    [InlineData(0x250D, 0, 2, 1, 0)]   // DOWN LIGHT AND RIGHT HEAVY
    [InlineData(0x2529, 2, 0, 1, 2)]   // DOWN LIGHT AND LEFT UP HEAVY
    [InlineData(0x2547, 2, 2, 1, 2)]   // DOWN LIGHT AND UP HORIZONTAL HEAVY
    [InlineData(0x2552, 0, 3, 1, 0)]   // DOWN SINGLE AND RIGHT DOUBLE
    [InlineData(0x2553, 0, 1, 3, 0)]   // DOWN DOUBLE AND RIGHT SINGLE
    [InlineData(0x256C, 3, 3, 3, 3)]   // DOUBLE VERTICAL AND HORIZONTAL
    [InlineData(0x256D, 0, 1, 1, 0)]   // LIGHT ARC DOWN AND RIGHT
    [InlineData(0x256F, 1, 0, 0, 1)]   // LIGHT ARC UP AND LEFT
    [InlineData(0x2574, 0, 0, 0, 1)]   // LIGHT LEFT
    [InlineData(0x257C, 0, 2, 0, 1)]   // LIGHT LEFT AND HEAVY RIGHT
    [InlineData(0x257F, 2, 0, 1, 0)]   // HEAVY UP AND LIGHT DOWN
    public void BoxDrawing_ArmWeightsFollowTheNames(int cp, int up, int right, int down, int left)
    {
        foreach (var (width, height) in new[] { (7, 14), (16, 37), (40, 99) })
        {
            var references = new Dictionary<int, SpriteBitmap>();
            SpriteBitmap Reference(int c) => references.TryGetValue(c, out var s) ? s : references[c] = Draw(c, width, height);
            var sprite = Draw(cp, width, height);
            Assert.Equal(new[] { up, right, down, left }, Enumerable.Range(0, 4).Select(arm => EdgeWeight(sprite, arm, Reference)));
        }
    }

    private static int RefCode(int arm, int weight) => weight switch
    {
        1 => Arms[arm].Light,
        2 => Arms[arm].Heavy,
        _ => Arms[arm].Double,
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public void Strokes_HaveTheLightAndHeavyThickness(int width, int height)
    {
        int light = SpriteRasterizer.LightThickness(width);
        Assert.Equal(light, Column(Draw(0x2500, width, height), 0).Count(v => v == 255));
        Assert.Equal(2 * light, Column(Draw(0x2501, width, height), 0).Count(v => v == 255));
        Assert.Equal(2 * light, Column(Draw(0x2550, width, height), 0).Count(v => v == 255));
        Assert.Equal(light, Row(Draw(0x2502, width, height), 0).Count(v => v == 255));
        Assert.Equal(2 * light, Row(Draw(0x2503, width, height), 0).Count(v => v == 255));
        // Only full or empty pixels on straight strokes.
        Assert.All(Draw(0x253C, width, height).Pixels, v => Assert.True(v is 0 or 255));
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Dashes_RepeatAcrossCellsWithEqualGaps(int width, int height)
    {
        foreach (var (cp, count, horizontal) in new[] { (0x2504, 3, true), (0x2508, 4, true), (0x254C, 2, true), (0x2506, 3, false), (0x254E, 2, false) })
        {
            var sprite = Draw(cp, width, height);
            byte[] line = horizontal ? Row(sprite, (height - SpriteRasterizer.LightThickness(width)) / 2)
                : Column(sprite, (width - SpriteRasterizer.LightThickness(width)) / 2);
            int runs = 0;
            for (int i = 0; i < line.Length; i++)
                if (line[i] == 255 && (i == 0 || line[i - 1] == 0))
                    runs++;
            Assert.Equal(Math.Min(count, (horizontal ? width : height) / 2), runs);
            // A gap at the cell boundary: tiling the cell never merges two dashes.
            Assert.True(line[0] == 0 || line[^1] == 0, $"U+{cp:X4} {width}x{height}");
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Diagonals_ReachTheirCorners(int width, int height)
    {
        var slash = Draw(0x2571, width, height);
        Assert.True(slash[width - 1, 0] > 0 && slash[0, height - 1] > 0);
        Assert.True(slash[0, 0] == 0 && slash[width - 1, height - 1] == 0);
        var backslash = Draw(0x2572, width, height);
        Assert.True(backslash[0, 0] > 0 && backslash[width - 1, height - 1] > 0);
        var cross = Draw(0x2573, width, height);
        Assert.True(cross[0, 0] > 0 && cross[width - 1, 0] > 0);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Blocks_TileWithoutGapsOrOverlap(int width, int height)
    {
        AssertPartition(width, height, 0x2580, 0x2584);              // upper + lower half
        AssertPartition(width, height, 0x258C, 0x2590);              // left + right half
        AssertPartition(width, height, 0x2598, 0x259D, 0x2596, 0x2597);   // four quadrants
        AssertPartition(width, height, 0x2594, 0x2587);              // upper 1/8 + lower 7/8
        AssertPartition(width, height, 0x2589, 0x2595);              // left 7/8 + right 1/8
        Assert.All(Draw(0x2588, width, height).Pixels, v => Assert.Equal(255, v));
        for (int k = 1; k <= 7; k++)
        {
            var lower = Draw(0x2580 + k, width, height);
            Assert.Equal(height - SpriteRasterizer.Edge(height, 8 - k, 8), Column(lower, 0).Count(v => v == 255));
        }
        foreach (var (cp, alpha) in new[] { (0x2591, 0x40), (0x2592, 0x80), (0x2593, 0xC0) })
            Assert.All(Draw(cp, width, height).Pixels, v => Assert.Equal(alpha, v));
    }

    private static void AssertPartition(int width, int height, params int[] codes)
    {
        var sum = new int[width * height];
        foreach (int cp in codes)
        {
            var sprite = Draw(cp, width, height);
            for (int i = 0; i < sum.Length; i++)
                sum[i] += sprite.Pixels[i];
        }
        Assert.All(sum, v => Assert.Equal(255, v));
    }

    [Theory]
    [InlineData(7, 14)]
    [InlineData(10, 21)]
    [InlineData(23, 50)]
    [InlineData(40, 99)]
    public void Braille_EveryPatternInksExactlyItsDots(int width, int height)
    {
        var rects = Enumerable.Range(1, 8).Select(d => SpriteRasterizer.BrailleDot(d, width, height)).ToArray();
        for (int i = 0; i < rects.Length; i++)
        {
            Assert.True(rects[i].X >= 0 && rects[i].Right <= width && rects[i].Y >= 0 && rects[i].Bottom <= height);
            for (int j = i + 1; j < rects.Length; j++)
                Assert.False(Overlap(rects[i], rects[j]), $"dots {i + 1} and {j + 1}");
        }
        for (int pattern = 0; pattern < 256; pattern++)
        {
            var sprite = Draw(0x2800 + pattern, width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool expected = Enumerable.Range(0, 8).Any(bit => (pattern & (1 << bit)) != 0 && Contains(rects[bit], x, y));
                    Assert.Equal(expected ? 255 : 0, sprite[x, y]);
                }
            }
        }
    }

    [Fact]
    public void Braille_DotLayoutFollowsTheUnicodeNumbering()
    {
        // 1 4 / 2 5 / 3 6 / 7 8, top to bottom.
        var dots = Enumerable.Range(1, 8).Select(d => SpriteRasterizer.BrailleDot(d, 8, 16)).ToArray();
        Assert.True(dots[0].Y < dots[1].Y && dots[1].Y < dots[2].Y && dots[2].Y < dots[6].Y);
        Assert.True(dots[0].X < dots[3].X);
        Assert.Equal(new[] { dots[0].Y, dots[1].Y, dots[2].Y, dots[6].Y }, new[] { dots[3].Y, dots[4].Y, dots[5].Y, dots[7].Y });
    }

    [Theory]
    [InlineData(7, 14)]
    [InlineData(16, 37)]
    [InlineData(40, 99)]
    public void SextantsAndOctants_FillExactlyTheirMaskCells(int width, int height)
    {
        for (int i = 0; i < 60; i++)
            AssertGrid(Draw(0x1FB00 + i, width, height), SpriteRasterizer.SextantMask(i), 3);
        for (int i = 0; i <= 0xE5; i++)
            AssertGrid(Draw(0x1CD00 + i, width, height), SpriteRasterizer.OctantMask(i), 4);
    }

    private static void AssertGrid(SpriteBitmap sprite, int mask, int rows)
    {
        int mid = SpriteRasterizer.Edge(sprite.Width, 1, 2);
        for (int y = 0; y < sprite.Height; y++)
        {
            int row = Enumerable.Range(0, rows).Last(r => y >= SpriteRasterizer.Edge(sprite.Height, r, rows));
            for (int x = 0; x < sprite.Width; x++)
            {
                int bit = 2 * row + (x >= mid ? 1 : 0);
                Assert.Equal((mask & (1 << bit)) != 0 ? 255 : 0, sprite[x, y]);
            }
        }
    }

    [Fact]
    public void SextantAndOctantMasks_AreDistinctAndSkipExistingBlocks()
    {
        var sextants = Enumerable.Range(0, 60).Select(SpriteRasterizer.SextantMask).ToArray();
        Assert.Equal(60, sextants.Distinct().Count());
        Assert.DoesNotContain(0, sextants);
        Assert.DoesNotContain(0b010101, sextants);   // left half, U+258C
        Assert.DoesNotContain(0b101010, sextants);   // right half, U+2590
        Assert.DoesNotContain(0b111111, sextants);
        Assert.Equal(0b000001, sextants[0]);         // BLOCK SEXTANT-1
        Assert.Equal(0b111110, sextants[59]);        // BLOCK SEXTANT-23456

        var octants = Enumerable.Range(0, 0xE6).Select(SpriteRasterizer.OctantMask).ToArray();
        Assert.Equal(230, octants.Distinct().Count());
        Assert.Equal(0b00000100, octants[0]);        // BLOCK OCTANT-3
        Assert.Equal(0b11111110, octants[0xE5]);     // BLOCK OCTANT-2345678
        Assert.DoesNotContain(0b11111111, octants);
        Assert.DoesNotContain(0b00001111, octants);  // upper half
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Powerline_TrianglesCoverTheFullCellHeight(int width, int height)
    {
        var right = Draw(0xE0B0, width, height);
        var left = Draw(0xE0B2, width, height);
        for (int y = 0; y < height; y++)
        {
            Assert.True(right[0, y] > 0, $"E0B0 row {y}");
            Assert.True(left[width - 1, y] > 0, $"E0B2 row {y}");
        }
        for (int y = 1; y < height - 1; y++)
        {
            Assert.True(right[0, y] >= 128, $"E0B0 row {y} = {right[0, y]}");
            Assert.True(left[width - 1, y] >= 128, $"E0B2 row {y} = {left[width - 1, y]}");
        }
        // Mirror images.
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Assert.InRange(right[x, y] - left[width - 1 - x, y], -16, 16);   // one 4 × 4 sample on an edge tie
    }

    [Fact]
    public void Golden_LightHorizontalAndCross()
    {
        var line = Draw(0x2500, 8, 16);
        for (int y = 0; y < 16; y++)
            Assert.Equal(y == 7 ? Enumerable.Repeat((byte)255, 8) : new byte[8], Row(line, y));

        var cross = Draw(0x253C, 8, 16);
        string[] expected = Enumerable.Range(0, 16).Select(y => y == 7 ? "########" : "...#....").ToArray();
        Assert.Equal(expected, Ascii(cross));
    }

    [Fact]
    public void Golden_DoubleDownAndRight()
    {
        // ╔ at 8 × 12, light 1: strokes at x 2 and 4, y 4 and 6; the outer corner closes, the inner one is open.
        Assert.Equal(new[]
        {
            "........",
            "........",
            "........",
            "........",
            "..######",
            "..#.....",
            "..#.####",
            "..#.#...",
            "..#.#...",
            "..#.#...",
            "..#.#...",
            "..#.#...",
        }, Ascii(Draw(0x2554, 8, 12)));
    }

    [Fact]
    public void Golden_QuadrantAndBrailleDot()
    {
        Assert.Equal(new[] { "........", "........", "####....", "####...." }, Ascii(Draw(0x2596, 8, 4)));
        // Braille dot 1 in an 8 × 16 cell: a 2 × 2 square centred in the top-left 4 × 4 box.
        var dot = Draw(0x2801, 8, 16);
        Assert.Equal(new[] { "........", ".##.....", ".##.....", "........" }, Ascii(dot).Take(4));
        Assert.All(Ascii(dot).Skip(4), row => Assert.Equal("........", row));
    }

    [Theory]
    [InlineData(0x24FF, false)]
    [InlineData(0x2500, true)]
    [InlineData(0x259F, true)]
    [InlineData(0x25A0, false)]
    [InlineData(0x27FF, false)]
    [InlineData(0x2800, true)]
    [InlineData(0x28FF, true)]
    [InlineData(0x2900, false)]
    [InlineData(0x1FAFF, false)]
    [InlineData(0x1FB00, true)]
    [InlineData(0x1FB3B, true)]
    [InlineData(0x1FB3C, false)]
    [InlineData(0x1CCFF, false)]
    [InlineData(0x1CD00, true)]
    [InlineData(0x1CDE5, true)]
    [InlineData(0x1CDE6, false)]
    [InlineData(0xE0AF, false)]
    [InlineData(0xE0B0, true)]
    [InlineData(0xE0BF, true)]
    [InlineData(0xE0C0, false)]
    public void IsSupported_Boundaries(int codePoint, bool expected)
    {
        Assert.Equal(expected, SpriteRasterizer.IsSupported(codePoint));
        Assert.Equal(expected, SpriteRasterizer.Rasterize(codePoint, 8, 16) != null);
    }

    [Fact]
    public void EmptySize_ReturnsNull()
    {
        Assert.Null(SpriteRasterizer.Rasterize(0x2500, 0, 16));
        Assert.Null(SpriteRasterizer.Rasterize(0x2500, 8, 0));
    }

    [Fact]
    public void WideSpan_KeepsTheOneCellStrokeThickness()
    {
        var wide = SpriteRasterizer.Rasterize(0x2502, 32, 40, cellWidth: 16)!;
        Assert.Equal(SpriteRasterizer.LightThickness(16), Row(wide, 0).Count(v => v == 255));
    }

    private static bool Overlap(PixelRect a, PixelRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static bool Contains(PixelRect r, int x, int y) => x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;

    private static string[] Ascii(SpriteBitmap s) =>
        Enumerable.Range(0, s.Height)
            .Select(y => new string(Enumerable.Range(0, s.Width).Select(x => s[x, y] >= 128 ? '#' : '.').ToArray()))
            .ToArray();
}
