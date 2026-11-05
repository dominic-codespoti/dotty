using Dotty.Runtime.Panes;
using Xunit;

namespace Dotty.App.Tests;

public class PaneResizeTests
{
    [Theory]
    [InlineData(0.5f, 100f, 10f, 0.5f)]
    [InlineData(0.05f, 100f, 10f, 0.1f)]   // first side below 10 px
    [InlineData(0.95f, 100f, 10f, 0.9f)]   // second side below 10 px
    [InlineData(0.3f, 100f, 60f, 0.5f)]    // minimums cannot both fit: centred
    [InlineData(0f, 1000f, 0f, 0.01f)]     // absolute floor
    [InlineData(1f, 1000f, 0f, 0.99f)]
    [InlineData(float.NaN, 100f, 10f, 0.5f)]
    public void ClampRatio_KeepsMinimumOnBothSides(float ratio, float available, float min, float expected) =>
        Assert.Equal(expected, PaneResizeMath.ClampRatio(ratio, available, min), 4);

    [Theory]
    [InlineData(51f, 0f, 102f, 2f, 0f, 0.5f)]    // pointer at centre of a 100 px + 2 px divider
    [InlineData(26f, 0f, 102f, 2f, 0f, 0.25f)]
    [InlineData(126f, 100f, 102f, 2f, 0f, 0.25f)] // split offset by 100 px
    [InlineData(-50f, 0f, 102f, 2f, 20f, 0.2f)]   // dragged past the edge: clamp
    [InlineData(500f, 0f, 102f, 2f, 20f, 0.8f)]
    public void RatioFromPointer_CentresDividerUnderPointer(float pointer, float start, float length, float divider, float min, float expected) =>
        Assert.Equal(expected, PaneResizeMath.RatioFromPointer(pointer, start, length, divider, min), 4);

    [Theory]
    [InlineData(0.5f, 200f, 10f, 0.55f)]
    [InlineData(0.5f, 200f, -10f, 0.45f)]
    [InlineData(0.9f, 200f, 100f, 0.9f)] // clamped at 20 px min on the second side
    public void RatioAfterStep_MovesByPixels(float ratio, float available, float delta, float expected) =>
        Assert.Equal(expected, PaneResizeMath.RatioAfterStep(ratio, available, delta, 20f), 4);

    [Fact]
    public void ResolveHover_KeepsCurrentInsideKeepBandOnly()
    {
        object a = new(), b = new();
        Assert.Same(a, PaneResizeMath.ResolveHover(a, currentWithinKeep: true, enterHit: b));
        Assert.Same(b, PaneResizeMath.ResolveHover(a, currentWithinKeep: false, enterHit: b));
        Assert.Null(PaneResizeMath.ResolveHover(a, currentWithinKeep: false, enterHit: null));
        Assert.Same(b, PaneResizeMath.ResolveHover<object>(null, currentWithinKeep: true, enterHit: b));
    }

    [Theory]
    [InlineData(50f, 50f, 0f, true)]
    [InlineData(47f, 50f, 3f, true)]
    [InlineData(46f, 50f, 3f, false)]
    [InlineData(57f, 50f, 7f, true)]
    public void IsNear_UsesTolerance(float x, float y, float tolerance, bool expected) =>
        Assert.Equal(expected, PaneResizeMath.IsNear(new PaneRect(50, 0, 2, 100), x, y, tolerance));

    [Fact]
    public void IsNear_EmptyDividerNeverHits() =>
        Assert.False(PaneResizeMath.IsNear(default, 0, 0, 10));

    [Fact]
    public void Tree_DragAndKeyboardResizeClampToMinimumCells()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        var left = tree.ActivePane;
        var right = tree.Split(left, SplitDirection.Vertical);
        var split = (SplitPaneNode)tree.Root;
        tree.Layout(202, 100, 10, 20, 2);

        tree.DragDivider(split, 51, 10);
        Assert.Equal(0.25f, split.SplitRatio, 4);

        tree.DragDivider(split, 0, 10);
        tree.Layout(202, 100, 10, 20, 2);
        Assert.Equal(PaneResizeMath.MinColumns, left.Columns);

        tree.ActivePane = right;
        Assert.True(tree.ResizeActive(PaneDirection.Right, 1));
        Assert.Equal(0.15f, split.SplitRatio, 4);
        Assert.False(tree.ResizeActive(PaneDirection.Up)); // no horizontal split
    }

    [Fact]
    public void Tree_ZoomHidesOtherPanesAndEndsOnFocusChange()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        var first = tree.ActivePane;
        Assert.False(tree.ToggleZoom()); // single pane
        var second = tree.Split(first, SplitDirection.Horizontal);

        Assert.True(tree.ToggleZoom());
        tree.Layout(200, 100, 10, 20, 2);
        Assert.Same(second, Assert.Single(tree.VisibleLeaves));
        Assert.Equal(new PaneRect(0, 0, 200, 100), second.Bounds);
        Assert.Same(second, tree.FindPaneAt(1, 1));
        Assert.Null(tree.HitTestDivider(0, 0));

        tree.ActivePane = first;
        Assert.Null(tree.ZoomedPane);
        Assert.Equal(2, tree.VisibleLeaves.Count);
    }

    [Fact]
    public void Tree_EqualizeResetsNestedRatios()
    {
        using var tree = new PaneTree(rows: 24, columns: 80, ptyFactory: SilentPty.Create);
        var a = tree.ActivePane;
        var b = tree.Split(a, SplitDirection.Vertical);
        tree.Split(b, SplitDirection.Horizontal);
        var root = (SplitPaneNode)tree.Root;
        root.SplitRatio = 0.2f;
        ((SplitPaneNode)root.Second).SplitRatio = 0.7f;

        Assert.True(tree.Equalize());
        Assert.Equal(0.5f, root.SplitRatio);
        Assert.Equal(0.5f, ((SplitPaneNode)root.Second).SplitRatio);
    }
}
