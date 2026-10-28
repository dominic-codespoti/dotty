using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;
using static Dotty.Terminal.Tests.KittyCommands;
using static Dotty.Terminal.Tests.KittyTestData;

namespace Dotty.Terminal.Tests;

/// <summary>In-memory cell grid for <see cref="PlaceholderResolver"/>: no screen, buffer or parser.</summary>
internal sealed class FakePlaceholderSource : IPlaceholderSource
{
    private static readonly TerminalPalette Palette = new();
    private readonly CellHot[] _cells;
    private readonly ColdCell[] _cold;
    private readonly CellAttributes[] _styles = new CellAttributes[64];
    private int _styleCount = 1;

    public FakePlaceholderSource(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        _cells = new CellHot[rows * columns];
        _cold = new ColdCell[rows * columns];
        for (int i = 0; i < _cold.Length; i++)
            _cold[i].Reset();
    }

    public int Rows { get; }
    public int Columns { get; }

    /// <summary>When set, every cell access fails: proves the resolver did not look at the cells.</summary>
    public bool ThrowOnAccess { get; set; }

    /// <summary>The style id of an SGR parameter list (interned); "" is the default style 0.</summary>
    public ushort Style(string sgr)
    {
        var attributes = sgr.Length == 0 ? CellAttributes.Default : SgrParserArgb.Apply(sgr.AsSpan(), CellAttributes.Default, Palette);
        for (int i = 0; i < _styleCount; i++)
        {
            if (_styles[i].Equals(attributes))
                return (ushort)i;
        }
        _styles[_styleCount] = attributes;
        return (ushort)_styleCount++;
    }

    /// <summary>A placeholder cell; <c>null</c> marks are absent (index values are positions in the diacritic table).</summary>
    public void Placeholder(int row, int column, string sgr, int? r = null, int? c = null, int? msb = null)
    {
        int i = row * Columns + column;
        _cells[i].Rune = PlaceholderTable.PlaceholderRune;
        _cells[i].StyleId = Style(sgr);
        if (r != null || c != null || msb != null)
        {
            _cold[i].GraphemeIndex = PlaceholderTable.Intern(
                (ushort)(r ?? PlaceholderIndices.Absent), (ushort)(c ?? PlaceholderIndices.Absent), (ushort)(msb ?? PlaceholderIndices.Absent));
        }
    }

    /// <summary>A placeholder whose cold cell carries an arbitrary code (stale, absent, a grapheme-store index...).</summary>
    public void PlaceholderWithCode(int row, int column, string sgr, short code)
    {
        Placeholder(row, column, sgr);
        _cold[row * Columns + column].GraphemeIndex = code;
    }

    public void Text(int row, int column, char ch) => _cells[row * Columns + column].Rune = ch;

    /// <summary>A double-width character at <paramref name="column"/> and its continuation cell.</summary>
    public void Wide(int row, int column, int rune)
    {
        int i = row * Columns + column;
        _cells[i].Rune = (uint)rune;
        _cells[i].Width = 2;
        _cells[i + 1].IsContinuation = true;
    }

    public ReadOnlySpan<CellHot> GetRowCells(int row)
    {
        if (ThrowOnAccess) throw new InvalidOperationException("cells must not be read");
        return new ReadOnlySpan<CellHot>(_cells, row * Columns, Columns);
    }

    public ReadOnlySpan<ColdCell> GetRowColdCells(int row)
    {
        if (ThrowOnAccess) throw new InvalidOperationException("cells must not be read");
        return new ReadOnlySpan<ColdCell>(_cold, row * Columns, Columns);
    }

    public ref readonly CellAttributes GetStyle(ushort styleId) => ref _styles[styleId];
}

