using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Image-quad composition in <see cref="TerminalSceneComposer"/>. Synthetic <see cref="KittyRenderPlacement"/>s are
/// attached to each captured pane snapshot through the composer's <c>LeafSnapshotCaptured</c> seam (the real store
/// capture is a later stage). All expected pixel values are derived by hand in the comments.
/// </summary>
public sealed class TerminalSceneComposerImageTests
{
    private sealed record LeafImages(KittyRenderPlacement[] Placements, long DeadlineTicks = 0);

    private sealed class Scene : IDisposable
    {
        private readonly TerminalTabManager _manager = new(SilentPty.Create);
        private readonly GlyphAtlas _atlas = new(SKTypeface.Default, 14f, initialSize: 256);
        private readonly Dictionary<LeafPane, LeafImages> _images = new();

        public Scene(int rows, int columns)
        {
            Tab = new TerminalTab(ptyFactory: SilentPty.Create, rows: rows, columns: columns);
            Composer = new TerminalSceneComposer(_atlas, SKTypeface.Default, 14f);
            Composer.LeafSnapshotCaptured = (leaf, snapshot) =>
            {
                if (!_images.TryGetValue(leaf, out LeafImages? images))
                    return;
                KittyRenderSnapshot graphics = snapshot.BeginGraphics();
                foreach (KittyRenderPlacement placement in images.Placements)
                    graphics.Add(placement);
                graphics.NextAnimationDeadlineTicks = images.DeadlineTicks;
            };
        }

        public TerminalTab Tab { get; }
        public TerminalSceneComposer Composer { get; }

        public void Provide(LeafPane leaf, long deadlineTicks, params KittyRenderPlacement[] placements) =>
            _images[leaf] = new LeafImages(placements, deadlineTicks);

        public void Clear() => _images.Clear();

        public TerminalSceneFrame Compose(
            int framebufferWidth, int framebufferHeight, float cellWidth, float cellHeight, float scale,
            int rows, int columns, PaddingUserConfig? padding = null, bool showTabBar = false)
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
                padding: padding ?? NoPadding(),
                showTabBar: showTabBar,
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

    private static PaddingUserConfig NoPadding() => new() { Left = 0, Top = 0, Right = 0, Bottom = 0 };

    private static ImagePixels NewPixels() => ImagePixels.Allocate(2, 2, ImagePixelFormat.Rgba8Premultiplied);

    private static KittyRenderPlacement Placement(
        ImagePixels pixels, int viewRow, int col, int cols, int rows,
        int z = 0, uint imageId = 1, uint placementId = 1, float offX = 0f, float offY = 0f) => new()
        {
            Pixels = pixels,
            ImageSerial = pixels.Serial,
            ViewRow = viewRow,
            Col = col,
            Cols = cols,
            Rows = rows,
            OffXCells = offX,
            OffYCells = offY,
            U0 = 0f,
            V0 = 0f,
            U1 = 1f,
            V1 = 1f,
            Z = z,
            ImageId = imageId,
            PlacementId = placementId,
        };

    private static void AssertAlive(ImagePixels pixels) => pixels.Retain().Release();

    private static void AssertFreed(ImagePixels pixels) =>
        Assert.Throws<ObjectDisposedException>(() => pixels.Retain());

    [Fact]
    public void Compose_WithoutGraphicsProducesAnEmptyDrawList()
    {
        using var scene = new Scene(rows: 8, columns: 40);

        TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        Assert.Equal(0, frame.ImageDraws.DrawCount);
        Assert.Equal(0, frame.ImageDraws.QuadCount);
        Assert.Equal(0L, frame.NextImageDeadlineTicks);
    }

    [Fact]
    public void Compose_SinglePaneQuadUsesCellGeometry()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        using ImagePixels pixels = NewPixels();
        // Col 2, row 1, 3x2 cells at 10x20 px: x = 2*10, y = 1*20, w = 3*10, h = 2*20.
        scene.Provide(scene.Tab.ActivePane, 0, Placement(pixels, viewRow: 1, col: 2, cols: 3, rows: 2));

        TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        ImageDrawList draws = frame.ImageDraws;
        Assert.Equal(1, draws.DrawCount);
        Assert.Equal(1, draws.QuadCount);
        Assert.Equal(new[] { 0, 0, 0, 1 }, draws.LayerDrawStart); // one draw, in the AboveText layer (z = 0)
        Assert.Same(pixels, draws.Draws[0].Pixels);
        Assert.Equal(ImageLayer.AboveText, draws.Draws[0].Layer);
        ImageQuadInstance quad = draws.Quads[0];
        Assert.Equal((20f, 20f, 30f, 40f), (quad.X, quad.Y, quad.W, quad.H));
        Assert.Equal((0f, 0f, 1f, 1f), (quad.U0, quad.V0, quad.U1, quad.V1));
        Assert.Equal(1f, quad.Alpha);
    }

