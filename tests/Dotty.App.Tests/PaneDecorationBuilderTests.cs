using System;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class PaneDecorationBuilderTests
{
    private const uint DividerColor = 0xFF102030;
    private const uint ActiveColor = 0xFF4080C0;

    [Theory]
    [InlineData(2.0, 1f, 2f)]
    [InlineData(2.0, 2f, 4f)]
    [InlineData(2.0, 1.5f, 3f)]
    [InlineData(1.0, 1.25f, 1f)]
    [InlineData(1.0, 1.5f, 2f)]
    [InlineData(0.2, 1f, 1f)]
    [InlineData(0.0, 2f, 0f)]
    [InlineData(-3.0, 1f, 0f)]
    [InlineData(double.NaN, 1f, 0f)]
    [InlineData(double.PositiveInfinity, 1f, 0f)]
    [InlineData(100.0, 1f, 32f)]
    [InlineData(100.0, 2f, 64f)]
    public void ResolveThickness_ScalesRoundsAndClamps(double configured, float scale, float expected)
    {
        Assert.Equal(expected, PaneDecorationBuilder.ResolveThickness(configured, scale));
    }

    [Fact]
    public void Build_LonePaneDrawsNothing()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        tree.Layout(800, 600, 10, 20, dividerThickness: 2f);
        var destination = new ChromeQuadInstance[8];

        int written = PaneDecorationBuilder.Build(tree.Root, tree.ActivePane.Bounds, 0, 0, 800, 600, 2f,
            activeBorder: true, DividerColor, ActiveColor, destination);

        Assert.Equal(0, written);
        Assert.Equal(0, PaneDecorationBuilder.MaxQuadCount(1));
    }

    [Fact]
    public void Build_ZeroThicknessDrawsNothing()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        tree.Split(tree.ActivePane, SplitDirection.Vertical);
        tree.Layout(800, 600, 10, 20, dividerThickness: 0f);
        var destination = new ChromeQuadInstance[8];

        Assert.Equal(0, PaneDecorationBuilder.Build(tree.Root, tree.ActivePane.Bounds, 0, 0, 800, 600, 0f,
            activeBorder: true, DividerColor, ActiveColor, destination));
    }

    [Fact]
    public void Build_VerticalSplitDrawsDividerAndRingOnlyInsideTheGap()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        var left = tree.ActivePane;
        tree.Split(left, SplitDirection.Vertical);
        tree.ActivePane = left;
        tree.Layout(800, 600, 10, 20, dividerThickness: 2f);
        var destination = new ChromeQuadInstance[PaneDecorationBuilder.MaxQuadCount(2)];

        int withoutBorder = PaneDecorationBuilder.Build(tree.Root, left.Bounds, 0, 0, 800, 600, 2f,
            activeBorder: false, DividerColor, ActiveColor, destination);
        Assert.Equal(1, withoutBorder);
        AssertQuad(destination[0], 399, 0, 2, 600, DividerColor);

        int written = PaneDecorationBuilder.Build(tree.Root, left.Bounds, 0, 0, 800, 600, 2f,
            activeBorder: true, DividerColor, ActiveColor, destination);

        // The left pane touches the window on three sides: only its right edge borders a divider.
        Assert.Equal(2, written);
        AssertQuad(destination[0], 399, 0, 2, 600, DividerColor);
        AssertQuad(destination[1], 399, 0, 2, 600, ActiveColor);
    }

    [Fact]
    public void Build_NestedSplitsOffsetByOriginDrawEveryDividerAndClipTheRing()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        var left = tree.ActivePane;
        var right = tree.Split(left, SplitDirection.Vertical);
        var bottomRight = tree.Split(right, SplitDirection.Horizontal);
        tree.ActivePane = bottomRight;
        tree.Layout(800, 600, 10, 20, dividerThickness: 4f);
        var destination = new ChromeQuadInstance[PaneDecorationBuilder.MaxQuadCount(tree.Leaves.Count)];

        int written = PaneDecorationBuilder.Build(tree.Root, bottomRight.Bounds, originX: 10, originY: 30,
            areaWidth: 800, areaHeight: 600, thickness: 4f, activeBorder: true, DividerColor, ActiveColor, destination);

        // Root divider at x 398..402, the nested one at y 298..302 over x 402..800; the active (bottom-right)
        // pane is 402..800 x 302..600, so only its top and left ring pieces survive the clip.
        Assert.Equal(4, written);
        AssertQuad(destination[0], 408, 30, 4, 600, DividerColor);
        AssertQuad(destination[1], 412, 328, 398, 4, DividerColor);
        AssertQuad(destination[2], 408, 328, 402, 4, ActiveColor);
        AssertQuad(destination[3], 408, 332, 4, 298, ActiveColor);
    }

    [Theory]
    [InlineData(0.5f, 0.25f)]
    [InlineData(1.5f, 2.5f)]
    [InlineData(0.49f, 0.51f)]
    public void Build_SnapsToWholePixelsWithoutChangingTheThickness(float originX, float originY)
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        var top = tree.ActivePane;
        tree.Split(top, SplitDirection.Horizontal);
        ((SplitPaneNode)tree.Root).SplitRatio = 0.37f;
        tree.ActivePane = top;
        tree.Layout(801, 599, 10, 20, dividerThickness: 3f);
        var destination = new ChromeQuadInstance[PaneDecorationBuilder.MaxQuadCount(2)];

        int written = PaneDecorationBuilder.Build(tree.Root, top.Bounds, originX, originY, 801, 599, 3f,
            activeBorder: true, DividerColor, ActiveColor, destination);

        Assert.Equal(2, written);
        for (int i = 0; i < written; i++)
        {
            var quad = destination[i];
            Assert.Equal(MathF.Floor(quad.X), quad.X);
            Assert.Equal(MathF.Floor(quad.Y), quad.Y);
            Assert.Equal(3f, quad.H);
        }
        Assert.Equal(destination[0].Y, destination[1].Y);
        Assert.Equal(destination[0].X, destination[1].X);
        Assert.Equal(destination[0].W, destination[1].W);
    }

    [Fact]
    public void Build_StopsAtDestinationCapacity()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        var left = tree.ActivePane;
        var right = tree.Split(left, SplitDirection.Vertical);
        tree.Split(right, SplitDirection.Horizontal);
        tree.Layout(800, 600, 10, 20, dividerThickness: 2f);
        var destination = new ChromeQuadInstance[1];

        Assert.Equal(1, PaneDecorationBuilder.Build(tree.Root, left.Bounds, 0, 0, 800, 600, 2f,
            activeBorder: true, DividerColor, ActiveColor, destination));
    }

    private static void AssertQuad(ChromeQuadInstance quad, float x, float y, float w, float h, uint color)
    {
        Assert.Equal(x, quad.X);
        Assert.Equal(y, quad.Y);
        Assert.Equal(w, quad.W);
        Assert.Equal(h, quad.H);
        Assert.Equal(0f, quad.Radius);
        Assert.Equal(((color >> 16) & 0xFF) / 255f, quad.TopR);
        Assert.Equal(((color >> 8) & 0xFF) / 255f, quad.TopG);
        Assert.Equal((color & 0xFF) / 255f, quad.TopB);
        Assert.Equal(1f, quad.TopA);
        Assert.Equal(quad.TopR, quad.BottomR);
        Assert.Equal(quad.TopA, quad.BottomA);
    }
}
