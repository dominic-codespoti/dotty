using System;

namespace Dotty.Runtime.Tabs;

/// <summary>
/// Result of a hit-test operation on the tab bar.
/// </summary>
public abstract record TabBarHitResult
{
    public sealed record None : TabBarHitResult;
    public sealed record SelectTab(int Index) : TabBarHitResult;
    public sealed record CloseTab(int Index) : TabBarHitResult;
    public sealed record NewTab : TabBarHitResult;
    public sealed record Minimize : TabBarHitResult;
    public sealed record Maximize : TabBarHitResult;
    public sealed record Close : TabBarHitResult;
    public sealed record Caption : TabBarHitResult;
}

/// <summary>
/// Convenience hit-type enum if integer/switch style is preferred.
/// </summary>
public enum TabBarHitType
{
    None = 0,
    SelectTab = 1,
    CloseTab = 2,
    NewTab = 3,
    Minimize = 4,
    Maximize = 5,
    Close = 6,
    Caption = 7
}

/// <summary>
/// Hit-testing helper for tab bar mouse interactions.
/// </summary>
public static class TabBarHitTester
{
    /// <summary>
    /// Hit tests a point (x, y) against the tab bar layout.
    /// </summary>
    public static TabBarHitResult HitTest(
        float x,
        float y,
        float windowWidth,
        int tabCount,
        int activeIndex,
        float barHeight = TabBarLayout.DefaultBarHeight,
        float statusWidth = 0f,
        float captionButtonsWidth = 0f)
    {
        if (x < 0 || x >= windowWidth || y < 0 || y > barHeight || windowWidth <= 0 || tabCount < 0)
        {
            return new TabBarHitResult.None();
        }

        var layout = TabBarLayout.Calculate(
            windowWidth, tabCount, activeIndex, barHeight, statusWidth, captionButtonsWidth);

        if (layout.MinimizeButtonBounds.Width > 0f && layout.MinimizeButtonBounds.Contains(x, y))
            return new TabBarHitResult.Minimize();
        if (layout.MaximizeButtonBounds.Width > 0f && layout.MaximizeButtonBounds.Contains(x, y))
            return new TabBarHitResult.Maximize();
        if (layout.CloseButtonBounds.Width > 0f && layout.CloseButtonBounds.Contains(x, y))
            return new TabBarHitResult.Close();

        // Check new tab (+) button
        if (layout.NewTabButtonBounds.Contains(x, y))
        {
            return new TabBarHitResult.NewTab();
        }

        // Check each tab and its close button
        ReadOnlySpan<TabLayoutItem> tabs = layout.AsSpan();
        for (int i = 0; i < tabs.Length; i++)
        {
            ref readonly var tab = ref tabs[i];
            if (!tab.TabBounds.Contains(x, y)) continue;

            // Check if clicking inside close button
            if (tab.CloseButtonBounds.Contains(x, y))
            {
                return new TabBarHitResult.CloseTab(i);
            }

            return new TabBarHitResult.SelectTab(i);
        }

        if (layout.StatusBounds.Contains(x, y))
            return new TabBarHitResult.None();

        return captionButtonsWidth > 0f
            ? new TabBarHitResult.Caption()
            : new TabBarHitResult.None();
    }

    /// <summary>
    /// Value-type variant of hit testing that returns <see cref="TabBarHitType"/> and the associated tab index.
    /// </summary>
    public static TabBarHitType HitTest(
        float x,
        float y,
        float windowWidth,
        int tabCount,
        int activeIndex,
        out int tabIndex,
        float barHeight = TabBarLayout.DefaultBarHeight,
        float statusWidth = 0f,
        float captionButtonsWidth = 0f)
    {
        tabIndex = -1;

        if (x < 0 || x >= windowWidth || y < 0 || y > barHeight || windowWidth <= 0 || tabCount < 0)
        {
            return TabBarHitType.None;
        }

        var layout = TabBarLayout.Calculate(
            windowWidth, tabCount, activeIndex, barHeight, statusWidth, captionButtonsWidth);

        if (layout.MinimizeButtonBounds.Width > 0f && layout.MinimizeButtonBounds.Contains(x, y))
            return TabBarHitType.Minimize;
        if (layout.MaximizeButtonBounds.Width > 0f && layout.MaximizeButtonBounds.Contains(x, y))
            return TabBarHitType.Maximize;
        if (layout.CloseButtonBounds.Width > 0f && layout.CloseButtonBounds.Contains(x, y))
            return TabBarHitType.Close;

        if (layout.NewTabButtonBounds.Contains(x, y))
        {
            return TabBarHitType.NewTab;
        }

        ReadOnlySpan<TabLayoutItem> tabs = layout.AsSpan();
        for (int i = 0; i < tabs.Length; i++)
        {
            ref readonly var tab = ref tabs[i];
            if (!tab.TabBounds.Contains(x, y)) continue;

            tabIndex = i;
            if (tab.CloseButtonBounds.Contains(x, y))
            {
                return TabBarHitType.CloseTab;
            }

            return TabBarHitType.SelectTab;
        }

        if (layout.StatusBounds.Contains(x, y))
            return TabBarHitType.None;

        return captionButtonsWidth > 0f
            ? TabBarHitType.Caption
            : TabBarHitType.None;
    }
}
