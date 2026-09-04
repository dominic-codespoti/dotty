using System;
using System.Collections.Generic;
using Dotty.Runtime.Sessions;

namespace Dotty.Runtime.Panes;

public sealed class PaneTree : IDisposable
{
    private PaneNode _root;
    private LeafPane _activePane;
    private bool _isDisposed;

    public PaneNode Root => _root;

    public LeafPane ActivePane
    {
        get => _activePane;
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!ContainsLeaf(value))
                throw new InvalidOperationException("Active pane must belong to the pane tree.");
            _activePane = value;
        }
    }

    public IReadOnlyList<LeafPane> Leaves
    {
        get
        {
            var list = new List<LeafPane>();
            CollectLeaves(_root, list);
            return list;
        }
    }

    public PaneTree(LeafPane initialPane)
    {
        _activePane = initialPane ?? throw new ArgumentNullException(nameof(initialPane));
        _root = initialPane;
    }

    public PaneTree(string? workingDirectory = null, string? shell = null, int rows = 24, int columns = 80)
    {
        var session = new TerminalSession(rows: rows, columns: columns);
        if (!string.IsNullOrEmpty(workingDirectory) || !string.IsNullOrEmpty(shell))
        {
            session.StartWithOptions(shell: shell, workingDirectory: workingDirectory);
        }
        var initialPane = new LeafPane(session);
        _activePane = initialPane;
        _root = initialPane;
    }

    public LeafPane Split(LeafPane target, SplitDirection direction, string? workingDirectory = null, string? shell = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ContainsLeaf(target))
            throw new InvalidOperationException("Target pane does not belong to this pane tree.");

        var session = new TerminalSession(rows: Math.Max(1, target.Rows), columns: Math.Max(1, target.Columns));
        if (!string.IsNullOrEmpty(workingDirectory) || !string.IsNullOrEmpty(shell))
        {
            session.StartWithOptions(shell: shell, workingDirectory: workingDirectory);
        }

        var newPane = new LeafPane(session);
        var parent = target.Parent;

        var splitNode = new SplitPaneNode(direction, target, newPane, splitRatio: 0.5f);

        if (parent == null)
        {
            _root = splitNode;
        }
        else
        {
            parent.ReplaceChild(target, splitNode);
        }

        ActivePane = newPane;
        return newPane;
    }

    public bool Close(LeafPane target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ContainsLeaf(target))
            return false;

        var leaves = Leaves;
        if (leaves.Count <= 1)
        {
            return false;
        }

        var parent = target.Parent;
        if (parent == null)
        {
            return false;
        }

        var sibling = ReferenceEquals(parent.First, target) ? parent.Second : parent.First;
        var grandParent = parent.Parent;

        if (grandParent == null)
        {
            _root = sibling;
            sibling.Parent = null;
        }
        else
        {
            grandParent.ReplaceChild(parent, sibling);
        }

        target.Dispose();

        if (ReferenceEquals(_activePane, target))
        {
            if (sibling is LeafPane siblingLeaf)
            {
                _activePane = siblingLeaf;
            }
            else
            {
                var remaining = Leaves;
                _activePane = remaining[0];
            }
        }
        return true;
    }

    public void Layout(float totalWidth, float totalHeight, float cellWidth, float cellHeight, float dividerThickness = 2f)
    {
        if (totalWidth <= 0 || totalHeight <= 0) return;
        LayoutNode(_root, new PaneRect(0, 0, totalWidth, totalHeight), cellWidth, cellHeight, dividerThickness);
    }

    private static void LayoutNode(PaneNode node, PaneRect bounds, float cellWidth, float cellHeight, float dividerThickness)
    {
        if (node is LeafPane leaf)
        {
            leaf.Bounds = bounds;
            if (cellWidth > 0 && cellHeight > 0)
            {
                var cols = Math.Max(1, (int)Math.Floor(bounds.Width / cellWidth));
                var rows = Math.Max(1, (int)Math.Floor(bounds.Height / cellHeight));
                if (cols != leaf.Columns || rows != leaf.Rows)
                {
                    leaf.Columns = cols;
                    leaf.Rows = rows;
                    leaf.Session.Resize(cols, rows);
                }
            }
        }
        else if (node is SplitPaneNode split)
        {
            float firstWidth, firstHeight, secondWidth, secondHeight;
            float secondX, secondY;
            PaneRect divider;

            if (split.Direction == SplitDirection.Horizontal)
            {
                var availableHeight = Math.Max(0, bounds.Height - dividerThickness);
                firstWidth = bounds.Width;
                firstHeight = availableHeight * split.SplitRatio;
                secondWidth = bounds.Width;
                secondHeight = availableHeight - firstHeight;

                secondX = bounds.X;
                secondY = bounds.Y + firstHeight + dividerThickness;

                divider = new PaneRect(bounds.X, bounds.Y + firstHeight, bounds.Width, dividerThickness);
            }
            else
            {
                var availableWidth = Math.Max(0, bounds.Width - dividerThickness);
                firstWidth = availableWidth * split.SplitRatio;
                firstHeight = bounds.Height;
                secondWidth = availableWidth - firstWidth;
                secondHeight = bounds.Height;

                secondX = bounds.X + firstWidth + dividerThickness;
                secondY = bounds.Y;

                divider = new PaneRect(bounds.X + firstWidth, bounds.Y, dividerThickness, bounds.Height);
            }

            split.DividerBounds = divider;

            LayoutNode(split.First, new PaneRect(bounds.X, bounds.Y, firstWidth, firstHeight), cellWidth, cellHeight, dividerThickness);
            LayoutNode(split.Second, new PaneRect(secondX, secondY, secondWidth, secondHeight), cellWidth, cellHeight, dividerThickness);
        }
    }

    public LeafPane? FindPaneAt(float x, float y)
    {
        return FindPaneAtNode(_root, x, y);
    }

    private static LeafPane? FindPaneAtNode(PaneNode node, float x, float y)
    {
        if (node is LeafPane leaf)
        {
            return leaf.Bounds.Contains(x, y) ? leaf : null;
        }

        if (node is SplitPaneNode split)
        {
            return FindPaneAtNode(split.First, x, y) ?? FindPaneAtNode(split.Second, x, y);
        }

        return null;
    }

    public SplitPaneNode? HitTestDivider(float x, float y, float hitTolerance = 4f)
    {
        return HitTestDividerNode(_root, x, y, hitTolerance);
    }

    private static SplitPaneNode? HitTestDividerNode(PaneNode node, float x, float y, float hitTolerance)
    {
        if (node is SplitPaneNode split)
        {
            var div = split.DividerBounds;
            var expanded = new PaneRect(
                div.X - hitTolerance,
                div.Y - hitTolerance,
                div.Width + (hitTolerance * 2),
                div.Height + (hitTolerance * 2)
            );

            if (expanded.ContainsInclusive(x, y))
            {
                return split;
            }

            return HitTestDividerNode(split.First, x, y, hitTolerance) ??
                   HitTestDividerNode(split.Second, x, y, hitTolerance);
        }

        return null;
    }

    public LeafPane? NavigateFocus(LeafPane current, PaneDirection direction)
    {
        ArgumentNullException.ThrowIfNull(current);
        var leaves = Leaves;
        if (leaves.Count <= 1) return null;

        var currentBounds = current.Bounds;
        var currentCenterX = currentBounds.X + (currentBounds.Width * 0.5f);
        var currentCenterY = currentBounds.Y + (currentBounds.Height * 0.5f);

        LeafPane? bestPane = null;
        var bestDistance = float.MaxValue;

        foreach (var leaf in leaves)
        {
            if (ReferenceEquals(leaf, current)) continue;

            var targetBounds = leaf.Bounds;
            var targetCenterX = targetBounds.X + (targetBounds.Width * 0.5f);
            var targetCenterY = targetBounds.Y + (targetBounds.Height * 0.5f);

            bool isInDirection = direction switch
            {
                PaneDirection.Left => targetCenterX < currentCenterX,
                PaneDirection.Right => targetCenterX > currentCenterX,
                PaneDirection.Up => targetCenterY < currentCenterY,
                PaneDirection.Down => targetCenterY > currentCenterY,
                _ => false
            };

            if (!isInDirection) continue;

            var dx = targetCenterX - currentCenterX;
            var dy = targetCenterY - currentCenterY;
            var distance = (dx * dx) + (dy * dy);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPane = leaf;
            }
        }

        return bestPane;
    }

    private bool ContainsLeaf(LeafPane target)
    {
        return ContainsLeafNode(_root, target);
    }

    private static bool ContainsLeafNode(PaneNode node, LeafPane target)
    {
        if (ReferenceEquals(node, target)) return true;
        if (node is SplitPaneNode split)
        {
            return ContainsLeafNode(split.First, target) || ContainsLeafNode(split.Second, target);
        }
        return false;
    }

    private static void CollectLeaves(PaneNode node, List<LeafPane> leaves)
    {
        if (node is LeafPane leaf)
        {
            leaves.Add(leaf);
        }
        else if (node is SplitPaneNode split)
        {
            CollectLeaves(split.First, leaves);
            CollectLeaves(split.Second, leaves);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var leaf in Leaves)
        {
            leaf.Dispose();
        }
    }
}
