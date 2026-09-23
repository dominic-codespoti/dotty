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
    public TabRect BarBounds { get; private set; }
    public ArraySegment<TabLayoutItem> Tabs { get; private set; }
    public int TabCount { get; private set; }
    public TabRect NewTabButtonBounds { get; private set; }
    public TabRect StatusBounds { get; private set; }

    public TabBarLayoutResult(TabRect barBounds, TabLayoutItem[] tabs, TabRect newTabButtonBounds, TabRect statusBounds)
    {
        var items = tabs ?? Array.Empty<TabLayoutItem>();
        BarBounds = barBounds;
        Tabs = new ArraySegment<TabLayoutItem>(items);
        TabCount = items.Length;
        NewTabButtonBounds = newTabButtonBounds;
        StatusBounds = statusBounds;
    }

    internal void Update(TabRect barBounds, TabLayoutItem[] tabs, int tabCount, TabRect newTabButtonBounds, TabRect statusBounds)
    {
        BarBounds = barBounds;
        Tabs = new ArraySegment<TabLayoutItem>(tabs, 0, tabCount);
        TabCount = tabCount;
        NewTabButtonBounds = newTabButtonBounds;
        StatusBounds = statusBounds;
    }
    public ReadOnlySpan<TabLayoutItem> AsSpan() => new(Tabs.Array!, Tabs.Offset, Tabs.Count);
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
    public const float PaddingBottom = 4f;
    public const float NewTabButtonWidth = 28f;
    public const float CloseButtonWidth = 20f;
    public const float CloseButtonHeight = 20f;
    public const float CloseButtonPaddingRight = 4f;
    public const float TextPaddingLeft = 8f;
    private const float HardMinTabWidth = 56f;
    private const float NewTabGap = 4f;
    private const float RightPadding = 6f;

    // Calls are consumed synchronously by the renderer and hit tester. Keep a
    // reusable result per thread so concurrent UI/render threads cannot mutate
    // each other's layouts.
    [ThreadStatic] private static TabLayoutItem[]? _tabScratch;
    [ThreadStatic] private static TabBarLayoutResult? _result;

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
    /// The result and its tab storage are reused by later calls on the same
    /// thread; consume the result before calling this method again.
    /// </summary>
    public static TabBarLayoutResult Calculate(
        float windowWidth,
        int tabCount,
        int activeIndex,
        float barHeight = DefaultBarHeight,
        float statusWidth = 0f)
    {
        statusWidth = Math.Clamp(statusWidth, 0f, Math.Max(0f, windowWidth));

        int visibleItemCount = windowWidth > 0f ? Math.Max(0, tabCount) : 0;
        TabLayoutItem[] tabs = _tabScratch ?? Array.Empty<TabLayoutItem>();
        if (tabs.Length < visibleItemCount)
        {
            int capacity = Math.Max(visibleItemCount, tabs.Length == 0 ? 8 : tabs.Length * 2);
            Array.Resize(ref tabs, capacity);
            _tabScratch = tabs;
        }

        TabBarLayoutResult result = _result ??= new TabBarLayoutResult(default, tabs, default, default);
        TabRect barBounds;
        TabRect newTabBounds;
        TabRect statusBounds = GetStatusBounds(windowWidth, barHeight, statusWidth);
        if (visibleItemCount == 0)
        {
            barBounds = new TabRect(0f, 0f, Math.Max(0f, windowWidth), barHeight);
            newTabBounds = ClampNewTabBounds(windowWidth, barHeight, PaddingLeft, statusWidth);
        }
        else
        {
            newTabBounds = PopulateTabs(
                tabs,
                windowWidth,
                visibleItemCount,
                activeIndex,
                barHeight,
                statusWidth);
            barBounds = new TabRect(0f, 0f, windowWidth, barHeight);
        }

        result.Update(barBounds, tabs, visibleItemCount, newTabBounds, statusBounds);
        return result;
    }


    private static TabRect PopulateTabs(
        TabLayoutItem[] tabs,
        float windowWidth,
        int tabCount,
        int activeIndex,
        float barHeight,
        float statusWidth)
    {
        float tabHeight = Math.Max(0f, barHeight - PaddingTop - PaddingBottom);
        float tabAreaWidth = Math.Max(
            0f,
            windowWidth - PaddingLeft - NewTabButtonWidth - NewTabGap - RightPadding - statusWidth - (statusWidth > 0f ? NewTabGap : 0f));
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
            Math.Max(PaddingLeft, windowWidth - RightPadding - NewTabButtonWidth - statusWidth - (statusWidth > 0f ? NewTabGap : 0f)),
            statusWidth);
    }

    private static TabRect ClampNewTabBounds(float windowWidth, float barHeight, float preferredX, float statusWidth)
    {
        float height = Math.Max(0f, barHeight - PaddingTop - PaddingBottom);
        float width = Math.Max(0f, windowWidth);
        if (width <= 0f)
            return new TabRect(0f, PaddingTop, 0f, height);

        float maxRight = Math.Max(0f, width - RightPadding - statusWidth - (statusWidth > 0f ? NewTabGap : 0f));
        float x = Math.Clamp(preferredX, 0f, Math.Max(0f, maxRight - NewTabButtonWidth));
        float buttonWidth = Math.Min(NewTabButtonWidth, Math.Max(0f, maxRight - x));
        return new TabRect(x, PaddingTop, buttonWidth, height);
    }

    private static TabRect GetStatusBounds(float windowWidth, float barHeight, float statusWidth)
    {
        float height = Math.Max(0f, barHeight - PaddingTop - PaddingBottom);
        if (statusWidth <= 0f)
            return new TabRect(windowWidth, PaddingTop, 0f, height);
        float width = Math.Min(statusWidth, Math.Max(0f, windowWidth));
        return new TabRect(Math.Max(0f, windowWidth - RightPadding - width), PaddingTop, width, height);
    }
}
