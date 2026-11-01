using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>W4-C11: run segmentation, cluster-to-cell mapping and the ligature row cache (fake shaper).</summary>
[Collection("Allocation-sensitive tests")]
public sealed class LigatureTests
{
    private static SegmentCell[] Cells(string text, int[]? faces = null) =>
        // ' ' is a blank and '#' a wide/non-candidate cell in these tables.
        text.Select((c, i) => c is ' ' or '#' ? SegmentCell.None : new SegmentCell(c, faces?[i] ?? 0)).ToArray();

    private static string Segment(string text, LigatureMode mode, int cursor = -1, int[]? faces = null)
    {
        var runs = new CellRun[16];
        int n = RunSegmenter.Segment(Cells(text, faces), mode, cursor, runs);
        return string.Join(" ", runs.Take(n).Select(r => $"{r.Start}+{r.Length}"));
    }

    [Theory]
    [InlineData("a->b", LigatureMode.Always, -1, "0+4")]
    [InlineData("a->b", LigatureMode.Never, -1, "")]
    [InlineData("abc", LigatureMode.Always, -1, "")]          // no punctuation pair
    [InlineData("a-b>c", LigatureMode.Always, -1, "")]        // punctuation not adjacent
    [InlineData("x => y", LigatureMode.Always, -1, "2+2")]    // blanks break runs
    [InlineData("=>#=>", LigatureMode.Always, -1, "0+2 3+2")] // wide/non-candidate cell breaks
    [InlineData("a->b", LigatureMode.Cursor, 2, "")]          // cursor splits "->"
    [InlineData("==>", LigatureMode.Cursor, 0, "1+2")]        // cursor cell excluded, rest still a run
    [InlineData("==>", LigatureMode.Always, 0, "0+3")]        // always ignores the cursor
    [InlineData("==>", LigatureMode.Cursor, 7, "0+3")]        // cursor elsewhere
    public void SegmenterTable(string text, LigatureMode mode, int cursor, string expected) =>
        Assert.Equal(expected, Segment(text, mode, cursor));

    [Fact]
    public void SegmenterBreaksAtStyleChange()
    {
        Assert.Equal("", Segment("->", LigatureMode.Always, faces: [0, 1]));
        Assert.Equal("0+2 2+2", Segment("->=>", LigatureMode.Always, faces: [0, 0, 1, 1]));
    }

    [Theory]
    [InlineData("never", LigatureMode.Never)]
    [InlineData("ALWAYS", LigatureMode.Always)]
    [InlineData("cursor", LigatureMode.Cursor)]
    [InlineData("bogus", LigatureMode.Cursor)]
    [InlineData(null, LigatureMode.Cursor)]
    public void ModeParsing(string? value, LigatureMode expected) => Assert.Equal(expected, LigatureModes.Parse(value));

    private static ShapedGlyph G(ushort id, int cluster) => new(id, cluster, 10, 0, 0);

    private static string Map(ShapedGlyph[] glyphs, string text)
    {
        var nominal = text.Select(c => (ushort)c).ToArray();
        var cells = new LigatureCell[text.Length];
        bool ok = ClusterCellMapper.Map(glyphs, nominal, cells);
        return (ok ? "" : "!") + string.Join(" ", cells.Select(c => c.Kind switch
        {
            LigatureCellKind.Start => $"S{c.Span}:{c.GlyphStart}+{c.GlyphCount}",
            LigatureCellKind.Continuation => "C",
            _ => "-",
        }));
    }

