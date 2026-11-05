namespace Dotty.Terminal.Graphics;

/// <summary>
/// One animation frame of a <see cref="KittyImage"/>: a full-size canvas and the time it stays on screen.
/// </summary>
/// <remarks>
/// Frames are eager full canvases (see <see cref="KittyAnimation"/>). A canvas is never modified once it is stored:
/// an edit builds a new <see cref="ImagePixels"/> and swaps it in, so a renderer that retained the old buffer (and
/// the GPU texture keyed by its serial) is unaffected and the changed frame gets a new texture key.
/// </remarks>
internal sealed class KittyFrame
{
    internal KittyFrame(ImagePixels canvas, int gapMs)
    {
        Canvas = canvas;
        GapMs = gapMs;
    }

    /// <summary>Pixels of this frame. Frame 1 (the root) keeps the transmitted format; every other canvas is premultiplied RGBA.</summary>
    public ImagePixels Canvas { get; internal set; }

    /// <summary>Milliseconds the frame is shown before the next one; 0 = gapless (skipped by playback).</summary>
    public int GapMs { get; internal set; }
}