/// <summary>
/// <see cref="PlaceholderResolver"/> over synthetic cells: identity (image id from the foreground, high byte, placement
/// id from the underline), the three inheritance rules, runs, the fit of the image into the grid, grid bounds and
/// virtual-parent children. Cells are 8x16 px unless stated; an image the grid fits exactly has the aspect ratio of the grid.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyPlaceholderResolverTests
{
    private const float CellW = 8f, CellH = 16f;

    // diacritic table positions used below: U+0305 = 0, U+030D = 1, U+030E = 2
    private const int D0 = 0, D1 = 1, D2 = 2;

    private sealed class Scene : IDisposable
    {
        public KittyImageStore Store { get; } = new(100_000_000);

        public KittyImage Image(uint id, int width, int height, params string[] virtualKeys)
        {
            var image = Store.AddImage(width, height, id);
            foreach (string keys in virtualKeys)
                Store.Place(image, keys.Length == 0 ? "U=1" : "U=1," + keys);
            return image;
        }

        public KittyRenderSnapshot Snapshot()
        {
            var snapshot = new KittyRenderSnapshot();
            Store.CaptureVisible(snapshot, 24, 0);
            return snapshot;
        }

        public List<KittyRenderPlacement> Resolve(FakePlaceholderSource source, float cellW = CellW, float cellH = CellH)
        {
            using var snapshot = Snapshot();
            var output = new List<KittyRenderPlacement>();
            PlaceholderResolver.Resolve(source, snapshot, cellW, cellH, output);
            return output;
        }

        public void Dispose() => Store.Dispose();
    }

    private static void Expect(KittyRenderPlacement p, int row, int col, int cols, double u0, double v0, double u1, double v1,
        double offX = 0, double offY = 0, double widthCells = -1, double heightCells = 1)
    {
        if (widthCells < 0) widthCells = cols;
        Assert.Equal((row, col, cols), (p.ViewRow, p.Col, p.Cols));
        Assert.Equal(u0, p.U0, 5);
        Assert.Equal(v0, p.V0, 5);
        Assert.Equal(u1, p.U1, 5);
        Assert.Equal(v1, p.V1, 5);
        Assert.Equal(offX, p.OffXCells, 5);
        Assert.Equal(offY, p.OffYCells, 5);
        Assert.True(p.NativeSize);
        Assert.Equal(PlaceholderResolver.Basis, p.BasisCellW);
        Assert.Equal(PlaceholderResolver.Basis, p.BasisCellH);
        Assert.Equal((int)Math.Round(widthCells * PlaceholderResolver.Basis), p.NativeW);
        Assert.Equal((int)Math.Round(heightCells * PlaceholderResolver.Basis), p.NativeH);
        Assert.Equal(0, p.Z);
    }

    // ------------------------------------------------------------------------------- spec examples verbatim

    [Fact]
    public void SpecExampleTwoByTwoForImage42()
    {
        // printf "\e[38;5;42m\U10EEEE\U0305\U0305\U10EEEE\U0305\U030D\e[39m\n"
        // printf "\e[38;5;42m\U10EEEE\U030D\U0305\U10EEEE\U030D\U030D\e[39m\n"
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(8, 20);
        source.Placeholder(3, 5, "38;5;42", D0, D0);
        source.Placeholder(3, 6, "38;5;42", D0, D1);
        source.Placeholder(4, 5, "38;5;42", D1, D0);
        source.Placeholder(4, 6, "38;5;42", D1, D1);

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        Expect(quads[0], 3, 5, 2, 0, 0, 1, 0.5);
        Expect(quads[1], 4, 5, 2, 0, 0.5, 1, 1);
        Assert.All(quads, q => Assert.Equal(42u, q.ImageId));
    }

    [Fact]
    public void SpecExampleHighByteForImage33554474()
    {
        // image id 33554474 = 42 + (2 << 24); the third diacritic U+030E is the number 2
        using var scene = new Scene();
        scene.Image(33554474, 16, 32, "c=2,r=2");
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(4, 8);
        source.Placeholder(0, 0, "38;5;42", D0, D0, D2);
        source.Placeholder(0, 1, "38;5;42", D0, D1, D2);
        source.Placeholder(1, 0, "38;5;42", D1, D0, D2);
        source.Placeholder(1, 1, "38;5;42", D1, D1, D2);

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        Assert.All(quads, q => Assert.Equal(33554474u, q.ImageId));
        Expect(quads[0], 0, 0, 2, 0, 0, 1, 0.5);
        Expect(quads[1], 1, 0, 2, 0, 0.5, 1, 1);
    }

    [Fact]
    public void SpecExampleTwoRowsByThreeColumnsWithInheritance()
    {
        // printf "\e[38;5;42m\U10EEEE\U0305\U10EEEE\U10EEEE\n"
        // printf "\e[38;5;42m\U10EEEE\U030D\U10EEEE\U10EEEE\n"
        using var scene = new Scene();
        scene.Image(42, 24, 32, "c=3,r=2");
        var source = new FakePlaceholderSource(4, 10);
        foreach (var (row, mark) in new[] { (0, D0), (1, D1) })
        {
            source.Placeholder(row, 0, "38;5;42", mark);
            source.Placeholder(row, 1, "38;5;42");
            source.Placeholder(row, 2, "38;5;42");
        }

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        Expect(quads[0], 0, 0, 3, 0, 0, 1, 0.5);
        Expect(quads[1], 1, 0, 3, 0, 0.5, 1, 1);
    }

    // ------------------------------------------------------------------------------------- inheritance rules

    [Fact]
    public void RuleOneNoDiacriticsInheritsRowColumnPlusOneAndHighByte()
    {
        using var scene = new Scene();
        scene.Image(33554474, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(0, 0, "38;5;42", D0, D0, D2);
        source.Placeholder(0, 1, "38;5;42");

        var quads = scene.Resolve(source);

        var run = Assert.Single(quads);
        Assert.Equal(33554474u, run.ImageId);
        Expect(run, 0, 0, 2, 0, 0, 1, 0.5);
    }

    [Fact]
    public void RuleTwoOnlyTheRowInheritsColumnPlusOneAndHighByteWhenTheRowsAreEqual()
    {
        using var scene = new Scene();
        scene.Image(33554474, 16, 32, "c=2,r=2");
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(0, 0, "38;5;42", D1, D0, D2);       // (1,0), high byte 2
        source.Placeholder(0, 1, "38;5;42", D1);               // same row: column 1, high byte 2

        var run = Assert.Single(scene.Resolve(source));

        Assert.Equal(33554474u, run.ImageId);
        Expect(run, 0, 0, 2, 0, 0.5, 1, 1);
    }

    [Fact]
    public void RuleTwoDoesNotApplyWhenTheRowsDiffer()
    {
        using var scene = new Scene();
        scene.Image(33554474, 16, 32, "c=2,r=2");
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(0, 0, "38;5;42", D0, D1, D2);       // (0,1), high byte 2
        source.Placeholder(0, 1, "38;5;42", D1);               // another row: nothing is inherited, so (1,0) of image 42

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        Assert.Equal(33554474u, quads[0].ImageId);
        Assert.Equal(42u, quads[1].ImageId);
        Expect(quads[1], 0, 1, 1, 0, 0.5, 0.5, 1);
    }

    [Fact]
    public void RuleThreeRowAndColumnInheritTheHighByteWhenTheColumnIsOnePast()
    {
        using var scene = new Scene();
        scene.Image(33554474, 16, 32, "c=2,r=2");
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(0, 0, "38;5;42", D0, D0, D2);
        source.Placeholder(0, 1, "38;5;42", D0, D1);           // column 0 + 1: inherits the high byte

        var run = Assert.Single(scene.Resolve(source));

        Assert.Equal(33554474u, run.ImageId);
        Expect(run, 0, 0, 2, 0, 0, 1, 0.5);
    }

    [Fact]
    public void RuleThreeDoesNotApplyWhenTheColumnIsNotOnePast()
    {
        using var scene = new Scene();
        scene.Image(33554474, 16, 32, "c=2,r=2");
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(0, 0, "38;5;42", D0, D0, D2);
        source.Placeholder(0, 1, "38;5;42", D0, D0);           // column 0 again: high byte 0, image 42

        var quads = scene.Resolve(source);

        Assert.Equal(new uint[] { 33554474, 42 }, quads.Select(q => q.ImageId));
    }

    [Fact]
    public void AFullTripleInheritsNothing()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        scene.Image(1, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(0, 0, "38;5;42", D0, D0, D0);
        source.Placeholder(0, 1, "38;5;42", D0, D1, D0);       // explicit everything
        source.Placeholder(0, 2, "38;5;42", D0, D0, D0);

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);                           // cols 0..1 are a run; col 2 repeats (0,0)
        Expect(quads[0], 0, 0, 2, 0, 0, 1, 0.5);
        Expect(quads[1], 0, 2, 1, 0, 0, 0.5, 0.5);
    }

    [Fact]
    public void ADifferentForegroundOrUnderlineBlocksInheritance()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=1", "c=2,r=2,p=2");
        scene.Image(43, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(3, 6);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;43");                    // other foreground: image 43, (0,0) by default
        source.Placeholder(1, 0, "38;5;42;58;5;1", D0, D0);
        source.Placeholder(1, 1, "38;5;42;58;5;2");             // other underline: image 42 placement 2, (0,0) by default
        source.Placeholder(2, 0, "38;5;42;58;5;1", D0, D0);
        source.Placeholder(2, 1, "38;5;42;58;5;1");             // same colours: inherits (0,1)

        var quads = scene.Resolve(source);

        Assert.Equal(5, quads.Count);
        Assert.Equal((42u, 1u), (quads[0].ImageId, quads[0].PlacementId));
        Expect(quads[0], 0, 0, 1, 0, 0, 0.5, 0.5);
        Assert.Equal((43u, 0u), (quads[1].ImageId, quads[1].PlacementId));
        Expect(quads[1], 0, 1, 1, 0, 0, 0.5, 0.5);
        Assert.Equal((42u, 1u), (quads[2].ImageId, quads[2].PlacementId));
        Assert.Equal((42u, 2u), (quads[3].ImageId, quads[3].PlacementId));
        Expect(quads[3], 1, 1, 1, 0, 0, 0.5, 0.5);
        Expect(quads[4], 2, 0, 2, 0, 0, 1, 0.5);
    }

    [Fact]
    public void ANonPlaceholderCellBreaksInheritance()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 8);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Text(0, 1, 'x');
        source.Placeholder(0, 2, "38;5;42");                    // nothing to inherit from: (0,0) again

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        Expect(quads[0], 0, 0, 1, 0, 0, 0.5, 0.5);
        Expect(quads[1], 0, 2, 1, 0, 0, 0.5, 0.5);
    }

    [Fact]
    public void ADoubleWidthNeighbourBreaksInheritanceAndIsSkippedSafely()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 8);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Wide(0, 1, 0x4E00);                              // cols 1-2
        source.Placeholder(0, 3, "38;5;42");                    // directly after the continuation cell

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        Expect(quads[1], 0, 3, 1, 0, 0, 0.5, 0.5);
    }

    [Fact]
    public void ACellWithNoUsableDiacriticCodeCountsAsHavingNone()
    {
        // an unissued code, the all-absent code and a grapheme-store index (what a full table degrades to)
        foreach (short code in new short[] { short.MinValue, PlaceholderTable.AbsentCode, 5, -1 })
        {
            using var scene = new Scene();
            scene.Image(42, 16, 32, "c=2,r=2");
            var source = new FakePlaceholderSource(2, 6);
            source.Placeholder(0, 0, "38;5;42", D0, D0);
            source.PlaceholderWithCode(0, 1, "38;5;42", code);

            var run = Assert.Single(scene.Resolve(source));
            Expect(run, 0, 0, 2, 0, 0, 1, 0.5);                 // inherited: (0,1)
        }
    }

    [Fact]
    public void WithoutAnythingToInheritAPlainPlaceholderIsCellZeroZero()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(1, 3, "38;5;42");

        var run = Assert.Single(scene.Resolve(source));

        Expect(run, 1, 3, 1, 0, 0, 0.5, 0.5);
    }

    // ------------------------------------------------------------------------------------------ identity

    [Fact]
    public void TheForegroundIsTheImageIdAsPaletteIndexOrTruecolorAndBothAgree()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        scene.Image(0x010203, 16, 32, "c=2,r=2");
        scene.Image(0x03010203, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(4, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(1, 0, "38;2;0;0;42", D0, D0);
        source.Placeholder(2, 0, "38;2;1;2;3", D0, D0);
        source.Placeholder(3, 0, "38;2;1;2;3", D0, D0, 3);      // high byte 3

        var quads = scene.Resolve(source);

        Assert.Equal(new uint[] { 42, 42, 0x010203, 0x03010203 }, quads.Select(q => q.ImageId));
    }

    [Fact]
    public void ImageIdZeroDrawsNothingButAHighByteMakesItNonZero()
    {
        using var scene = new Scene();
        scene.Image(1u << 24, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(4, 4);
        source.Placeholder(0, 0, "38;5;0", D0, D0);             // id 0
        source.Placeholder(1, 0, "38;2;0;0;0", D0, D0);         // id 0 in truecolor
        source.Placeholder(2, 0, "", D0, D0);                   // default foreground: no image at all
        source.Placeholder(3, 0, "38;5;0", D0, D0, D1);         // 0 | 1 << 24

        var quads = scene.Resolve(source);

        var only = Assert.Single(quads);
        Assert.Equal(1u << 24, only.ImageId);
        Assert.Equal(3, only.ViewRow);
    }

    [Fact]
    public void AHighByteAboveTwoFiftyFiveNamesNoImage()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0, 296);      // the last table entry is 296: not a byte

        Assert.Empty(scene.Resolve(source));
    }

    [Fact]
    public void AnUnknownImageDrawsNothingAndDoesNotBreakItsNeighbours()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 6);
        source.Placeholder(0, 0, "38;5;99", D0, D0);
        source.Placeholder(0, 1, "38;5;42", D0, D0);

        var run = Assert.Single(scene.Resolve(source));

        Expect(run, 0, 1, 1, 0, 0, 0.5, 0.5);
    }

    [Fact]
    public void TheUnderlineColourSelectsThePlacementAndZeroMeansTheLowestId()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=7", "c=2,r=2,p=3");
        var source = new FakePlaceholderSource(6, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0);                       // none: lowest (3)
        source.Placeholder(1, 0, "38;5;42;58;5;7", D0, D0);                // palette index 7
        source.Placeholder(2, 0, "38;5;42;58;2;0;0;7", D0, D0);            // truecolor 7
        source.Placeholder(3, 0, "38;5;42;58;5;3", D0, D0);
        source.Placeholder(4, 0, "38;5;42;58;5;9", D0, D0);                // no such placement: nothing
        source.Placeholder(5, 0, "38;5;42;58;5;0", D0, D0);                // index 0 is "any" too

        var quads = scene.Resolve(source);

        Assert.Equal(new[] { 3u, 7u, 7u, 3u, 3u }, quads.Select(q => q.PlacementId));
        Assert.Equal(new[] { 0, 1, 2, 3, 5 }, quads.Select(q => q.ViewRow));
    }

    [Fact]
    public void APlacementWithoutAnIdIsTheLowestOfAll()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=5", "c=2,r=2");       // the second has no placement id
        var source = new FakePlaceholderSource(1, 2);
        source.Placeholder(0, 0, "38;5;42", D0, D0);

        Assert.Equal(0u, Assert.Single(scene.Resolve(source)).PlacementId);
    }

    [Fact]
    public void MixedPaletteAndTruecolorCellsOnOneRow()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 8);
        // explicit columns: one run although the colours are specified differently
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;2;0;0;42", D0, D1);
        // a bare cell does not inherit across kinds (palette 42 is not RGB 42)
        source.Placeholder(1, 0, "38;5;42", D1, D0);
        source.Placeholder(1, 1, "38;2;0;0;42");

        var quads = scene.Resolve(source);

        Assert.Equal(3, quads.Count);
        Expect(quads[0], 0, 0, 2, 0, 0, 1, 0.5);
        Expect(quads[1], 1, 0, 1, 0, 0.5, 0.5, 1);
        Expect(quads[2], 1, 1, 1, 0, 0, 0.5, 0.5);              // (0,0) by default
    }

    // ------------------------------------------------------------------------------------- runs and bounds

    [Fact]
    public void RunsNeedAdjacentScreenCellsAndAdjacentGridCells()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(3, 10);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 2, "38;5;42", D0, D1);            // a screen gap
        source.Placeholder(1, 0, "38;5;42", D0, D0);
        source.Placeholder(1, 1, "38;5;42", D0, D0);            // same grid cell twice
        source.Placeholder(2, 0, "38;5;42", D1, D1);
        source.Placeholder(2, 1, "38;5;42", D1, D0);            // grid columns going backwards

        Assert.Equal(6, scene.Resolve(source).Count);
    }

    [Fact]
    public void CellsOutsideTheGridDrawNothing()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(4, 10);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;42", D0, D1);
        source.Placeholder(0, 2, "38;5;42", D0, 2);             // column 2 of a 2-column grid
        source.Placeholder(1, 0, "38;5;42", 2, D0);             // row 2 of a 2-row grid
        source.Placeholder(2, 0, "38;5;42", 200, 250);

        var run = Assert.Single(scene.Resolve(source));

        Expect(run, 0, 0, 2, 0, 0, 1, 0.5);
    }

    [Fact]
    public void ARunStopsAtTheEdgeOfTheGridAndRestartsOnTheNextOne()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 10);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        for (int c = 1; c < 4; c++)
            source.Placeholder(0, c, "38;5;42");                // inherits columns 1, 2, 3: the grid has two

        var run = Assert.Single(scene.Resolve(source));

        Expect(run, 0, 0, 2, 0, 0, 1, 0.5);
    }

    [Fact]
    public void OnlyTheVisiblePartOfAGridCanBeOnTheScreen()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 4);            // the screen shows just grid row 1
        source.Placeholder(0, 0, "38;5;42", D1, D0);
        source.Placeholder(0, 1, "38;5;42");

        var run = Assert.Single(scene.Resolve(source));

        Expect(run, 0, 0, 2, 0, 0.5, 1, 1);
    }

    [Fact]
    public void ASingleCellOfTheLowerRightGridCorner()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(8, 20);
        source.Placeholder(2, 7, "38;5;42", D1, D1);

        var run = Assert.Single(scene.Resolve(source));

        Expect(run, 2, 7, 1, 0.5, 0.5, 1, 1);
    }

    // --------------------------------------------------------------------------------------------- the fit

    [Fact]
    public void AWideImageIsLetterboxedInTheGrid()
    {
        // 32x16 px into 2x2 cells of 8x16 (a 16x32 box): scaled to 16x8, centred vertically at y 12..20
        using var scene = new Scene();
        scene.Image(42, 32, 16, "c=2,r=2");
        var source = new FakePlaceholderSource(4, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;42");
        source.Placeholder(1, 0, "38;5;42", D1, D0);
        source.Placeholder(1, 1, "38;5;42");

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        Expect(quads[0], 0, 0, 2, 0, 0, 1, 0.5, offY: 0.75, heightCells: 0.25);
        Expect(quads[1], 1, 0, 2, 0, 0.5, 1, 1, offY: 0, heightCells: 0.25);
    }

    [Fact]
    public void ATallImageIsPillarboxedAndTheRunCoversTheFittedWidthOnly()
    {
        // 8x32 px into 2x2 cells of 8x16: scaled 1:1, centred horizontally at x 4..12
        using var scene = new Scene();
        scene.Image(42, 8, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(4, 8);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;42");
        source.Placeholder(2, 0, "38;5;42", D0, D0);            // column 0 alone
        source.Placeholder(3, 0, "38;5;42", D0, D1);            // column 1 alone

        var quads = scene.Resolve(source);

        Assert.Equal(3, quads.Count);
        Expect(quads[0], 0, 0, 2, 0, 0, 1, 0.5, offX: 0.5, widthCells: 1);
        Expect(quads[1], 2, 0, 1, 0, 0, 0.5, 0.5, offX: 0.5, widthCells: 0.5);
        Expect(quads[2], 3, 0, 1, 0.5, 0, 1, 0.5, offX: 0, widthCells: 0.5);
    }

    [Fact]
    public void TheFitUsesThePixelAspectOfTheCells()
    {
        // 16x32 px into 2x2 cells of 10x10 (a 20x20 box): scale 0.625 gives 10x20 at x 5..15
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;42");

        var run = Assert.Single(scene.Resolve(source, 10f, 10f));

        Expect(run, 0, 0, 2, 0, 0, 1, 0.5, offX: 0.5, widthCells: 1);
    }

    [Fact]
    public void OnlyTheCellRatioMattersNotItsAbsoluteSize()
    {
        using var scene = new Scene();
        scene.Image(42, 32, 16, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;42");

        var small = Assert.Single(scene.Resolve(source, 8f, 16f));
        var large = Assert.Single(scene.Resolve(source, 17f, 34f));

        Expect(large, 0, 0, 2, small.U0, small.V0, small.U1, small.V1, small.OffXCells, small.OffYCells, heightCells: 0.25);
        Assert.Equal(small.NativeW, large.NativeW);
        Assert.Equal(small.NativeH, large.NativeH);
    }

    [Fact]
    public void ACropOfTheImageIsTheSourceOfTheFit()
    {
        // x=0,y=0,w=16,h=16 of a 16x32 image: a square source fitted into a 2x2 box of 8x16 cells (16x32)
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,x=0,y=0,w=16,h=16");
        var source = new FakePlaceholderSource(2, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;42");

        var run = Assert.Single(scene.Resolve(source));

        // scale 1: 16x16 centred vertically at y 8..24; row 0 shows y 8..16, the upper half of the (square) crop,
        // which is the top quarter of the image
        Expect(run, 0, 0, 2, 0, 0, 1, 0.25, offY: 0.5, heightCells: 0.5);
    }

    [Fact]
    public void AGridWithoutRequestedSizeUsesTheNativeCellBox()
    {
        // 16x32 px at 8x16 cells: native size, 2x2 cells
        using var scene = new Scene();
        scene.Image(42, 16, 32, "");
        var source = new FakePlaceholderSource(4, 4);
        source.Placeholder(1, 1, "38;5;42", D1, D1);
        source.Placeholder(3, 3, "38;5;42", 2, 0);               // outside the 2x2 grid

        var run = Assert.Single(scene.Resolve(source));

        Expect(run, 1, 1, 1, 0.5, 0.5, 1, 1);
    }

    [Fact]
    public void QuadsCarryTheImageAndAreAlwaysAboveTheText()
    {
        using var scene = new Scene();
        var image = scene.Image(42, 16, 32, "c=2,r=2,z=-5");
        var source = new FakePlaceholderSource(2, 2);
        source.Placeholder(0, 0, "38;5;42", D0, D0);

        var run = Assert.Single(scene.Resolve(source));

        Assert.Equal(0, run.Z);
        Assert.Equal(image.Root.Serial, run.ImageSerial);
        Assert.Same(image.Root, run.Pixels);
    }

    // ------------------------------------------------------------------------------------ plumbing

    [Fact]
    public void NoVirtualPlacementMeansTheCellsAreNeverRead()
    {
        using var scene = new Scene();
        var image = scene.Image(42, 16, 32);                      // no virtual placement ...
        scene.Store.Place(image, "c=1,r=1");                      // ... only a normal one
        var source = new FakePlaceholderSource(4, 4) { ThrowOnAccess = true };
        using var snapshot = scene.Snapshot();
        var output = new List<KittyRenderPlacement>();

        Assert.Equal(0, PlaceholderResolver.Resolve(source, snapshot, CellW, CellH, output));
        Assert.Empty(output);
    }

    [Theory]
    [InlineData(0f, 16f)]
    [InlineData(8f, 0f)]
    [InlineData(-8f, 16f)]
    [InlineData(float.NaN, 16f)]
    [InlineData(8f, float.PositiveInfinity)]
    public void ADegenerateCellSizeResolvesNothing(float cellW, float cellH)
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 2);
        source.Placeholder(0, 0, "38;5;42", D0, D0);

        Assert.Empty(scene.Resolve(source, cellW, cellH));
    }

    [Fact]
    public void ResultsAreAppendedToWhatIsAlreadyInTheList()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 2);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        using var snapshot = scene.Snapshot();
        var output = new List<KittyRenderPlacement> { default };

        int added = PlaceholderResolver.Resolve(source, snapshot, CellW, CellH, output);

        Assert.Equal(1, added);
        Assert.Equal(2, output.Count);
        Assert.Equal(42u, output[1].ImageId);
    }

    [Fact]
    public void ResolvingTwiceGivesTheSameQuads()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2");
        var source = new FakePlaceholderSource(2, 4);
        source.Placeholder(0, 0, "38;5;42", D0, D0);
        source.Placeholder(0, 1, "38;5;42");

        Assert.Equal(scene.Resolve(source), scene.Resolve(source));
    }

    [Fact]
    public void ManyVirtualPlacementsAreFoundByIdAndPlacement()
    {
        using var scene = new Scene();
        for (uint id = 1; id <= 40; id++)
        {
            scene.Image(id * 3, 16, 32, "c=2,r=2,p=2", "c=2,r=2,p=1");
        }
        var source = new FakePlaceholderSource(3, 4);
        source.Placeholder(0, 0, "38;5;30;58;5;2", D0, D0);     // image 30, placement 2
        source.Placeholder(1, 0, "38;5;120", D0, D0);           // image 120 (= 40 * 3), lowest placement
        source.Placeholder(2, 0, "38;5;31", D0, D0);            // no such image (not a multiple of 3)

        var quads = scene.Resolve(source);

        Assert.Equal(new[] { (30u, 2u), (120u, 1u) }, quads.Select(q => (q.ImageId, q.PlacementId)));
    }

    // ------------------------------------------------------------------ relative placements on a virtual parent

    [Fact]
    public void AChildOfAVirtualPlacementSitsAtTheTopLeftPlaceholderCellPlusItsOffsets()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=1");
        var child = scene.Store.AddImage(40, 20, 7);
        Assert.Equal(KittyStatus.Ok, scene.Store.TryPlaceRelative(child, Parse("P=42,Q=1,H=1,V=2"), 8, 16, out _));
        var source = new FakePlaceholderSource(10, 12);
        source.Placeholder(4, 6, "38;5;42;58;5;1", D1, D1);      // (min row, min col) are taken independently
        source.Placeholder(3, 7, "38;5;42;58;5;1", D0, D1);
        source.Placeholder(4, 5, "38;5;42;58;5;1", D1, D0);

        var quads = scene.Resolve(source);

        Assert.Equal(3, quads.Count);                             // two placeholder runs and the child
        var last = quads[^1];
        Assert.Equal(7u, last.ImageId);
        Assert.Equal((3 + 2, 5 + 1), (last.ViewRow, last.Col));
    }

    [Fact]
    public void AChildOfAVirtualPlacementWithoutPlaceholdersIsNotDrawn()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=1");
        var child = scene.Store.AddImage(40, 20, 7);
        scene.Store.TryPlaceRelative(child, Parse("P=42,Q=1"), 8, 16, out _);
        var source = new FakePlaceholderSource(4, 4);
        source.Placeholder(0, 0, "38;5;99", D0, D0);              // another image

        Assert.Empty(scene.Resolve(source));
    }

    [Fact]
    public void AChildOfAVirtualPlacementOffTheScreenIsDropped()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=1");
        var child = scene.Store.AddImage(40, 20, 7);
        scene.Store.TryPlaceRelative(child, Parse("P=42,Q=1,V=-9"), 8, 16, out _);   // 2 rows tall, ends at row -7
        var source = new FakePlaceholderSource(4, 4);
        source.Placeholder(1, 0, "38;5;42;58;5;1", D0, D0);

        var quads = scene.Resolve(source);

        Assert.Equal(42u, Assert.Single(quads).ImageId);
    }

    [Fact]
    public void AChildStaysAtTheGridOriginWhileTheTopRowsOfTheGridScrollAway()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=1");
        var child = scene.Store.AddImage(40, 20, 7);
        Assert.Equal(KittyStatus.Ok, scene.Store.TryPlaceRelative(child, Parse("P=42,Q=1,H=1,V=2"), 8, 16, out _));
        var source = new FakePlaceholderSource(10, 12);
        // only grid row 1 is on the screen, at screen row 0: the grid's cell (0, 0) is at (-1, 3)
        source.Placeholder(0, 3, "38;5;42;58;5;1", D1, D0);
        source.Placeholder(0, 4, "38;5;42;58;5;1");

        var quads = scene.Resolve(source);

        Assert.Equal(2, quads.Count);
        var last = quads[^1];
        Assert.Equal(7u, last.ImageId);
        Assert.Equal((-1 + 2, 3 + 1), (last.ViewRow, last.Col));
    }

    [Fact]
    public void CellsOutsideTheGridDoNotMoveTheChildsAnchor()
    {
        using var scene = new Scene();
        scene.Image(42, 16, 32, "c=2,r=2,p=1");
        var child = scene.Store.AddImage(40, 20, 7);
        Assert.Equal(KittyStatus.Ok, scene.Store.TryPlaceRelative(child, Parse("P=42,Q=1"), 8, 16, out _));
        var source = new FakePlaceholderSource(10, 12);
        source.Placeholder(4, 6, "38;5;42;58;5;1", D0, D0);
        source.Placeholder(1, 1, "38;5;42;58;5;1", D0, 5);        // column 5 of a 2-column grid: ignored

        var last = scene.Resolve(source)[^1];

        Assert.Equal(7u, last.ImageId);
        Assert.Equal((4, 6), (last.ViewRow, last.Col));
    }

    [Fact]
    public void CollectImageIdsFollowsTheSameIdentityAndInheritanceAsTheResolver()
    {
        var source = new FakePlaceholderSource(6, 8);
        source.Placeholder(0, 0, "38;5;42", D0, D0, D2);
        source.Placeholder(0, 1, "38;5;42");                        // inherits the high byte: still 42 | 2 << 24
        source.Placeholder(1, 0, "38;2;1;2;3");                     // 0x010203
        source.Placeholder(2, 0, "");                               // default foreground: none
        source.Placeholder(3, 0, "38;5;0", D0, D0);                 // id 0: none
        source.Placeholder(4, 0, "38;5;9", D0, D0, 296);            // high byte above 255: none
        source.Text(5, 0, 'x');

        var ids = new HashSet<uint>();
        PlaceholderResolver.CollectImageIds(source, ids);

        Assert.Equal(2, ids.Count);
        Assert.True(ids.SetEquals(new uint[] { 33554474, 0x010203 }));
    }

    // ---------------------------------------------------------------- animation visibility (AdvanceAnimations)

    // A running two-frame image (1x1, default 40 ms gap): the first pass that treats it as displayed arms its
    // deadline at now + 40 ms (clock of 1000 ticks per second), a pass that does not returns 0.
    private static KittyImage Animated(KittyRig rig, uint id)
    {
        rig.Store.TicksPerSecond = 1000;
        AnimKit.Transmit(rig, id, 1, 1, new byte[] { 10, 0, 0, 255 });
        AnimKit.LoadFrame(rig, id, 1, 1, new byte[] { 20, 0, 0, 255 });
        rig.Apc($"a=a,i={id},s=3");
        return AnimKit.Img(rig, id);
    }

    [Fact]
    public void AnAnimatedImageWithAVirtualPlacementAdvancesWhenTheCellsAreNotKnown()
    {
        using var rig = new KittyRig();
        rig.Store.Place(Animated(rig, 5), "U=1,c=2,r=2,p=1");

        Assert.Equal(1040L, rig.Store.AdvanceAnimations(1000, 24, 0));
    }

    [Fact]
    public void AVirtualPlacementAdvancesOnlyWhileAPlaceholderNamesItsImage()
    {
        foreach (var (sgr, expected) in new[] { ("38;5;5", 1040L), ("38;5;6", 0L), ("", 0L) })
        {
            using var rig = new KittyRig();
            rig.Store.Place(Animated(rig, 5), "U=1,c=2,r=2,p=1");
            var source = new FakePlaceholderSource(8, 20);
            source.Placeholder(2, 3, sgr, D0, D0);

            Assert.Equal(expected, rig.Store.AdvanceAnimations(1000, 24, 0, source));
        }

        using var empty = new KittyRig();
        empty.Store.Place(Animated(empty, 5), "U=1,c=2,r=2,p=1");
        Assert.Equal(0L, empty.Store.AdvanceAnimations(1000, 24, 0, new FakePlaceholderSource(8, 20)));
    }

    [Fact]
    public void ARelativeChildOfAVirtualPlacementAdvancesWhileTheParentsPlaceholdersAreShown()
    {
        foreach (var (sgr, expected) in new[] { ("38;5;5", 1040L), ("38;5;6", 0L) })
        {
            using var rig = new KittyRig();
            rig.Store.Place(rig.Store.AddImage(16, 32, 5), "U=1,c=2,r=2,p=1");
            var child = Animated(rig, 8);
            Assert.Equal(KittyStatus.Ok, rig.Store.TryPlaceRelative(child, Parse("P=5,Q=1,H=1"), 8, 16, out _));
            var source = new FakePlaceholderSource(8, 20);
            source.Placeholder(2, 3, sgr, D0, D0);

            Assert.Equal(expected, rig.Store.AdvanceAnimations(1000, 24, 0, source));
        }
    }

    [Theory]
    [InlineData(30L, -20, 1040L)]    // the parent is below the viewport, the child (row 10) is in it
    [InlineData(5L, 3, 1040L)]
    [InlineData(5L, 40, 0L)]         // the parent is in view, the child (row 45) is not
    [InlineData(5L, -10, 0L)]        // the child is above the viewport (row -5)
    public void ARelativeChildAdvancesOnItsOwnResolvedPosition(long parentRow, int v, long expectedDeadline)
    {
        using var rig = new KittyRig();
        rig.Store.Place(rig.Store.AddImage(8, 16, 1), "p=1", anchorRow: parentRow);
        var child = Animated(rig, 2);
        Assert.Equal(KittyStatus.Ok, rig.Store.TryPlaceRelative(child, Parse($"P=1,Q=1,V={v}"), 8, 16, out _));

        Assert.Equal(expectedDeadline, rig.Store.AdvanceAnimations(1000, 24, 0));
    }
}

