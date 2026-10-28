using System;

namespace Dotty.Terminal.Adapter;

/// <summary>Why scrollback rows were discarded or renumbered; carried by <see cref="TerminalBuffer.HistoryInvalidated"/>.</summary>
internal enum HistoryDiscardReason : byte
{
    /// <summary>CSI 3 J (clear scrollback) or ED 2 (erase display) on the main screen.</summary>
    Cleared,

    /// <summary>RIS (<see cref="TerminalBuffer.FullReset"/>) or <see cref="TerminalBuffer.ClearScreen"/>.</summary>
    Reset,

    /// <summary>A resize reflow dropped the oldest rows (history no longer fits the capacity).</summary>
    CapacityShrunk,

    /// <summary>A resize reflow kept every row but re-based the absolute row numbers.</summary>
    Rebased,
}

public partial class TerminalBuffer
{
    /// <summary>
    /// Oldest absolute row (the <c>_totalScrolled</c>-based numbering used by prompt marks) that the active screen
    /// still retains; everything below it was overwritten as the ring wrapped. Polled by consumers (for example
    /// after each parser feed slice) instead of raising a callback per recycled line. It is "retained from
    /// here", not monotone: reverse scrolling pulls rows back and lowers it, and a reflow re-bases it (see
    /// <see cref="HistoryDiscardReason.Rebased"/>). With no scrollback capacity it equals the scrolled-line count.
    /// While the alternate screen is active it describes the alternate screen.
    /// </summary>
    internal long HistoryFloorAbsoluteRow => Math.Max(0L, (long)_totalScrolled - MaxScrollback);

    /// <summary>
    /// Raised, under <see cref="SyncRoot"/>, exactly once per operation that discards or renumbers history rows
    /// other than by the ring wrapping: CSI 3 J and ED 2 on the main screen, RIS and
    /// <see cref="ClearScreen"/>, and every resize (<see cref="HistoryDiscardReason.CapacityShrunk"/> when rows
    /// were dropped, otherwise <see cref="HistoryDiscardReason.Rebased"/>). The buffer is consistent when it
    /// fires. Entering or leaving the alternate screen, and ED 2 / CSI 3 J while it is active, do not raise it:
    /// they leave the main screen's history alone.
    /// </summary>
    internal event Action<HistoryDiscardReason>? HistoryInvalidated;

    private void RaiseHistoryInvalidated(HistoryDiscardReason reason) => HistoryInvalidated?.Invoke(reason);

    /// <summary>The main/alternate/spare screens, for tests that inspect lazy-commit state.</summary>
    internal ScreenManager ScreenManagerForTests => _screens;
}
