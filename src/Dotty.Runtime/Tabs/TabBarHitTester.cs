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
}

/// <summary>
/// Convenience hit-type enum if integer/switch style is preferred.
/// </summary>
public enum TabBarHitType
{
    None = 0,
    SelectTab = 1,
    CloseTab = 2,
    NewTab = 3
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
        float barHeight = TabBarLayout.DefaultBarHeight)
    {
        if (y < 0 || y > barHeight || windowWidth <= 0 || tabCount < 0)
        {
            return new TabBarHitResult.None();
        }

        var layout = TabBarLayout.Calculate(windowWidth, tabCount, activeIndex, barHeight);

        // Check new tab (+) button
        if (layout.NewTabButtonBounds.Contains(x, y))
        {
            return new TabBarHitResult.NewTab();
        }

        // Check each tab and its close button
        for (int i = 0; i < layout.Tabs.Length; i++)
        {
            ref readonly var tab = ref layout.Tabs[i];
            if (!tab.TabBounds.Contains(x, y)) continue;

            // Check if clicking inside close button
            if (tab.CloseButtonBounds.Contains(x, y))
            {
                return new TabBarHitResult.CloseTab(i);
            }

            return new TabBarHitResult.SelectTab(i);
        }

        return new TabBarHitResult.None();
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
        float barHeight = TabBarLayout.DefaultBarHeight)
    {
        tabIndex = -1;

        if (y < 0 || y > barHeight || windowWidth <= 0 || tabCount < 0)
        {
            return TabBarHitType.None;
        }

        var layout = TabBarLayout.Calculate(windowWidth, tabCount, activeIndex, barHeight);

        if (layout.NewTabButtonBounds.Contains(x, y))
        {
            return TabBarHitType.NewTab;
        }

        for (int i = 0; i < layout.Tabs.Length; i++)
        {
            ref readonly var tab = ref layout.Tabs[i];
            if (!tab.TabBounds.Contains(x, y)) continue;

            tabIndex = i;
            if (tab.CloseButtonBounds.Contains(x, y))
            {
                return TabBarHitType.CloseTab;
            }

            return TabBarHitType.SelectTab;
        }

        return TabBarHitType.None;
    }
}
