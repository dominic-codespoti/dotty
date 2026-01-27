using System;

namespace Dotty.App.Rendering;

public sealed class TerminalAppearanceSettings
{
    public float HorizontalPadding { get; }
    public float VerticalPaddingFactor { get; }
    public float MaxVerticalPadding { get; }
    public float RadiusFactor { get; }
    public float RadiusMinimum { get; }

    public TerminalAppearanceSettings(
        float horizontalPadding = 4f,
        float verticalPaddingFactor = 0.08f,
        float maxVerticalPadding = 2f,
        float radiusFactor = 0.25f,
        float radiusMinimum = 1f)
    {
        HorizontalPadding = Math.Max(0f, horizontalPadding);
        VerticalPaddingFactor = Math.Max(0f, verticalPaddingFactor);
        MaxVerticalPadding = Math.Max(0f, maxVerticalPadding);
        RadiusFactor = Math.Max(0f, radiusFactor);
        RadiusMinimum = Math.Max(0f, radiusMinimum);
    }

    public float GetVerticalPadding(float rowHeight)
    {
        return Math.Min(rowHeight * VerticalPaddingFactor, MaxVerticalPadding);
    }

    public float GetRadius(float rowHeight, float verticalPaddingPerSide)
    {
        var availableHeight = Math.Max(0f, rowHeight - verticalPaddingPerSide * 2f);
        return Math.Max(RadiusMinimum, Math.Min(availableHeight * 0.5f, rowHeight * RadiusFactor));
    }
}
