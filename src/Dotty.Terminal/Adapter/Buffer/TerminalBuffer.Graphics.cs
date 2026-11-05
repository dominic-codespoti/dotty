using System;
using Dotty.Terminal.Graphics;

namespace Dotty.Terminal.Adapter;

// Kitty graphics: per-screen image stores, the cursor move after a placement, and the render-snapshot capture.
// All members except the capture run on the PTY consumer thread under SyncRoot; the capture runs under it too.
public partial class TerminalBuffer
{
    /// <summary>The active screen's image store (created on first use); main and alternate screens each have their own.</summary>
    internal KittyImageStore ActiveImageStore => _screens.EnsureActiveImages();

    /// <summary>The main screen's image store; null until first used.</summary>
    internal KittyImageStore? MainImageStore => _screens.MainImages;

    /// <summary>The alternate screen's image store; null until first used.</summary>
    internal KittyImageStore? AlternateImageStore => _screens.AlternateImages;

    internal bool ApplyImageLimits(long storageLimitBytes, int maxPlacementsPerScreen) =>
        _screens.ApplyImageLimits(storageLimitBytes, maxPlacementsPerScreen);

    internal bool ClearImageStores() => _screens.ClearImageStores();

    internal void DisposeImageStores() => _screens.DisposeImageStores();

    /// <summary>
    /// Marks the visible graphics as changed: bumps <see cref="Generation"/> so dirty detection redraws even
    /// when no cell changed.
    /// </summary>
    internal void BumpGraphicsGeneration()
    {
        unchecked { _globalGeneration++; }
    }

    /// <summary>
    /// Moves the cursor past a just-placed image (<see cref="KittyCursorPolicy.Advance"/>). A move past the bottom
    /// margin scrolls the scroll region through the normal scroll path, so the placement (registered before this
    /// call) scrolls with the text and the scroll hooks see it. The whole overshoot is applied: a full-screen
    /// region scrolls in steps of at most <see cref="Rows"/> (one ring rotation is capped at the screen height), so
    /// the last row of an image taller than the screen ends on the bottom row; a margin region is emptied by its own
    /// height and scrolling it further changes nothing. Clears the wrap-pending state like any explicit cursor move; as
    /// with every cursor move, the soft-wrap (continuation) flags of the rows are left alone.
    /// </summary>
    internal void MoveCursorAfterImage(int effCols, int effRows)
    {
        var advance = KittyCursorPolicy.Advance(_cursor.Row, _cursor.Col, Columns, Rows, _scrollBottom, effCols, effRows);
        int remaining = advance.ScrollRows;
        if (remaining > 0)
        {
            bool fullScreen = _scrollTop == 0 && _scrollBottom == Rows - 1;
            int step = Math.Max(fullScreen ? Rows : _scrollBottom - _scrollTop + 1, 1);
            if (!fullScreen)
                remaining = Math.Min(remaining, step);
            while (remaining > 0)
            {
                int n = Math.Min(remaining, step);
                ScrollRegionUp(_scrollTop, _scrollBottom, n);
                remaining -= n;
            }
        }
        _cursor.Set(advance.Row, advance.Col, Rows, Columns);
        BumpGraphicsGeneration();
    }

    /// <summary>
    /// Clock behind animation playback: the capture advances animations by its timestamps and reports the next
    /// deadline in them. The owning <c>TerminalAdapter</c> sets its own provider here so the capture and
    /// <c>IKittyGraphicsHost.NowTicks</c> read one clock; defaults to <see cref="TimeProvider.System"/>.
    /// </summary>
    internal TimeProvider GraphicsClock
    {
        get => _graphicsClock;
        set => _graphicsClock = value ?? throw new ArgumentNullException(nameof(value));
    }

    private TimeProvider _graphicsClock = TimeProvider.System;

    /// <summary>
    /// Attaches the active screen's visible placements to <paramref name="snapshot"/>. Free when the store does not
    /// exist or has no placements (a null check and a count test).
    /// </summary>
    private void CaptureGraphics(RenderSnapshot snapshot, int scrollOffset)
    {
        var store = _screens.ActiveImages;
        if (store is not { HasPlacements: true })
            return;
        // Playing animations of the visible placements step first, so the capture sees the frame that is due now;
        // the earliest next change travels with the snapshot as the host's wake-up deadline (same clock as
        // IKittyGraphicsHost.NowTicks: the timestamps of GraphicsClock).
        var clock = _graphicsClock;
        if (store.TicksPerSecond != clock.TimestampFrequency)
            store.TicksPerSecond = clock.TimestampFrequency;
        // The captured cells tell which virtual placements are on screen (placeholder cells name them).
        long deadline = store.AdvanceAnimations(clock.GetTimestamp(), Rows, scrollOffset, new RenderSnapshotPlaceholderSource(snapshot));
        var graphics = snapshot.BeginGraphics();
        store.CaptureVisible(graphics, Rows, scrollOffset);
        graphics.NextAnimationDeadlineTicks = deadline;
    }

    /// <summary>
    /// Deletes the active screen's placements that a history discard took with it: with <paramref name="wholeRing"/>
    /// (ED 2, whose <c>Screen.Clear</c> wipes the history and the visible rows alike) everything, otherwise (CSI 3 J)
    /// the placements wholly above the first visible row. Runs for the alternate screen too: it has a ring of its
    /// own, and the main screen's store is untouched while it is active.
    /// </summary>
    private void DiscardGraphicsHistory(bool wholeRing)
    {
        if (_screens.ActiveImages is not { HasPlacements: true } images)
            return;
        long floor = wholeRing ? long.MaxValue : images.ViewTop;
        if (images.OnHistoryInvalidated(HistoryDiscardReason.Cleared, floor) > 0)
            BumpGraphicsGeneration();
    }

    /// <summary>Deletes every placement of both screens (the images stay): <see cref="HistoryDiscardReason.Reset"/>.</summary>
    private void DiscardAllGraphicsPlacements()
    {
        int deleted = 0;
        if (_screens.MainImages is { HasPlacements: true } main)
            deleted += main.OnHistoryInvalidated(HistoryDiscardReason.Reset, 0);
        if (_screens.AlternateImages is { HasPlacements: true } alternate)
            deleted += alternate.OnHistoryInvalidated(HistoryDiscardReason.Reset, 0);
        if (deleted > 0)
            BumpGraphicsGeneration();
    }

    /// <summary>
    /// The host's cell size changed: re-resolves the auto-sized placements of both screens (see
    /// <see cref="KittyImageStore.OnCellPixelSizeChanged"/>). Takes <see cref="SyncRoot"/>.
    /// </summary>
    internal void OnCellPixelSizeChanged(int cellPixelWidth, int cellPixelHeight)
    {
        lock (SyncRoot)
        {
            int changed = 0;
            if (_screens.MainImages is { HasPlacements: true } main)
                changed += main.OnCellPixelSizeChanged(cellPixelWidth, cellPixelHeight);
            if (_screens.AlternateImages is { HasPlacements: true } alternate)
                changed += alternate.OnCellPixelSizeChanged(cellPixelWidth, cellPixelHeight);
            if (changed > 0)
                BumpGraphicsGeneration();
        }
    }
}