    public static IEnumerable<object[]> MapperCases() =>
    [
        // 2-cell ligature: one glyph for "->".
        [new[] { G('a', 0), G(900, 1), G('b', 3) }, "a->b", "- S2:1+1 C -"],
        // 3-cell ligature "===".
        [new[] { G(901, 0) }, "===", "S3:0+1 C C"],
        // Contextual alternates keep 1:1 cells: each cell draws its alternate, no continuation.
        [new[] { G(801, 0), G(802, 1) }, "!=", "S1:0+1 S1:1+1"],
        // Unchanged nominal glyphs stay on the ordinary path.
        [new[] { G('=', 0), G('>', 1) }, "=>", "- -"],
        // Partial cluster: two glyphs share cluster 0 spanning two cells, then a nominal cell.
        [new[] { G(700, 0), G(701, 0), G('x', 2) }, "<-x", "S2:0+2 C -"],
        // Spacer-style ligature (Fira-like): two 1:1 clusters with different glyphs.
        [new[] { G(1, 0), G(902, 1) }, "->", "S1:0+1 S1:1+1"],
        // Clusters going backwards (not LTR) make the run unusable.
        [new[] { G(1, 1), G(2, 0) }, "->", "!- -"],
        // Cluster outside the run.
        [new[] { G(1, 0), G(2, 5) }, "->", "!- -"],
    ];

    [Theory]
    [MemberData(nameof(MapperCases))]
    public void MapperTable(ShapedGlyph[] glyphs, string text, string expected) => Assert.Equal(expected, Map(glyphs, text));

    /// <summary>"->" becomes glyph 900 (2 cells), "===" glyph 901 (3 cells), anything else stays nominal.</summary>
    private sealed class FakeShaper : IRunShaper
    {
        public int Calls;
        public FontFeature[] LastFeatures = [];

        public int Shape(ReadOnlySpan<char> text, SKTypeface face, float textSize, ReadOnlySpan<FontFeature> features, Span<ShapedGlyph> output)
        {
            Calls++;
            LastFeatures = features.ToArray();
            bool ligaOff = features.ToArray().Any(f => f.Tag == "liga" && f.Value == 0);
            int n = 0;
            for (int i = 0; i < text.Length;)
            {
                if (!ligaOff && text[i..].StartsWith("==="))
                {
                    output[n++] = new ShapedGlyph(901, i, 30, 0, 0);
                    i += 3;
                }
                else if (!ligaOff && text[i..].StartsWith("->"))
                {
                    output[n++] = new ShapedGlyph(900, i, 20, 0, 0);
                    i += 2;
                }
                else
                {
                    output[n++] = new ShapedGlyph(text[i], i, 10, 0, 0);
                    i++;
                }
            }
            return n;
        }

        public ushort GetNominalGlyph(SKTypeface face, char c) => c;
    }

    private static TerminalBuffer Row(string text, int cursorCol = -1)
    {
        var buffer = new TerminalBuffer(rows: 2, columns: 20);
        buffer.SetCursor(0, 0);
        buffer.WriteText(text.AsSpan(), CellAttributes.Default);
        buffer.SetCursor(cursorCol < 0 ? 1 : 0, cursorCol < 0 ? 0 : cursorCol);
        return buffer;
    }

    private static string Kinds(LigatureRenderer.Row? row, int length) => row == null ? "null"
        : new string(Enumerable.Range(0, length).Select(i => row.KindAt(i) switch
        {
            LigatureCellKind.Start => 'S',
            LigatureCellKind.Continuation => 'C',
            _ => '-',
        }).ToArray());