    [Theory]
    // Cell size (before scale) and scale; the 40x8 grid always fills the framebuffer exactly, so nothing is clipped.
    // Scaled cell = cell * scale; placement is col 2, row 1, 3x2 cells: x = 2*cw, y = 1*ch, w = 3*cw, h = 2*ch.
    [InlineData(10f, 20f, 1f, 400, 160, 20f, 20f, 30f, 40f)]
    [InlineData(15f, 30f, 1f, 600, 240, 30f, 30f, 45f, 60f)]     // zoomed font: 15x30
    [InlineData(10f, 20f, 2f, 800, 320, 40f, 40f, 60f, 80f)]     // 2x DPI: 20x40
    [InlineData(8f, 16f, 1.5f, 480, 192, 24f, 24f, 36f, 48f)]    // 1.5x DPI: 12x24
    public void Compose_QuadFollowsScaledCellSize(
        float cellWidth, float cellHeight, float scale, int framebufferWidth, int framebufferHeight,
        float x, float y, float w, float h)
    {
        using var scene = new Scene(rows: 8, columns: 40);
        using ImagePixels pixels = NewPixels();
        scene.Provide(scene.Tab.ActivePane, 0, Placement(pixels, viewRow: 1, col: 2, cols: 3, rows: 2));

        TerminalSceneFrame frame = scene.Compose(framebufferWidth, framebufferHeight, cellWidth, cellHeight, scale, 8, 40);

        Assert.Equal(1, frame.ImageDraws.QuadCount);
        ImageQuadInstance quad = frame.ImageDraws.Quads[0];
        Assert.Equal((x, y, w, h), (quad.X, quad.Y, quad.W, quad.H));
    }

    [Fact]
    public void Compose_PaddingAndSubCellOffsetsScaleWithDpi()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        using ImagePixels pixels = NewPixels();
        // scale 2 => padLeft = 8*2 = 16, padTop = 4*2 = 8, scaled cell 20x40; the pane grid is 40x8 cells = 800x320
        // at (16, 8), so the framebuffer is 816x328.
        var padding = new PaddingUserConfig { Left = 8, Top = 4, Right = 0, Bottom = 0 };
        // x = floor(16 + 3*20 + 0.5*20) = 86; y = floor(8 + 2*40 + 0.25*40) = 98; size 2x1 cells = 40x40.
        scene.Provide(scene.Tab.ActivePane, 0, Placement(pixels, viewRow: 2, col: 3, cols: 2, rows: 1, offX: 0.5f, offY: 0.25f));

        TerminalSceneFrame frame = scene.Compose(816, 328, 10, 20, 2, 8, 40, padding);