/// <summary>
/// Placeholder text through a real adapter and parser (UTF-8 bytes with SGR colours and diacritics), captured into a
/// <see cref="RenderSnapshot"/> and resolved: the cell encoding, colour kinds, scrolling, screens and deletes seen
/// end to end. Cells are 10x20 px.
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class KittyPlaceholderEndToEndTests
{
    private const string Esc = "\u001b";
    private static readonly string P = char.ConvertFromUtf32(0x10EEEE);
    private const string D0 = "\u0305", D1 = "\u030D", D2 = "\u030E";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; }
        public BasicAnsiParser Parser { get; }

        public Rig(int rows = 8, int columns = 20)
        {
            Adapter = new TerminalAdapter(rows, columns, 100);
            Adapter.SetCellPixelSize(10, 20);
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public TerminalBuffer Buffer => Adapter.Buffer;

        public void Feed(string text) => Parser.Feed(Encoding.UTF8.GetBytes(text));

        public void Apc(string control, string payload = "") => Parser.Feed(KittyTestData.ApcBytes(control, payload));

        public void At(int row, int col, string text) => Feed(Esc + "[" + (row + 1) + ";" + (col + 1) + "H" + text);

        /// <summary>Transmits a <paramref name="w"/>x<paramref name="h"/> image quietly and makes a virtual placement of the given grid.</summary>
        public void VirtualImage(uint id, int w, int h, int cols, int rows, string extra = "")
        {
            Apc($"a=t,i={id},s={w},v={h},f=24,q=2", Convert.ToBase64String(new byte[w * h * 3]));
            Apc($"a=p,i={id},U=1,c={cols},r={rows},q=2" + extra);
        }

        public List<KittyRenderPlacement> Resolve(int scrollOffset = 0)
        {
            using var snapshot = Buffer.CaptureRenderSnapshotVisible(0, -1, scrollOffset);
            var output = new List<KittyRenderPlacement>();
            if (snapshot.Graphics is { } graphics)
                PlaceholderResolver.Resolve(snapshot, graphics, 10, 20, output);
            return output;
        }

        public void Dispose() => Adapter.Dispose();
    }

    private static void Quad(KittyRenderPlacement q, uint image, int row, int col, int cols, double v0, double v1)
    {
        Assert.Equal(image, q.ImageId);
        Assert.Equal((row, col, cols), (q.ViewRow, q.Col, q.Cols));
        Assert.Equal(0.0, (double)q.U0, 4);
        Assert.Equal(1.0, (double)q.U1, 4);
        Assert.Equal(v0, (double)q.V0, 4);
        Assert.Equal(v1, (double)q.V1, 4);
    }

    [Fact]
    public void TheSpecsTwoByTwoExampleResolvesFromRealTerminalOutput()
    {
        using var t = new Rig();
        t.VirtualImage(42, 16, 32, 2, 2);

        t.At(2, 4, Esc + "[38;5;42m" + P + D0 + D0 + P + D0 + D1 + Esc + "[39m");
        t.At(3, 4, Esc + "[38;5;42m" + P + D1 + D0 + P + D1 + D1 + Esc + "[39m");

        var quads = t.Resolve();
        Assert.Equal(2, quads.Count);
        Quad(quads[0], 42, 2, 4, 2, 0, 0.5);
        Quad(quads[1], 42, 3, 4, 2, 0.5, 1);
    }

    [Fact]
    public void TheSpecsHighByteExampleResolvesFromRealTerminalOutput()
    {
        using var t = new Rig();
        t.VirtualImage(33554474, 16, 32, 2, 2);

        t.At(0, 0, Esc + "[38;5;42m" + P + D0 + D0 + D2 + P + D0 + D1 + D2);
        t.At(1, 0, Esc + "[38;5;42m" + P + D1 + D0 + D2 + P + D1 + D1 + D2);

        var quads = t.Resolve();
        Assert.Equal(2, quads.Count);
        Quad(quads[0], 33554474, 0, 0, 2, 0, 0.5);
        Quad(quads[1], 33554474, 1, 0, 2, 0.5, 1);
    }

    [Fact]
    public void TheSpecsInheritanceExampleResolvesFromRealTerminalOutput()
    {
        using var t = new Rig();
        t.VirtualImage(42, 24, 32, 3, 2);

        t.At(5, 2, Esc + "[38;5;42m" + P + D0 + P + P);
        t.At(6, 2, Esc + "[38;5;42m" + P + D1 + P + P);

        var quads = t.Resolve();
        Assert.Equal(2, quads.Count);
        Quad(quads[0], 42, 5, 2, 3, 0, 0.5);
        Quad(quads[1], 42, 6, 2, 3, 0.5, 1);
    }

    [Fact]
    public void TruecolorForegroundAndUnderlineColoursAndColonFormsReachTheResolver()
    {
        using var t = new Rig();
        t.VirtualImage(0x010203, 16, 32, 2, 2, ",p=7");

        t.At(0, 0, Esc + "[38;2;1;2;3;58;2;0;0;7m" + P + D0 + D0 + P);          // rgb fg and rgb underline
        t.At(1, 0, Esc + "[38:2::1:2:3m" + Esc + "[58:5:7m" + P + D1 + D0 + P);   // colon forms, palette underline
        t.At(2, 0, Esc + "[59m" + Esc + "[38;2;1;2;3m" + P + D1 + D0 + P);        // no underline: any placement

        var quads = t.Resolve();
        Assert.Equal(3, quads.Count);
        Assert.All(quads, q => Assert.Equal(0x010203u, q.ImageId));
        Assert.Equal(new[] { 7u, 7u, 7u }, quads.Select(q => q.PlacementId));
        Assert.Equal(new[] { 0, 1, 2 }, quads.Select(q => q.ViewRow));
    }

    [Fact]
    public void ScrollingMovesThePlaceholdersAndTheScrollbackViewShowsThemLater()
    {
        using var t = new Rig(rows: 6);
        t.VirtualImage(42, 16, 32, 2, 2);
        t.At(4, 0, Esc + "[38;5;42m" + P + D0 + D0 + P + D0 + D1);
        t.At(5, 0, Esc + "[38;5;42m" + P + D1 + D0 + P + D1 + D1);
        Assert.Equal(new[] { 4, 5 }, t.Resolve().Select(q => q.ViewRow));

        t.At(5, 0, "\n\n\n\n\n");                              // five line feeds at the bottom: the text moves up five rows

        // Live view: grid row 0 is in the history, only grid row 1 (now on row 0) is left on the screen.
        var live = Assert.Single(t.Resolve());
        Quad(live, 42, 0, 0, 2, 0.5, 1);
        // Scrolled back by k rows the text is k rows lower again.
        Assert.Equal(new[] { 0, 1 }, t.Resolve(scrollOffset: 1).Select(q => q.ViewRow));
        Assert.Equal(new[] { 1, 2 }, t.Resolve(scrollOffset: 2).Select(q => q.ViewRow));
        var back = t.Resolve(scrollOffset: 5);
        Assert.Equal(new[] { 4, 5 }, back.Select(q => q.ViewRow));
        Quad(back[0], 42, 4, 0, 2, 0, 0.5);
        Quad(back[1], 42, 5, 0, 2, 0.5, 1);
    }

    [Fact]
    public void VirtualPlacementsBelongToTheirScreen()
    {
        using var t = new Rig();
        t.VirtualImage(42, 16, 32, 2, 2);
        t.At(0, 0, Esc + "[38;5;42m" + P + D0 + D0);
        Assert.Single(t.Resolve());

        t.Feed(Esc + "[?1049h");                                  // alternate screen: its own, empty, image store
        t.At(0, 0, Esc + "[38;5;42m" + P + D0 + D0);
        Assert.Empty(t.Resolve());

        t.Feed(Esc + "[?1049l");
        Assert.Single(t.Resolve());
    }

    [Fact]
    public void DeletingTheVirtualPlacementStopsTheImageButNotTheText()
    {
        using var t = new Rig();
        t.VirtualImage(42, 16, 32, 2, 2);
        t.At(0, 0, Esc + "[38;5;42m" + P + D0 + D0);
        Assert.Single(t.Resolve());

        t.Apc("a=d,d=a");                                         // position-based deletes never touch virtual placements
        Assert.Single(t.Resolve());

        t.Apc("a=d,d=i,i=42");
        Assert.Empty(t.Resolve());
        using var snapshot = t.Buffer.CaptureRenderSnapshotVisible(0, -1, 0);
        Assert.Equal(PlaceholderTable.PlaceholderRune, (int)snapshot.GetRowCells(0)[0].Rune);
    }

    [Fact]
    public void ARelativePlacementOnAPlaceholderFollowsItsText()
    {
        using var t = new Rig(rows: 10);
        t.VirtualImage(42, 16, 32, 2, 2, ",p=1");
        t.Apc("a=T,i=7,s=10,v=20,f=24,P=42,Q=1,H=3,V=1,q=2,C=1", Convert.ToBase64String(new byte[10 * 20 * 3]));
        t.At(2, 4, Esc + "[38;5;42;58;5;1m" + P + D0 + D0 + P + D0 + D1);
        t.At(3, 4, Esc + "[38;5;42;58;5;1m" + P + D1 + D0 + P + D1 + D1);

        var quads = t.Resolve();
        var child = quads.Single(q => q.ImageId == 7);
        Assert.Equal((3, 7), (child.ViewRow, child.Col));         // top-left placeholder (2,4) + (V=1, H=3)

        t.Feed(Esc + "[10;1H\n");                                 // one scroll: everything moves up a row
        child = t.Resolve().Single(q => q.ImageId == 7);
        Assert.Equal((2, 7), (child.ViewRow, child.Col));
    }
}

