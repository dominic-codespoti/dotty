using System;
using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Graphics;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Unicode placeholders through the real pipeline: placeholder text and a virtual placement in a pane's buffer, the
/// composer's locked capture, the resolver on the snapshot copy, and the image quads it appends; plus the guarantee that
/// a <c>U+10EEEE</c> cell never draws a glyph. Cells are 10x20 px; every image the grid fits exactly has the grid's aspect.
/// </summary>
public sealed class TerminalSceneComposerPlaceholderTests
{
    private static readonly string P = char.ConvertFromUtf32(0x10EEEE);
    private const string D0 = "\u0305", D1 = "\u030D";

    private sealed class Scene : IDisposable
    {
        private readonly TerminalTabManager _manager = new(SilentPty.Create);
        private readonly GlyphAtlas _atlas = new(SKTypeface.Default, 14f, initialSize: 256);

        public Scene(int rows, int columns)
        {
            Tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: rows, columns: columns);
            Composer = new TerminalSceneComposer(_atlas, SKTypeface.Default, 14f);
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, null);
        }

        public TerminalTab Tab { get; }
        public TerminalSceneComposer Composer { get; }
        public TerminalAdapter Adapter => Tab.ActivePane.Session.Adapter;
        public KittyImageStore Store => Adapter.Buffer.ActiveImageStore;

        /// <summary>Adds a <paramref name="width"/>x<paramref name="height"/> image and a virtual placement of the given keys.</summary>
        public KittyImage VirtualImage(uint id, int width, int height, string keys)
        {
            Assert.True(Store.TryAdd(ImagePixels.Allocate(width, height, ImagePixelFormat.Rgba8Premultiplied), id, 0, false, 0, out var image));
            Assert.Equal(KittyParseResult.Ok, KittyControlParser.TryParse(Encoding.ASCII.GetBytes("U=1," + keys), out var cmd));
            Assert.Equal(KittyStatus.Ok, Store.TryPlace(image!, in cmd, 0, 0, 10, 20, out _));
            return image!;
        }

        /// <summary>Prints <paramref name="text"/> at the 0-based cell with the given SGR parameters.</summary>
        public void Print(int row, int column, string sgr, string text)
        {
            Adapter.Buffer.SetCursor(row, column);
            Adapter.OnSetGraphicsRendition(sgr);
            Adapter.OnPrint(text);
        }

        public TerminalSceneFrame Compose(int framebufferWidth, int framebufferHeight, float cellWidth, float cellHeight, float scale,
            int rows, int columns)
        {
            var theme = BuiltInThemes.DarkPlus;
            return Composer.Compose(
                Tab,
                _manager,
                theme,
                new SgrColorArgb(theme.Foreground),
                selectionColor: new SgrColorArgb(0x803385DB),
                framebufferWidth: framebufferWidth,
                framebufferHeight: framebufferHeight,
                cellWidth: cellWidth,
                cellHeight: cellHeight,
                scale: scale,
                rows: rows,
                columns: columns,
                padding: new PaddingUserConfig { Left = 0, Top = 0, Right = 0, Bottom = 0 },
                showTabBar: false,
                cursorVisible: false,
                scrollbarHovered: false,
                scrollbarDragging: false,
                searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
                activeContextMenu: null);
        }

        public void Dispose()
        {
            Composer.Dispose();
            Tab.Dispose();
            _manager.Dispose();
            _atlas.Dispose();
        }
    }

    private static void PrintTwoByTwo(Scene scene, int row, int column, string sgr = "38;5;42")
    {
        scene.Print(row, column, sgr, P + D0 + D0 + P + D0 + D1);
        scene.Print(row + 1, column, sgr, P + D1 + D0 + P + D1 + D1);
    }

    [Fact]
    public void APlaceholderGridBecomesOneQuadPerGridRowAtItsCells()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        scene.VirtualImage(42, 16, 32, "c=2,r=2");
        PrintTwoByTwo(scene, row: 1, column: 2);

        var frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        ImageDrawList draws = frame.ImageDraws;
        Assert.Equal(2, draws.QuadCount);
        Assert.Equal(ImageLayer.AboveText, draws.Draws[0].Layer);
        // Cell (row 1, col 2) is x 20, y 20 at 10x20 px; a two-cell run is 20x20 and shows the top half of the image.
        ImageQuadInstance top = draws.Quads[0], bottom = draws.Quads[1];
        Assert.Equal((20f, 20f, 20f, 20f), (top.X, top.Y, top.W, top.H));
        Assert.Equal((0f, 0f, 1f, 0.5f), (top.U0, top.V0, top.U1, top.V1));
        Assert.Equal((20f, 40f, 20f, 20f), (bottom.X, bottom.Y, bottom.W, bottom.H));
        Assert.Equal((0f, 0.5f, 1f, 1f), (bottom.U0, bottom.V0, bottom.U1, bottom.V1));
    }

    [Fact]
    public void AMismatchedAspectIsLetterboxedIntoPartialCellQuads()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        // 32x16 px into a 2x2 grid of 10x20 cells (a 20x40 box): scaled to 20x10, centred at y 15..25.
        scene.VirtualImage(42, 32, 16, "c=2,r=2");
        PrintTwoByTwo(scene, row: 1, column: 2);

        var frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        Assert.Equal(2, frame.ImageDraws.QuadCount);
        ImageQuadInstance top = frame.ImageDraws.Quads[0], bottom = frame.ImageDraws.Quads[1];
        // Row 1's cells start at y 20: the image occupies y 35..40 of the upper grid row and y 40..45 of the lower one.
        Assert.Equal((20f, 35f, 20f, 5f), (top.X, top.Y, top.W, top.H));
        Assert.Equal((0f, 0f, 1f, 0.5f), (top.U0, top.V0, top.U1, top.V1));
        Assert.Equal((20f, 40f, 20f, 5f), (bottom.X, bottom.Y, bottom.W, bottom.H));
        Assert.Equal((0f, 0.5f, 1f, 1f), (bottom.U0, bottom.V0, bottom.U1, bottom.V1));
    }

    [Fact]
    public void PlaceholderQuadsFollowTheScaledCellSize()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        scene.VirtualImage(42, 16, 32, "c=2,r=2");
        PrintTwoByTwo(scene, row: 1, column: 2);

        var frame = scene.Compose(800, 320, 10, 20, 2, 8, 40);   // scaled cells are 20x40

        ImageQuadInstance top = frame.ImageDraws.Quads[0];
        Assert.Equal(2, frame.ImageDraws.QuadCount);
        Assert.Equal((40f, 40f, 40f, 40f), (top.X, top.Y, top.W, top.H));
    }

    [Fact]
    public void PlaceholderRowsTouchAtAFractionalDpiScale()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        scene.VirtualImage(42, 16, 32, "c=2,r=2");
        PrintTwoByTwo(scene, row: 1, column: 2);

        // Scaled cells are 11.25 x 22.5 px. Both edges snap like the cell backgrounds: x 22.5 -> 23 .. 45,
        // the upper run's y 22.5 -> 23 .. 45 and the lower one's 45 .. 67.5 -> 68; they share the edge at y 45.
        var frame = scene.Compose(450, 180, 9, 18, 1.25f, 8, 40);

        Assert.Equal(2, frame.ImageDraws.QuadCount);
        ImageQuadInstance top = frame.ImageDraws.Quads[0], bottom = frame.ImageDraws.Quads[1];
        Assert.Equal((23f, 23f, 22f, 22f), (top.X, top.Y, top.W, top.H));
        Assert.Equal((23f, 45f, 22f, 23f), (bottom.X, bottom.Y, bottom.W, bottom.H));
        Assert.Equal(top.Y + top.H, bottom.Y);
        Assert.Equal((0f, 0f, 1f, 0.5f), (top.U0, top.V0, top.U1, top.V1));
        Assert.Equal((0f, 0.5f, 1f, 1f), (bottom.U0, bottom.V0, bottom.U1, bottom.V1));
    }

    [Fact]
    public void PlaceholderTextWithoutAVirtualPlacementDrawsNoImage()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        PrintTwoByTwo(scene, row: 1, column: 2);

        var frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);
        Assert.Equal(0, frame.ImageDraws.QuadCount);

        // A virtual placement of another image does not match either.
        scene.VirtualImage(7, 16, 32, "c=2,r=2");
        frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);
        Assert.Equal(0, frame.ImageDraws.QuadCount);
    }

    [Fact]
    public void ADeletedVirtualPlacementStopsTheImageOnTheNextFrame()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        var image = scene.VirtualImage(42, 16, 32, "c=2,r=2");
        PrintTwoByTwo(scene, row: 1, column: 2);
        Assert.Equal(2, scene.Compose(400, 160, 10, 20, 1, 8, 40).ImageDraws.QuadCount);

        scene.Store.Delete(image);

        Assert.Equal(0, scene.Compose(400, 160, 10, 20, 1, 8, 40).ImageDraws.QuadCount);
    }

    [Fact]
    public void ARelativePlacementOnAPlaceholderIsDrawnAtTheGridsTopLeftCell()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        scene.VirtualImage(42, 16, 32, "c=2,r=2,p=1");
        Assert.True(scene.Store.TryAdd(ImagePixels.Allocate(10, 20, ImagePixelFormat.Rgba8Premultiplied), 7, 0, false, 0, out var child));
        Assert.Equal(KittyParseResult.Ok, KittyControlParser.TryParse(Encoding.ASCII.GetBytes("P=42,Q=1,H=3,V=1,z=1"), out var cmd));
        Assert.Equal(KittyStatus.Ok, scene.Store.TryPlaceRelative(child!, in cmd, 10, 20, out _));
        PrintTwoByTwo(scene, row: 1, column: 2, sgr: "38;5;42;58;5;1");

        var frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        // Placeholder quads (z 0) and the child (z 1, 10x20 px native) at cell (1 + 1, 2 + 3) = x 50, y 40.
        Assert.Equal(3, frame.ImageDraws.QuadCount);
        ImageQuadInstance childQuad = frame.ImageDraws.Quads[2];
        Assert.Equal((50f, 40f, 10f, 20f), (childQuad.X, childQuad.Y, childQuad.W, childQuad.H));
    }

    [Fact]
    public void ThePlaceholderRuneNeverDrawsAGlyphWhateverTheStoreHolds()
    {
        // No graphics configured at all: the suppression must not depend on any store state.
        using var tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: 2, columns: 10);
        using var manager = new TerminalTabManager(SilentPty.Create);
        using var atlas = new GlyphAtlas(SKTypeface.Default, 14f, initialSize: 256);
        using var composer = new TerminalSceneComposer(atlas, SKTypeface.Default, 14f);
        var adapter = tab.ActivePane.Session.Adapter;
        adapter.Buffer.SetCursor(0, 0);
        adapter.OnSetGraphicsRendition("38;5;42;48;2;1;2;3");     // an explicit background: the cell still paints it
        adapter.OnPrint(P + D0 + D0 + P);

        var theme = BuiltInThemes.DarkPlus;
        var frame = composer.Compose(
            tab, manager, theme, new SgrColorArgb(theme.Foreground), new SgrColorArgb(0x803385DB),
            100, 40, 10, 20, 1, 2, 10, new PaddingUserConfig { Left = 0, Top = 0, Right = 0, Bottom = 0 },
            false, false, false, false, new SearchOverlayRenderState(false, string.Empty, -1, 0, null), null);

        int found = 0;
        for (int i = 0; i < frame.InstanceCount; i++)
        {
            CellInstance cell = frame.Instances[i];
            if (cell.Row != 0 || cell.Col > 1)
                continue;
            found++;
            Assert.Equal((short)0, cell.GlyphW);
            Assert.Equal((short)0, cell.GlyphH);
            Assert.Equal((byte)1, cell.BgR);                     // background-only instance
            Assert.Equal((byte)255, cell.BgA);
        }
        Assert.Equal(2, found);
    }

    [Fact]
    public void SelectingPlaceholderCellsKeepsTheImageAndPaintsTheSelectionBackground()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        scene.VirtualImage(42, 16, 32, "c=2,r=2");
        PrintTwoByTwo(scene, row: 1, column: 2);
        scene.Tab.ActivePane.Selection.StartSelection(1, 2);
        scene.Tab.ActivePane.Selection.UpdateSelection(1, 3);

        var frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        Assert.Equal(2, frame.ImageDraws.QuadCount);
        bool painted = false;
        for (int i = 0; i < frame.InstanceCount; i++)
        {
            CellInstance cell = frame.Instances[i];
            if (cell.Row != 1 || cell.Col != 2)
                continue;
            painted = true;
            Assert.Equal((byte)0x33, cell.BgR);
            Assert.Equal((short)0, cell.GlyphW);
        }
        Assert.True(painted);
    }
}

