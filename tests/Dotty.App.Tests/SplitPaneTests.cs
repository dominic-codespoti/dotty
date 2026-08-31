using System;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Sessions;
using Xunit;

namespace Dotty.App.Tests;

public class SplitPaneTests
{
    [Fact]
    public void PaneRect_Contains_WorksCorrectly()
    {
        var rect = new PaneRect(10, 20, 100, 50);

        Assert.True(rect.Contains(10, 20));
        Assert.True(rect.Contains(50, 40));
        Assert.False(rect.Contains(9, 20));
        Assert.False(rect.Contains(110, 40));
        Assert.False(rect.Contains(50, 70));
    }

    [Fact]
    public void PaneTree_InitialState_HasSingleActiveLeaf()
    {
        using var tree = new PaneTree(rows: 24, columns: 80);

        Assert.NotNull(tree.Root);
        Assert.NotNull(tree.ActivePane);
        Assert.Single(tree.Leaves);
        Assert.Same(tree.Root, tree.ActivePane);
    }

    [Fact]
    public void PaneTree_Split_CreatesSplitNodeAndActivatesNewLeaf()
    {
        using var tree = new PaneTree(rows: 24, columns: 80);
        var initial = tree.ActivePane;

        var second = tree.Split(initial, SplitDirection.Vertical);

        Assert.Equal(2, tree.Leaves.Count);
        Assert.Same(second, tree.ActivePane);
        Assert.IsType<SplitPaneNode>(tree.Root);

        var split = (SplitPaneNode)tree.Root;
        Assert.Equal(SplitDirection.Vertical, split.Direction);
        Assert.Same(initial, split.First);
        Assert.Same(second, split.Second);
        Assert.Same(split, initial.Parent);
        Assert.Same(split, second.Parent);
    }

    [Fact]
    public void PaneTree_Layout_CalculatesCorrectBounds()
    {
        using var tree = new PaneTree(rows: 24, columns: 80);
        var left = tree.ActivePane;
        var right = tree.Split(left, SplitDirection.Vertical);

        tree.Layout(totalWidth: 800, totalHeight: 600, cellWidth: 10, cellHeight: 20, dividerThickness: 2f);

        // Available width = 800 - 2 = 798 -> each 399
        Assert.Equal(new PaneRect(0, 0, 399, 600), left.Bounds);
        Assert.Equal(new PaneRect(401, 0, 399, 600), right.Bounds);
        var split = (SplitPaneNode)tree.Root;
        Assert.Equal(new PaneRect(399, 0, 2, 600), split.DividerBounds);

        Assert.Equal(39, left.Columns);
        Assert.Equal(30, left.Rows);
        Assert.Equal(39, right.Columns);
        Assert.Equal(30, right.Rows);
    }

    [Fact]
    public void PaneTree_FindPaneAt_And_HitTestDivider()
    {
        using var tree = new PaneTree(rows: 24, columns: 80);
        var top = tree.ActivePane;
        var bottom = tree.Split(top, SplitDirection.Horizontal);

        tree.Layout(totalWidth: 800, totalHeight: 600, cellWidth: 10, cellHeight: 20, dividerThickness: 4f);

        // Available height = 600 - 4 = 596 -> each 298
        // Top: 0..298, Divider: 298..302, Bottom: 302..600
        Assert.Same(top, tree.FindPaneAt(100, 100));
        Assert.Same(bottom, tree.FindPaneAt(100, 400));

        var hitDivider = tree.HitTestDivider(100, 300, hitTolerance: 4f);
        Assert.NotNull(hitDivider);
        Assert.Same(tree.Root, hitDivider);
    }

    [Fact]
    public void PaneTree_NavigateFocus_MovesInCorrectDirection()
    {
        using var tree = new PaneTree(rows: 24, columns: 80);
        var left = tree.ActivePane;
        var right = tree.Split(left, SplitDirection.Vertical);

        tree.Layout(totalWidth: 800, totalHeight: 600, cellWidth: 10, cellHeight: 20, dividerThickness: 2f);

        var rightPane = tree.NavigateFocus(left, PaneDirection.Right);
        Assert.Same(right, rightPane);

        var leftPane = tree.NavigateFocus(right, PaneDirection.Left);
        Assert.Same(left, leftPane);

        Assert.Null(tree.NavigateFocus(left, PaneDirection.Up));
    }

    [Fact]
    public void PaneTree_Close_RemovesLeafAndPromotesSibling()
    {
        using var tree = new PaneTree(rows: 24, columns: 80);
        var first = tree.ActivePane;
        var second = tree.Split(first, SplitDirection.Vertical);
        var third = tree.Split(second, SplitDirection.Horizontal);

        Assert.Equal(3, tree.Leaves.Count);

        var closed = tree.Close(third);
        Assert.True(closed);
        Assert.Equal(2, tree.Leaves.Count);
        Assert.Same(second, tree.ActivePane);

        closed = tree.Close(second);
        Assert.True(closed);
        Assert.Single(tree.Leaves);
        Assert.Same(first, tree.Root);
        Assert.Null(first.Parent);

        // Cannot close the last pane
        Assert.False(tree.Close(first));
    }
}
