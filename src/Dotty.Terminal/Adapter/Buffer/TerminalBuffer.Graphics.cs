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
    /// call) scrolls with the text and the scroll hooks see it. Clears the wrap-pending state and the row's
    /// continuation flag like any explicit cursor move.
    /// </summary>
    internal void MoveCursorAfterImage(int effCols, int effRows)
    {
        var advance = KittyCursorPolicy.Advance(_cursor.Row, _cursor.Col, Columns, Rows, _scrollBottom, effCols, effRows);
        if (advance.ScrollRows > 0)
            ScrollRegionUp(_scrollTop, _scrollBottom, advance.ScrollRows);
        _cursor.Set(advance.Row, advance.Col, Rows, Columns);
        ActiveBuffer.ClearRowContinuation(_cursor.Row);
        BumpGraphicsGeneration();
    }

    /// <summary>
    /// Attaches the active screen's visible placements to <paramref name="snapshot"/>. Free when the store does not
    /// exist or has no placements (a null check and a count test).
    /// </summary>
    private void CaptureGraphics(RenderSnapshot snapshot, int scrollOffset)
    {
        var store = _screens.ActiveImages;
        if (store is not { HasPlacements: true })
            return;
        store.CaptureVisible(snapshot.BeginGraphics(), Rows, scrollOffset);
    }
}
