using System;
using System.Collections.Generic;
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
    public const float PaddingTop = 2f;
    public const float PaddingBottom = 2f;
    public const float NewTabButtonWidth = 28f;
    public const float CloseButtonWidth = 20f;
    public const float CloseButtonHeight = 20f;
    public const float CloseButtonPaddingRight = 4f;
    public const float TextPaddingLeft = 8f;
    private const float HardMinTabWidth = 56f;
    private const float NewTabGap = 4f;
    private const float RightPadding = 6f;

    private readonly record struct LayoutKey(
        float WindowWidth,
        int TabCount,
        int ActiveIndex,
        float BarHeight);

    // Calculate is a static entry point used by both rendering and hit testing.
    // Keep one result per geometry key so callers with different frame sizes do
    // not overwrite one another's arrays. The lock also makes interleaved
    // hit-test/render calls safe; cached values are rewritten on every hit so
    // callers cannot permanently corrupt a result by mutating its public array.
    private static readonly object CacheLock = new();
    private static readonly Dictionary<LayoutKey, TabBarLayoutResult> Cache = new();

    /// <summary>
    /// The character-grid renderer only knows whole text rows, so the tab bar
    /// occupies an integer number of rows. Given the user-configured bar
    /// height (logical px) and the current cell height (logical px), returns
    /// the row count whose total height is closest to the configured height,
    /// so the bar doesn't balloon to a chunky multi-row block.
    /// </summary>
    public static int ComputeBarRows(double configuredHeight, float cellHeight)
    {
        if (cellHeight <= 0f) return 1;
        return Math.Max(1, (int)Math.Round(configuredHeight / cellHeight, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Computes the layout of all tabs and buttons in the tab bar.
    /// </summary>
    public static TabBarLayoutResult Calculate(
        float windowWidth,
        int tabCount,
        int activeIndex,
        float barHeight = DefaultBarHeight)
    {
        var key = new LayoutKey(windowWidth, tabCount, activeIndex, barHeight);
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out var result))
            {
                if (windowWidth <= 0f || tabCount <= 0)
                {
                    var emptyBarBounds = new TabRect(0f, 0f, Math.Max(0f, windowWidth), barHeight);
                    var emptyNewTabBounds = ClampNewTabBounds(windowWidth, barHeight, PaddingLeft);
                    result = new TabBarLayoutResult(
                        emptyBarBounds,
                        Array.Empty<TabLayoutItem>(),
                        emptyNewTabBounds);
                }
                else
                {
                    var tabs = new TabLayoutItem[tabCount];
                    var newTabBounds = PopulateTabs(
                        tabs,
                        windowWidth,
                        tabCount,
                        activeIndex,
                        barHeight);
                    result = new TabBarLayoutResult(
                        new TabRect(0f, 0f, windowWidth, barHeight),
                        tabs,
                        newTabBounds);
                }

                Cache.Add(key, result);
            }
            else if (result.Tabs.Length > 0)
            {
                // The public result exposes the array for compatibility. Fill
                // it again so an external mutation cannot poison this key.
                PopulateTabs(result.Tabs, windowWidth, tabCount, activeIndex, barHeight);
            }

            return result;
        }
    }

    private static TabRect PopulateTabs(
        TabLayoutItem[] tabs,
        float windowWidth,
        int tabCount,
        int activeIndex,
        float barHeight)
    {
        float tabHeight = Math.Max(0f, barHeight - PaddingTop - PaddingBottom);
        float tabAreaWidth = Math.Max(
            0f,
            windowWidth - PaddingLeft - NewTabButtonWidth - NewTabGap - RightPadding);
        int maxVisibleTabs = Math.Max(
            1,
            (int)MathF.Floor((tabAreaWidth + TabSpacing) / (HardMinTabWidth + TabSpacing)));
        int visibleCount = Math.Min(tabCount, maxVisibleTabs);
        int firstVisible = Math.Clamp(
            activeIndex - visibleCount / 2,
            0,
            Math.Max(0, tabCount - visibleCount));
        int lastVisibleExclusive = firstVisible + visibleCount;
        float tabWidth = visibleCount > 0
            ? Math.Min(
                MaxTabWidth,
                Math.Max(0f, (tabAreaWidth - (visibleCount - 1) * TabSpacing) / visibleCount))
            : 0f;

        float currentX = PaddingLeft;
        var hidden = new TabRect(-1f, -1f, 0f, 0f);
        for (int i = 0; i < tabCount; i++)
        {
            if (i < firstVisible || i >= lastVisibleExclusive)
            {
                tabs[i] = new TabLayoutItem(
                    Index: i,
                    TabBounds: hidden,
                    CloseButtonBounds: hidden,
                    TextBounds: hidden,
                    IsActive: i == activeIndex);
                continue;
            }

            var tabRect = new TabRect(currentX, PaddingTop, tabWidth, tabHeight);
            float closeX = tabRect.Right - CloseButtonWidth - CloseButtonPaddingRight;
            float closeY = tabRect.Top + (tabHeight - CloseButtonHeight) * 0.5f;
            var closeRect = new TabRect(closeX, closeY, CloseButtonWidth, CloseButtonHeight);
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

        return ClampNewTabBounds(
            windowWidth,
            barHeight,
            Math.Max(PaddingLeft, windowWidth - RightPadding - NewTabButtonWidth));
    }

    private static TabRect ClampNewTabBounds(float windowWidth, float barHeight, float preferredX)
    {
        float height = Math.Max(0f, barHeight - PaddingTop - PaddingBottom);
        float width = Math.Max(0f, windowWidth);
        if (width <= 0f)
            return new TabRect(0f, PaddingTop, 0f, height);

        float x = Math.Clamp(preferredX, 0f, Math.Max(0f, width - NewTabButtonWidth));
        float buttonWidth = Math.Min(NewTabButtonWidth, width - x);
        return new TabRect(x, PaddingTop, buttonWidth, height);
    }
}
