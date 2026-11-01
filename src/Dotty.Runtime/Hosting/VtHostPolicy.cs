using System;
using Dotty.Runtime.Tabs;

namespace Dotty.Runtime.Hosting;

/// <summary>Window state as far as the CSI 8 t gate cares; mapped from the windowing toolkit by the host.</summary>
public enum VtWindowState
{
    Normal,
    Minimized,
    Maximized,
    Fullscreen,
}

/// <summary>
/// Host-side decisions for VT window features. Pure so they can be tested without a window:
/// the desktop host feeds in facts (tab/pane counts, window state, pixel metrics) and applies
/// the answers.
/// </summary>
public static class VtHostPolicy
{
    /// <summary>
    /// A session is potentially visible when its window is not minimized and its tab is the
    /// active one. Every pane of the active tab is visible.
    /// </summary>
    public static bool IsSessionVisible(bool windowIconified, bool sessionInActiveTab) =>
        !windowIconified && sessionInActiveTab;

    /// <summary>
    /// Pushes visibility to the adapter of every pane of every tab (edge-triggered by the adapter,
    /// so calling this repeatedly is cheap and emits nothing without a change).
    /// </summary>
    public static void ApplyVisibility(TerminalTabManager? tabs, bool windowIconified)
    {
        if (tabs is null)
            return;

        var active = tabs.ActiveTab;
        var all = tabs.Tabs;
        for (int tabIndex = 0; tabIndex < all.Count; tabIndex++)
        {
            var tab = all[tabIndex];
            bool visible = IsSessionVisible(windowIconified, ReferenceEquals(tab, active));
            var leaves = tab.PaneTree.Leaves;
            for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
                leaves[leafIndex].Session.Adapter.SetVisibility(visible);
        }
    }

    /// <summary>
    /// Decides whether a queued CSI 8 t request is honoured and resolves the target grid.
    /// Ignored when the gate is off, with several tabs, with split panes, or when the window
    /// is not in the normal state (a maximized, fullscreen or minimized window must not be
    /// resized by an application), and when nothing would change. A requested 0 keeps the
    /// current dimension.
    /// </summary>
    public static bool TryComputeResize(
        bool allowed,
        int tabCount,
        int activeTabLeafCount,
        VtWindowState windowState,
        int requestedRows,
        int requestedCols,
        int currentRows,
        int currentCols,
        out int newRows,
        out int newCols)
    {
        newRows = currentRows;
        newCols = currentCols;
        if (!allowed || tabCount != 1 || activeTabLeafCount != 1 || windowState != VtWindowState.Normal)
            return false;
        if (requestedRows <= 0 && requestedCols <= 0)
            return false;

        newRows = requestedRows > 0 ? requestedRows : currentRows;
        newCols = requestedCols > 0 ? requestedCols : currentCols;
        return newRows != currentRows || newCols != currentCols;
    }
}

/// <summary>
/// Thread-safe, allocation-free mailbox for the latest window-resize request. Parser threads
/// post; the host thread takes. Successive requests merge the way sequential application
/// would: a non-zero dimension overrides, 0 keeps the pending (or current) one.
/// </summary>
public sealed class VtResizeRequestQueue
{
    private readonly object _gate = new();
    private int _rows;
    private int _cols;
    private bool _pending;

    /// <summary>Records a request; both-zero requests are ignored.</summary>
    public void Post(int rows, int cols)
    {
        if (rows <= 0 && cols <= 0)
            return;

        lock (_gate)
        {
            if (rows > 0)
                _rows = rows;
            if (cols > 0)
                _cols = cols;
            _pending = true;
        }
    }

    /// <summary>Removes and returns the pending request, if any (0 = keep that dimension).</summary>
    public bool TryTake(out int rows, out int cols)
    {
        lock (_gate)
        {
            rows = _rows;
            cols = _cols;
            if (!_pending)
                return false;
            _rows = 0;
            _cols = 0;
            _pending = false;
            return true;
        }
    }

    /// <summary>Drops any pending request.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rows = 0;
            _cols = 0;
            _pending = false;
        }
    }
}
