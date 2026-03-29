using System;
using Avalonia.Threading;
using Dotty.App.Rendering;

namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// Helper class for scheduling lazy glyph preloading on the UI thread.
/// Eliminates code duplication between TerminalCanvas and TerminalGlCanvas.
/// </summary>
public static class GlyphPreloader
{
    /// <summary>
    /// Schedules lazy preloading of common glyphs on a background dispatcher priority.
    /// This defers the work to avoid blocking the UI thread during initial rendering.
    /// </summary>
    /// <param name="atlas">The glyph atlas to preload. If null, no action is taken.</param>
    /// <param name="preloadedFlag">Reference to the flag tracking preload state. Set to true immediately.</param>
    public static void ScheduleLazyPreload(GlyphAtlas? atlas, ref bool preloadedFlag)
    {
        if (preloadedFlag || atlas == null)
            return;

        preloadedFlag = true;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                atlas.PreloadCommonGlyphs();
            }
            catch
            {
                // Silently handle any preload errors to prevent UI disruption
            }
        }, DispatcherPriority.Background);
    }
}
