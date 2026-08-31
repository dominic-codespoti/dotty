using System;

namespace Dotty.Runtime.Tabs;

/// <summary>
/// Simple floating-point rectangle for 2D layout and hit testing.
/// </summary>
public readonly record struct TabRect(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(float px, float py)
    {
        return px >= Left && px <= Right && py >= Top && py <= Bottom;
    }
}

/// <summary>
/// Pre-calculated layout metrics for an individual tab item.
/// </summary>
public readonly record struct TabLayoutItem(
    int Index,
    TabRect TabBounds,
    TabRect CloseButtonBounds,
    TabRect TextBounds,
    bool IsActive);

/// <summary>
/// Layout calculation results for a terminal tab bar.
/// </summary>
public sealed class TabBarLayoutResult
{
    public TabRect BarBounds { get; }
    public TabLayoutItem[] Tabs { get; }
    public TabRect NewTabButtonBounds { get; }

    public TabBarLayoutResult(TabRect barBounds, TabLayoutItem[] tabs, TabRect newTabButtonBounds)
    {
        BarBounds = barBounds;
        Tabs = tabs ?? Array.Empty<TabLayoutItem>();
        NewTabButtonBounds = newTabButtonBounds;
    }
}

/// <summary>
/// Calculates tab pill widths, coordinates, close button bounds, and new tab button bounds.
/// </summary>
public static class TabBarLayout
{
    public const float DefaultBarHeight = 32f;
    public const float MinTabWidth = 80f;
    public const float MaxTabWidth = 240f;
    public const float TabSpacing = 2f;
    public const float PaddingLeft = 6f;
    public const float PaddingTop = 4f;
    public const float PaddingBottom = 4f;
    public const float NewTabButtonWidth = 28f;
    public const float CloseButtonWidth = 20f;
    public const float CloseButtonHeight = 20f;
    public const float CloseButtonPaddingRight = 4f;
    public const float TextPaddingLeft = 8f;

    /// <summary>
    /// Computes the layout of all tabs and buttons in the tab bar.
    /// </summary>
    public static TabBarLayoutResult Calculate(
        float windowWidth,
        int tabCount,
        int activeIndex,
        float barHeight = DefaultBarHeight)
    {
        if (windowWidth <= 0f || tabCount <= 0)
        {
            var emptyBarBounds = new TabRect(0f, 0f, Math.Max(0f, windowWidth), barHeight);
            var emptyNewTabBounds = new TabRect(
                PaddingLeft,
                PaddingTop,
                NewTabButtonWidth,
                Math.Max(0f, barHeight - PaddingTop - PaddingBottom));
            return new TabBarLayoutResult(emptyBarBounds, Array.Empty<TabLayoutItem>(), emptyNewTabBounds);
        }

        var barBounds = new TabRect(0f, 0f, windowWidth, barHeight);
        float tabHeight = Math.Max(0f, barHeight - PaddingTop - PaddingBottom);

        // Calculate available width for tabs (reserve space for padding, spacing, and the + new tab button)
        float availableWidth = windowWidth - PaddingLeft - NewTabButtonWidth - (tabCount * TabSpacing) - 8f;
        float tabWidth = availableWidth / tabCount;
        tabWidth = Math.Clamp(tabWidth, MinTabWidth, MaxTabWidth);

        // If tabs overflow the window width, shrink them proportionally down to a hard minimum
        if (tabWidth * tabCount > availableWidth && tabCount > 0)
        {
            float hardMin = 40f;
            tabWidth = Math.Max(hardMin, availableWidth / tabCount);
        }

        var tabs = new TabLayoutItem[tabCount];
        float currentX = PaddingLeft;

        for (int i = 0; i < tabCount; i++)
        {
            var tabRect = new TabRect(currentX, PaddingTop, tabWidth, tabHeight);

            // Close button rect (positioned at the right edge of the tab pill)
            float closeX = tabRect.Right - CloseButtonWidth - CloseButtonPaddingRight;
            float closeY = tabRect.Top + (tabHeight - CloseButtonHeight) * 0.5f;
            var closeRect = new TabRect(closeX, closeY, CloseButtonWidth, CloseButtonHeight);

            // Text bounds (from left padding to the left of the close button)
            float textX = tabRect.Left + TextPaddingLeft;
            float textWidth = Math.Max(0f, closeX - textX - 2f);
            var textRect = new TabRect(textX, tabRect.Top, textWidth, tabHeight);

            tabs[i] = new TabLayoutItem(
                Index: i,
                TabBounds: tabRect,
                CloseButtonBounds: closeRect,
                TextBounds: textRect,
                IsActive: i == activeIndex);

            currentX += tabWidth + TabSpacing;
        }

        // New tab (+) button rect positioned right after the last tab
        var newTabRect = new TabRect(
            currentX + 2f,
            PaddingTop,
            NewTabButtonWidth,
            tabHeight);

        return new TabBarLayoutResult(barBounds, tabs, newTabRect);
    }
}
