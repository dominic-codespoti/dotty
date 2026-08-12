using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Rotation math for the buffer's scroll-epoch array. A future incremental
/// renderer mirrors this array and applies the identical transformation so
/// moved rows keep `bufferEpoch == mirrorEpoch` and only exposed rows differ.
///
/// Delta sign convention: positive = content moved DOWN, negative = content
/// moved UP (same convention as the pixel memmove). The region is
/// [top..bottom] inclusive in logical (0-based buffer) row coordinates.
/// </summary>
public static class ScrollEpochMath
{
    /// <summary>
    /// Rotates the epochs of rows [top..bottom] by `delta`, mirroring how the
    /// Screen ring moves content between logical rows. Rows that scroll out of
    /// the region at one edge are replaced by rows entering from the other
    /// edge; their epochs are carried with the content. The exposed (blanked)
    /// rows at the trailing edge are NOT touched here — callers bump them.
    /// </summary>
    public static void RotateRange(ulong[] epochs, int top, int bottom, int delta)
    {
        if (epochs == null || epochs.Length == 0) return;
        if (top < 0) top = 0;
        if (bottom >= epochs.Length) bottom = epochs.Length - 1;
        if (top > bottom || delta == 0) return;

        int height = bottom - top + 1;
        int absDelta = Math.Abs(delta);
        if (absDelta >= height) return; // whole region replaced; caller bumps everything

        if (delta > 0)
        {
            // Content moved down: row r receives content (and epoch) from r - delta.
            Array.Copy(epochs, top, epochs, top + delta, height - delta);
        }
        else
        {
            // Content moved up: row r receives content (and epoch) from r + |delta|.
            Array.Copy(epochs, top + absDelta, epochs, top, height - absDelta);
        }
    }
}