    [Fact]
    public void RendererMapsRowCells()
    {
        var shaper = new FakeShaper();
        var renderer = new LigatureRenderer(shaper);
        renderer.Configure(LigatureMode.Always, null);
        var buffer = Row("a -> === b");
        var row = renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f);
        Assert.Equal("--SC-SCC---", Kinds(row, 11));
        Assert.Equal(900, row!.GlyphsAt(2)[0].GlyphId);
        var key = row.KeyAt(5);
        Assert.Equal(row.KeyAt(5), key);
        Assert.NotEqual(row.KeyAt(2), key);
        Assert.StartsWith("\u0001lig3:901", key.Grapheme);
    }

    [Fact]
    public void CursorModeLeavesTheCursorCellUnshaped()
    {
        var renderer = new LigatureRenderer(new FakeShaper());
        renderer.Configure(LigatureMode.Cursor, null);
        Assert.Null(renderer.PrepareRow(Row("a->b", cursorCol: 2), 0, 20, SKTypeface.Default, 14f));
        Assert.Equal("-SC-", Kinds(renderer.PrepareRow(Row("a->b", cursorCol: 0), 0, 20, SKTypeface.Default, 14f), 4));
    }

    [Fact]
    public void RowCacheReshapesOnlyOnModeOrFeatureChange()
    {
        var shaper = new FakeShaper();
        var renderer = new LigatureRenderer(shaper);
        var buffer = Row("x->y");
        var features = new List<FontFeature>();
        renderer.Configure(LigatureMode.Always, features);
        Assert.NotNull(renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
        Assert.NotNull(renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
        Assert.Equal(1, shaper.Calls);

        int stamp = renderer.ConfigStamp;
        renderer.Configure(LigatureMode.Always, new List<FontFeature>()); // equal content: no change
        Assert.Equal(stamp, renderer.ConfigStamp);
        renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f);
        Assert.Equal(1, shaper.Calls);

        renderer.Configure(LigatureMode.Always, [new FontFeature("liga", 0)]);
        Assert.NotEqual(stamp, renderer.ConfigStamp);
        Assert.Null(renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
        Assert.Equal(2, shaper.Calls);
        Assert.Equal("liga", shaper.LastFeatures.Single().Tag);

        stamp = renderer.ConfigStamp;
        renderer.Configure(LigatureMode.Cursor, [new FontFeature("liga", 1)]);
        Assert.NotEqual(stamp, renderer.ConfigStamp);
        Assert.NotNull(renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
        Assert.Equal(3, shaper.Calls);

        renderer.Configure(LigatureMode.Never, null);
        Assert.Null(renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
        Assert.Equal(3, shaper.Calls);
    }

    [Fact]
    public void RowWithoutPunctuationPairsTakesTheFastPath()
    {
        var shaper = new FakeShaper();
        var renderer = new LigatureRenderer(shaper);
        renderer.Configure(LigatureMode.Always, null);
        var buffer = Row("plain text, a-b c");
        Assert.Null(renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
        Assert.Equal(0, shaper.Calls);
        AllocationAssert.NoAllocations(() => renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
    }

    [Fact]
    public void CachedShapedRowAllocatesNothing()
    {
        var renderer = new LigatureRenderer(new FakeShaper());
        renderer.Configure(LigatureMode.Always, null);
        var buffer = Row("if (a -> b) => c");
        Assert.NotNull(renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
        AllocationAssert.NoAllocations(() => renderer.PrepareRow(buffer, 0, 20, SKTypeface.Default, 14f));
    }

    [Fact]
    public void QuadBuilderDrawsLigatureFromFirstCellAndBackgroundOnlyOnContinuations()
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 6);
        buffer.SetCursor(0, 0);
        var red = CellAttributes.Default with { Background = SgrColorArgb.FromRgb(200, 0, 0) };
        buffer.WriteText("a===".AsSpan(), red);
        var renderer = new LigatureRenderer(new FakeShaper());
        renderer.Configure(LigatureMode.Always, null);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        var options = TextAttributeOptions.Default with { Ligatures = renderer };
        var instances = new CellInstance[QuadFrameBuilder.InstanceBudget(1, 6)];
        int n = QuadFrameBuilder.BuildRow(buffer, 0, atlas, SKTypeface.Default, 14f, instances, null, 6,
            QuadFrameBuilder.DefaultForeground, QuadFrameBuilder.DefaultBackground, in options, out _);
        Assert.Equal(4, n);
        Assert.Equal([0, 1, 2, 3], instances.Take(n).Select(i => (int)i.Col));
        Assert.Equal(0, instances[2].GlyphW); // continuation: background only
        Assert.Equal(0, instances[3].GlyphW);
        Assert.All(instances.Take(n), i => Assert.Equal(255, i.BgA));
    }
}