/// <summary>The per-frame path with virtual placements (capture and resolve) allocates nothing once its buffers have grown.</summary>
[Collection("Allocation-sensitive tests")]
public sealed class KittyPlaceholderAllocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureAndResolveAllocateNothingInSteadyState(bool withChild)
    {
        using var store = new KittyImageStore(100_000_000);
        store.Place(store.AddImage(16, 32, 42), "U=1,c=2,r=2,p=1");
        store.Place(store.AddImage(16, 32, 43), "U=1,c=2,r=2,p=1");
        if (withChild)
        {
            var child = store.AddImage(40, 20, 7);
            Assert.Equal(KittyStatus.Ok, store.TryPlaceRelative(child, Parse("P=42,Q=1,H=1"), 8, 16, out _));
        }
        var source = new FakePlaceholderSource(6, 10);
        source.Placeholder(1, 2, "38;5;42;58;5;1", 0, 0);
        source.Placeholder(1, 3, "38;5;42;58;5;1");
        source.Placeholder(2, 2, "38;5;42;58;5;1", 1, 0);
        source.Placeholder(2, 3, "38;5;42;58;5;1");
        source.Placeholder(4, 0, "38;5;43", 0, 0);
        using var snapshot = new KittyRenderSnapshot();
        var output = new List<KittyRenderPlacement>();

        AllocationAssert.NoAllocations(() =>
        {
            snapshot.Reset();
            store.CaptureVisible(snapshot, 24, 0);
            output.Clear();
            PlaceholderResolver.Resolve(source, snapshot, 8f, 16f, output);
        });

        Assert.Equal(withChild ? 4 : 3, output.Count);
    }
}
