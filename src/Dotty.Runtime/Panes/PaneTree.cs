using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Dotty.Abstractions.Pty;
using Dotty.Runtime.Sessions;

namespace Dotty.Runtime.Panes;

public sealed class PaneTree : IDisposable
{
    private PaneNode _root;
    private LeafPane _activePane;
    private List<LeafPane>? _leaves;
    private ReadOnlyCollection<LeafPane>? _leavesView;
    private readonly Dictionary<LeafPane, Action<int>> _exitHandlers = new();
    private readonly object _exitLock = new();
    private readonly Func<IPty>? _ptyFactory;
    private bool _isDisposed;

    public event Action<LeafPane, LeafPane>? ActivePaneChanged;
    public event Action? TopologyChanged;
    public event Action<LeafPane, int>? ProcessExited;

    // The only topology mutations in this class are Split's root/child
    // replacement and Close's root/child replacement. Both call
    // InvalidateLeaves before returning; constructors establish the initial
    // root and Layout never replaces a node.
    private void InvalidateLeaves()
    {
        _leaves = null;
        _leavesView = null;
    }

    public PaneNode Root
    {
        get
        {
            ThrowIfDisposed();
            return _root;
        }
    }

    public LeafPane ActivePane
    {
        get
        {
            ThrowIfDisposed();
            return _activePane;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            if (!ContainsLeaf(value))
                throw new InvalidOperationException("Active pane must belong to the pane tree.");
            if (ReferenceEquals(_activePane, value)) return;

            var previous = _activePane;
            _activePane = value;
            ActivePaneChanged?.Invoke(previous, value);
        }
    }

    public IReadOnlyList<LeafPane> Leaves
    {
        get
        {
            ThrowIfDisposed();
            if (_leavesView != null) return _leavesView;
            var leaves = _leaves ?? new List<LeafPane>();
            CollectLeaves(_root, leaves);
            _leaves = leaves;
            _leavesView = new ReadOnlyCollection<LeafPane>(leaves);
            return _leavesView;
        }
    }

    public PaneTree(LeafPane initialPane)
    {
        _activePane = initialPane ?? throw new ArgumentNullException(nameof(initialPane));
        _root = initialPane;
        Subscribe(initialPane);
    }

    public PaneTree(string? workingDirectory = null, string? shell = null, int rows = 24, int columns = 80)
        : this(ptyFactory: null, workingDirectory, shell, rows, columns)
    {
    }

    internal PaneTree(Func<IPty>? ptyFactory, string? workingDirectory = null, string? shell = null, int rows = 24, int columns = 80)
    {
        _ptyFactory = ptyFactory;
        var session = CreateSession(rows, columns);
        var initialPane = new LeafPane(session);
        _activePane = initialPane;
        _root = initialPane;
        Subscribe(initialPane);
        if (!string.IsNullOrEmpty(workingDirectory) || !string.IsNullOrEmpty(shell))
        {
            session.StartWithOptions(shell: shell, workingDirectory: workingDirectory);
        }
    }

    private TerminalSession CreateSession(int rows, int columns) =>
        _ptyFactory is null
            ? new TerminalSession(rows: rows, columns: columns)
            : new TerminalSession(rows, columns, _ptyFactory);

    public LeafPane Split(LeafPane target, SplitDirection direction, string? workingDirectory = null, string? shell = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(target);
        if (!ContainsLeaf(target))
            throw new InvalidOperationException("Target pane does not belong to this pane tree.");

        string? effectiveWorkingDirectory = workingDirectory ?? target.Session.CurrentWorkingDirectory;
        string? effectiveShell = shell ?? target.Session.LaunchShell;
        bool effectiveShellIsExecutable = shell is null && target.Session.LaunchShellIsExecutable;
        var session = CreateSession(Math.Max(1, target.Rows), Math.Max(1, target.Columns));
        var newPane = new LeafPane(session);
        Subscribe(newPane);
        if (!string.IsNullOrEmpty(effectiveWorkingDirectory) || !string.IsNullOrEmpty(effectiveShell))
        {
            session.StartWithOptions(
                shell: effectiveShell,
                workingDirectory: effectiveWorkingDirectory,
                shellIsExecutable: effectiveShellIsExecutable);
        }
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

        InvalidateLeaves();
        var previous = _activePane;
        _activePane = newPane;
        TopologyChanged?.Invoke();
        ActivePaneChanged?.Invoke(previous, newPane);
        return newPane;
    }

