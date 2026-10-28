using System;
using Dotty.Terminal.Graphics;

namespace Dotty.Rendering.Gpu;

/// <summary>Draw order relative to the cell pipeline.</summary>
public enum ImageLayer : byte
{
    /// <summary>z &lt; -2^30: below cell backgrounds.</summary>
    BelowBackground = 0,
    /// <summary>-2^30 ≤ z &lt; 0: above backgrounds, below text.</summary>
    BelowText = 1,
    /// <summary>z ≥ 0: above text.</summary>
    AboveText = 2,
}

/// <summary>A run of consecutive quads sharing one texture. <see cref="Pixels"/> carries one lease released by <see cref="ImageDrawList.ReleaseLeases"/>.</summary>
public readonly record struct ImageDraw(ImagePixels Pixels, int FirstQuad, int QuadCount, ImageLayer Layer);

/// <summary>
/// Reusable GPU draw list. Draws are grouped by layer: layer <c>i</c> owns
/// <c>Draws[LayerDrawStart[i] .. LayerDrawStart[i+1])</c>; <c>LayerDrawStart[3] == DrawCount</c>.
/// </summary>
public sealed class ImageDrawList
{
    public ImageQuadInstance[] Quads = new ImageQuadInstance[16];
    public ImageDraw[] Draws = new ImageDraw[8];
    public int QuadCount;
    public int DrawCount;
    public int[] LayerDrawStart = new int[4];
    public long NextDeadlineTicks;

    internal ImageQuadBuilder.SortEntry[] Scratch = new ImageQuadBuilder.SortEntry[16];

    /// <summary>Releases every draw's pixel lease and empties the quad/draw lists. Idempotent.</summary>
    public void ReleaseLeases()
    {
        for (int i = 0; i < DrawCount; i++)
        {
            Draws[i].Pixels.Release();
            Draws[i] = default;
        }
        DrawCount = 0;
        QuadCount = 0;
        Array.Clear(LayerDrawStart);
    }

    /// <summary>Releases leases and resets all state for the next frame.</summary>
    public void Clear()
    {
        ReleaseLeases();
        NextDeadlineTicks = 0;
    }
}
