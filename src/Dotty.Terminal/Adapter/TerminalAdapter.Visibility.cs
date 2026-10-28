using System;
using System.Threading;

namespace Dotty.Terminal.Adapter;

// Visibility reports (mode 2033) and the one-shot CSI ? 998 n query.
//
// Reply form: CSI ? 999 ; Ps n with Ps 1 = potentially visible, 2 = not visible.
// The flag, the host-reported visibility and the emission decision are guarded
// by one lock so an edge from the host thread cannot interleave with the parser
// thread's enable/query and produce a lost or duplicate report. Emission only
// enqueues through ReplyRequested (the session's own input-queue lock), and the
// lock order is always visibility lock -> input-queue lock, so cross-thread
// emission cannot deadlock.
public partial class TerminalAdapter
{
    private readonly object _visibilitySync = new();
    private bool _visibilityReportsEnabled;
    private bool _isVisible = true;

    /// <summary>Mode 2033: unsolicited visibility reports are enabled.</summary>
    public bool VisibilityReportsEnabled => Volatile.Read(ref _visibilityReportsEnabled);

    /// <summary>
    /// Host-reported visibility of this terminal (default true: unknown counts as visible).
    /// Not terminal state: neither DECSTR nor RIS resets it.
    /// </summary>
    public bool IsVisible => Volatile.Read(ref _isVisible);

    /// <summary>
    /// Host fact: the terminal became visible or hidden (window minimized, tab switched away).
    /// Callable from any thread; edge-triggered. Emits a report only when the value changed
    /// while mode 2033 is enabled.
    /// </summary>
    public void SetVisibility(bool visible)
    {
        lock (_visibilitySync)
        {
            if (_isVisible == visible)
                return;
            Volatile.Write(ref _isVisible, visible);
            if (_visibilityReportsEnabled)
                EmitVisibilityReportLocked();
        }
    }

    /// <summary>
    /// CSI ? 2033 h / l. Every enable (even when already enabled) answers with the current
    /// state immediately; disabling never reports.
    /// </summary>
    public void OnSetVisibilityReports(bool enabled)
    {
        lock (_visibilitySync)
        {
            Volatile.Write(ref _visibilityReportsEnabled, enabled);
            if (enabled)
                EmitVisibilityReportLocked();
        }
    }

    /// <summary>
    /// CSI ? 998 n: one-shot query answered regardless of mode 2033, without changing the mode.
    /// Invoked from the private-DSR dispatcher when the code is 998.
    /// </summary>
    public void HandleVisibilityQuery()
    {
        lock (_visibilitySync)
            EmitVisibilityReportLocked();
    }

    /// <summary>Disables mode 2033 (RIS). Does not touch <see cref="IsVisible"/>.</summary>
    internal void ResetVisibilityReports()
    {
        lock (_visibilitySync)
            Volatile.Write(ref _visibilityReportsEnabled, false);
    }

    // Caller holds _visibilitySync.
    private void EmitVisibilityReportLocked()
    {
        var handler = ReplyRequested;
        if (handler is null)
            return;
        handler(_isVisible ? "\x1b[?999;1n".AsSpan() : "\x1b[?999;2n".AsSpan());
    }
}
