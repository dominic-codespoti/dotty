using System.Threading;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

public sealed partial class GlyphAtlas
{
    private GraphemeShaper? _graphemeShaper;

    /// <summary>
    /// Shapes a multi-scalar grapheme (ZWJ, flag, keycap, skin tone, combining marks) with the fallback chain's
    /// <c>font.features</c>. Null for single scalars and when the font lacks a glyph, so the caller keeps its
    /// per-scalar draw. The cell span is unaffected: it comes from the width tables.
    /// </summary>
    private SKTextBlob? TryShapeGrapheme(string grapheme, SKFont font)
    {
        if (!GraphemeShaper.HasMultipleScalars(grapheme)) return null;
        var features = Volatile.Read(ref _fallbackChain)?.Features ?? FontOptions.Default.Features;
        var shaper = Volatile.Read(ref _graphemeShaper);
        if (shaper == null || !ReferenceEquals(shaper.Features, features))
        {
            var created = new GraphemeShaper(features);
            var previous = Interlocked.CompareExchange(ref _graphemeShaper, created, shaper);
            if (ReferenceEquals(previous, shaper))
            {
                previous?.Dispose();
                shaper = created;
            }
            else
            {
                created.Dispose();
                shaper = previous!;
            }
        }
        try
        {
            return shaper.CreateBlob(grapheme, font);
        }
        catch (System.ObjectDisposedException)
        {
            return null; // replaced concurrently; the per-scalar draw is still correct
        }
    }

    private void DisposeGraphemeShaper() => Interlocked.Exchange(ref _graphemeShaper, null)?.Dispose();
}