    public bool Close(LeafPane target)
    {
        ThrowIfDisposed();
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
        Unsubscribe(target);
        InvalidateLeaves();

        var wasActive = ReferenceEquals(_activePane, target);
        LeafPane? nextActive = null;
        if (wasActive)
        {
            nextActive = sibling as LeafPane;
            if (nextActive == null)
            {
                var remaining = new List<LeafPane>();
                CollectLeaves(_root, remaining);
                nextActive = remaining[0];
            }
            _activePane = nextActive;
        }

        target.Dispose();
        TopologyChanged?.Invoke();
        if (wasActive)
            ActivePaneChanged?.Invoke(target, nextActive!);
        return true;
    }

    public void Layout(float totalWidth, float totalHeight, float cellWidth, float cellHeight, float dividerThickness = 2f)
    {
        ThrowIfDisposed();
        if (totalWidth <= 0 || totalHeight <= 0) return;
        LayoutNode(_root, new PaneRect(0, 0, totalWidth, totalHeight), cellWidth, cellHeight, dividerThickness);
    }

    private static void LayoutNode(PaneNode node, PaneRect bounds, float cellWidth, float cellHeight, float dividerThickness)
    {
        switch (node)
        {
            case LeafPane leaf:
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
                break;
            case SplitPaneNode split:
                LayoutSplit(split, bounds, cellWidth, cellHeight, dividerThickness);
                break;
        }
    }

    private static void LayoutSplit(SplitPaneNode split, PaneRect bounds, float cellWidth, float cellHeight, float dividerThickness)
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

    public LeafPane? FindPaneAt(float x, float y)
    {
        ThrowIfDisposed();
        return FindPaneAtNode(_root, x, y);
    }

    private static LeafPane? FindPaneAtNode(PaneNode node, float x, float y)
    {
        return node switch
        {
            LeafPane leaf => leaf.Bounds.Contains(x, y) ? leaf : null,
            SplitPaneNode split => FindPaneAtNode(split.First, x, y) ?? FindPaneAtNode(split.Second, x, y),
        };
    }

    public SplitPaneNode? HitTestDivider(float x, float y, float hitTolerance = 4f)
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        return node switch
        {
            LeafPane leaf => ReferenceEquals(leaf, target),
            SplitPaneNode split => ContainsLeafNode(split.First, target) || ContainsLeafNode(split.Second, target),
        };
    }

    private static void CollectLeaves(PaneNode node, List<LeafPane> leaves)
    {
        switch (node)
        {
            case LeafPane leaf:
                leaves.Add(leaf);
                break;
            case SplitPaneNode split:
                CollectLeaves(split.First, leaves);
                CollectLeaves(split.Second, leaves);
                break;
        }
    }

    private void Subscribe(LeafPane leaf)
    {
        Action<int> handler = code =>
        {
            lock (_exitLock)
            {
                if (_isDisposed || !_exitHandlers.ContainsKey(leaf)) return;
            }

            try { ProcessExited?.Invoke(leaf, code); } catch { }
        };
        lock (_exitLock)
        {
            if (_isDisposed || _exitHandlers.ContainsKey(leaf)) return;
            _exitHandlers.Add(leaf, handler);
            leaf.Session.ProcessExited += handler;
        }
    }

    private void Unsubscribe(LeafPane leaf)
    {
        lock (_exitLock)
        {
            if (!_exitHandlers.Remove(leaf, out var handler)) return;
            try { leaf.Session.ProcessExited -= handler; } catch { }
        }
    }

    public void Dispose()
    {
        lock (_exitLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            foreach (var entry in _exitHandlers)
            {
                try { entry.Key.Session.ProcessExited -= entry.Value; } catch { }
            }
            _exitHandlers.Clear();
        }

        var leaves = new List<LeafPane>();
        CollectLeaves(_root, leaves);
        foreach (var leaf in leaves)
            leaf.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(PaneTree));
    }
}