        Assert.Equal(1, frame.ImageDraws.QuadCount);
        ImageQuadInstance quad = frame.ImageDraws.Quads[0];
        Assert.Equal((86f, 98f, 40f, 40f), (quad.X, quad.Y, quad.W, quad.H));
    }

    [Fact]
    public void Compose_ClipsAtTheGridInsideThePadding()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        using ImagePixels pixels = NewPixels();
        var padding = new PaddingUserConfig { Left = 8, Top = 4, Right = 0, Bottom = 0 };
        // Row -1, 1x2 cells at 20x40: spans y = 8 - 40 = -32 .. 48 and x = 16 .. 36. The grid starts at y = 8
        // (the padding), so the quad keeps y = 8 .. 48 and the lower half of the image: v 0.5 .. 1.
        scene.Provide(scene.Tab.ActivePane, 0, Placement(pixels, viewRow: -1, col: 0, cols: 1, rows: 2));

        TerminalSceneFrame frame = scene.Compose(816, 328, 10, 20, 2, 8, 40, padding);

        ImageQuadInstance quad = frame.ImageDraws.Quads[0];
        Assert.Equal((16f, 8f, 20f, 40f), (quad.X, quad.Y, quad.W, quad.H));
        Assert.Equal((0f, 0.5f, 1f, 1f), (quad.U0, quad.V0, quad.U1, quad.V1));
    }

    [Fact]
    public void Compose_PaneRowsStartBelowTheTabBarWithoutCountingItTwice()
    {
        const float cellHeight = 20f;
        int barRows = TabBarLayout.ComputeBarRows(UserConfigService.Current.TabBar.Height, cellHeight);
        Assert.True(barRows >= 1);
        using var scene = new Scene(rows: 8, columns: 40);
        using ImagePixels pixels = NewPixels();
        // Placement id 1: row 1 sits one row below the pane origin, which itself is barRows rows down:
        // y = (barRows + 1) * 20.
        // Placement id 2: row -1 (3 rows tall, 60 px) starts one row above the pane, i.e. under the bar's last row;
        // it is clipped to the pane's top edge at barRows * 20, keeping 40 of its 60 px (v 1/3 .. 1).
        // Same image and z, so the quads are ordered by placement id.
        scene.Provide(scene.Tab.ActivePane, 0,
            Placement(pixels, viewRow: 1, col: 2, cols: 3, rows: 2, placementId: 1),
            Placement(pixels, viewRow: -1, col: 0, cols: 1, rows: 3, placementId: 2));
        int framebufferHeight = (8 + barRows) * 20;

        TerminalSceneFrame frame = scene.Compose(400, framebufferHeight, 10, cellHeight, 1, 8, 40, showTabBar: true);

        Assert.Equal(2, frame.ImageDraws.QuadCount);
        ImageQuadInstance below = frame.ImageDraws.Quads[0];
        ImageQuadInstance clipped = frame.ImageDraws.Quads[1];
        Assert.Equal((20f, (barRows + 1) * 20f, 30f, 40f), (below.X, below.Y, below.W, below.H));
        Assert.Equal((0f, barRows * 20f, 10f, 40f), (clipped.X, clipped.Y, clipped.W, clipped.H));
        Assert.InRange(clipped.V0, 0.33333f, 0.33334f);
        Assert.Equal(1f, clipped.V1);
    }

    [Fact]
    public void Compose_DoesNotApplyTheScrollOffsetAgainToViewportRows()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        LeafPane pane = scene.Tab.ActivePane;
        pane.Session.Adapter.Buffer.ScrollUpLines(3);
        pane.ScrollTo(2, pane.Session.Adapter.Buffer.ScrollbackCount);
        Assert.Equal(2, pane.ScrollOffset);
        using ImagePixels pixels = NewPixels();
        // ViewRow is already viewport-relative (the capture applied the scroll offset): row 3 => y = 3 * 20.
        scene.Provide(pane, 0, Placement(pixels, viewRow: 3, col: 0, cols: 1, rows: 1));

        TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        ImageQuadInstance quad = frame.ImageDraws.Quads[0];
        Assert.Equal((0f, 60f, 10f, 20f), (quad.X, quad.Y, quad.W, quad.H));
    }

    [Fact]
    public void Compose_SplitPanesGetTheirOwnOriginAndClipAtTheirOwnEdge()
    {
        using var scene = new Scene(rows: 4, columns: 8);
        LeafPane first = scene.Tab.ActivePane;
        LeafPane second = scene.Tab.PaneTree.Split(first, SplitDirection.Vertical);
        // 160x80 framebuffer, 10x20 cells, divider 2 px: each pane is (158 / 2) = 79 px wide => 7 columns.
        // The second pane starts at x = 79 + 2 = 81 px, which rounds to column 8 (80 px). Pane grids are
        // x 0..70 and x 80..150, both y 0..80.
        using ImagePixels firstImage = NewPixels();
        using ImagePixels secondImage = NewPixels();
        // First pane: col 5, 4 cells wide: x = 50..90 is clipped to its grid edge 70 (half of the image: u 0..0.5).
        scene.Provide(first, 0, Placement(firstImage, viewRow: 1, col: 5, cols: 4, rows: 1, imageId: 1));
        scene.Provide(second, 0,
            // Second pane: col 1, 3x2 cells: x = (8 + 1) * 10 = 90, y = 0.
            Placement(secondImage, viewRow: 0, col: 1, cols: 3, rows: 2, imageId: 2, placementId: 1),
            // col 5, 4 cells wide: x = 130..170 clipped to the grid edge 150 (u 0..0.5), y = 60.
            Placement(secondImage, viewRow: 3, col: 5, cols: 4, rows: 1, imageId: 2, placementId: 2));
        scene.Tab.PaneTree.ActivePane = second;

        TerminalSceneFrame frame = scene.Compose(160, 80, 10, 20, 1, 4, 16);

        ImageDrawList draws = frame.ImageDraws;
        Assert.Equal(7, first.Columns);
        Assert.Equal(7, second.Columns);
        Assert.Equal(2, draws.DrawCount);
        Assert.Equal(3, draws.QuadCount);
        Assert.Same(firstImage, draws.Draws[0].Pixels);
        Assert.Equal((0, 1), (draws.Draws[0].FirstQuad, draws.Draws[0].QuadCount));
        Assert.Same(secondImage, draws.Draws[1].Pixels);
        Assert.Equal((1, 2), (draws.Draws[1].FirstQuad, draws.Draws[1].QuadCount));

        ImageQuadInstance a = draws.Quads[0];
        Assert.Equal((50f, 20f, 20f, 20f), (a.X, a.Y, a.W, a.H));
        Assert.Equal((0f, 0f, 0.5f, 1f), (a.U0, a.V0, a.U1, a.V1));
        ImageQuadInstance b = draws.Quads[1];
        Assert.Equal((90f, 0f, 30f, 40f), (b.X, b.Y, b.W, b.H));
        ImageQuadInstance c = draws.Quads[2];
        Assert.Equal((130f, 60f, 20f, 20f), (c.X, c.Y, c.W, c.H));
        Assert.Equal((0f, 0f, 0.5f, 1f), (c.U0, c.V0, c.U1, c.V1));
    }

    [Fact]
    public void Compose_AssignsLayersAtTheMinusTwoToThirtyAndZeroBoundaries()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        int[] zs =
        {
            0, int.MaxValue, -1_073_741_824, int.MinValue, -1, -1_073_741_825,
        };
        var images = new ImagePixels[zs.Length];
        try
        {
            var placements = new KittyRenderPlacement[zs.Length];
            for (int i = 0; i < zs.Length; i++)
            {
                images[i] = NewPixels();
                placements[i] = Placement(images[i], viewRow: 0, col: 0, cols: 1, rows: 1, z: zs[i], imageId: (uint)(i + 1));
            }

            scene.Provide(scene.Tab.ActivePane, 0, placements);

            TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

            ImageDrawList draws = frame.ImageDraws;
            // z < -2^30 below backgrounds; -2^30 <= z < 0 below text; z >= 0 above text; ascending z within a layer.
            //   BelowBackground: int.MinValue (index 3), -2^30 - 1 (index 5)
            //   BelowText:       -2^30 (index 2), -1 (index 4)
            //   AboveText:       0 (index 0), int.MaxValue (index 1)
            Assert.Equal(6, draws.DrawCount);
            Assert.Equal(new[] { 0, 2, 4, 6 }, draws.LayerDrawStart);
            int[] expectedOrder = { 3, 5, 2, 4, 0, 1 };
            ImageLayer[] expectedLayers =
            {
                ImageLayer.BelowBackground, ImageLayer.BelowBackground,
                ImageLayer.BelowText, ImageLayer.BelowText,
                ImageLayer.AboveText, ImageLayer.AboveText,
            };
            for (int d = 0; d < expectedOrder.Length; d++)
            {
                Assert.Same(images[expectedOrder[d]], draws.Draws[d].Pixels);
                Assert.Equal(expectedLayers[d], draws.Draws[d].Layer);
            }
        }
        finally
        {
            foreach (ImagePixels pixels in images)
                pixels?.Dispose();
        }
    }

    [Theory]
    [InlineData(500L, 300L, 300L)]
    [InlineData(300L, 500L, 300L)]
    [InlineData(0L, 700L, 700L)]   // 0 = no animation, never the minimum
    [InlineData(700L, 0L, 700L)]
    [InlineData(0L, 0L, 0L)]
    public void Compose_NextImageDeadlineIsTheNonZeroMinimumOverPanes(long firstDeadline, long secondDeadline, long expected)
    {
        using var scene = new Scene(rows: 4, columns: 8);
        LeafPane first = scene.Tab.ActivePane;
        LeafPane second = scene.Tab.PaneTree.Split(first, SplitDirection.Vertical);
        using ImagePixels pixels = NewPixels();
        scene.Provide(first, firstDeadline, Placement(pixels, 0, 0, 1, 1));
        scene.Provide(second, secondDeadline, Placement(pixels, 0, 0, 1, 1));

        TerminalSceneFrame frame = scene.Compose(160, 80, 10, 20, 1, 4, 16);

        Assert.Equal(expected, frame.NextImageDeadlineTicks);
        Assert.Equal(expected, frame.ImageDraws.NextDeadlineTicks);
    }

    [Fact]
    public void Compose_ForgetsThePreviousFramesDeadline()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        using ImagePixels pixels = NewPixels();
        scene.Provide(scene.Tab.ActivePane, 4242, Placement(pixels, 0, 0, 1, 1));
        Assert.Equal(4242L, scene.Compose(400, 160, 10, 20, 1, 8, 40).NextImageDeadlineTicks);

        scene.Clear();
        TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        Assert.Equal(0L, frame.NextImageDeadlineTicks);
        Assert.Equal(0, frame.ImageDraws.DrawCount);
    }

    [Fact]
    public void Compose_PaneWithoutVisiblePlacementsStillReportsItsDeadlineButDrawsNothing()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        scene.Provide(scene.Tab.ActivePane, 99);

        TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        Assert.Equal(0, frame.ImageDraws.DrawCount);
        Assert.Equal(99L, frame.NextImageDeadlineTicks);
    }

    [Fact]
    public void Leases_FrameHoldsOneAndReleaseImageLeasesReturnsToBaseline()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        ImagePixels pixels = NewPixels(); // reference 1: the creator's
        scene.Provide(scene.Tab.ActivePane, 0, Placement(pixels, 0, 0, 1, 1));

        TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        // The snapshot's lease went away with the snapshot; the draw list holds its own, so dropping the
        // creator's reference must not free the buffer.
        pixels.Dispose();
        AssertAlive(pixels);
        Assert.Equal(1, frame.ImageDraws.DrawCount);

        frame.ReleaseImageLeases();

        AssertFreed(pixels);
        Assert.Equal(0, frame.ImageDraws.DrawCount);
        frame.ReleaseImageLeases(); // idempotent
    }

    [Fact]
    public void Leases_NextComposeReleasesThePreviousFramesLeasesWithoutAccumulating()
    {
        using var scene = new Scene(rows: 8, columns: 40);
        ImagePixels pixels = NewPixels();
        scene.Provide(scene.Tab.ActivePane, 0, Placement(pixels, 0, 0, 1, 1));

        scene.Compose(400, 160, 10, 20, 1, 8, 40);
        TerminalSceneFrame frame = scene.Compose(400, 160, 10, 20, 1, 8, 40);

        // One lease from the second frame only: a leaked first-frame lease would keep the buffer alive below.
        Assert.Equal(1, frame.ImageDraws.DrawCount);
        pixels.Dispose();
        AssertAlive(pixels);

        scene.Clear();
        scene.Compose(400, 160, 10, 20, 1, 8, 40); // starts by releasing the previous frame's lease

        AssertFreed(pixels);
    }

    [Fact]
    public void Leases_ComposerDisposalReleasesTheLastFramesLeases()
    {
        ImagePixels pixels = NewPixels();
        using (var scene = new Scene(rows: 8, columns: 40))
        {
            scene.Provide(scene.Tab.ActivePane, 0, Placement(pixels, 0, 0, 1, 1));
            scene.Compose(400, 160, 10, 20, 1, 8, 40);
            pixels.Dispose();
            AssertAlive(pixels);
        }

        AssertFreed(pixels);
    }

    [Fact]
    public async Task Compose_SkippedLeafContributesNoQuadsAndNoDeadline()
    {
        using var scene = new Scene(rows: 4, columns: 8);
        LeafPane first = scene.Tab.ActivePane;
        LeafPane second = scene.Tab.PaneTree.Split(first, SplitDirection.Vertical);
        using ImagePixels firstImage = NewPixels();
        using ImagePixels secondImage = NewPixels();
        scene.Provide(first, 500, Placement(firstImage, 0, 0, 1, 1, imageId: 1));
        scene.Provide(second, 100, Placement(secondImage, 0, 0, 1, 1, imageId: 2));
        // Settle pane sizes first: the layout pass would otherwise try to resize the pane whose lock we hold.
        scene.Compose(160, 80, 10, 20, 1, 4, 16);

        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        object syncRoot = second.Session.Adapter.Buffer.SyncRoot;
        Task holder = Task.Run(() =>
        {
            lock (syncRoot)
            {
                locked.Set();
                release.Wait();
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(locked.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            TerminalSceneFrame frame = scene.Compose(160, 80, 10, 20, 1, 4, 16);

            Assert.True(frame.IsIncomplete);
            Assert.Equal(1, frame.ImageDraws.DrawCount);
            Assert.Same(firstImage, frame.ImageDraws.Draws[0].Pixels);
            Assert.Equal(500L, frame.NextImageDeadlineTicks); // the skipped pane's 100 does not count
            frame.ReleaseImageLeases();
        }
        finally
        {
            release.Set();
            await holder;
        }
    }
}
