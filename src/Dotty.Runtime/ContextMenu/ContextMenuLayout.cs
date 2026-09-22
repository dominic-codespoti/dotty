using System;
using System.Collections.Generic;

using static Dotty.Runtime.Rendering.ChromeStyleUtils;
namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// Simple floating-point rectangle for 2D layout and hit testing in context menus.
/// </summary>
public readonly record struct MenuRect(float X, float Y, float Width, float Height)
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
/// Pre-calculated layout metrics for an individual context menu item.
/// </summary>
public readonly record struct MenuItemLayout(
    int Index,
    MenuRect Bounds,
    MenuRect IconBounds,
    MenuRect LabelBounds,
    MenuRect ShortcutBounds,
    bool IsSeparator,
    bool IsDisabled);

/// <summary>
/// Calculated layout metrics for the entire floating context menu popup.
/// </summary>
public sealed class ContextMenuLayout
{
    public const float DefaultMinWidth = 180f;
    public const float DefaultItemHeight = 30f;
    public const float DefaultSeparatorHeight = 10f;
    public const float DefaultPaddingX = 8f;
    public const float DefaultPaddingY = 8f;
    public const float DefaultIconWidth = 22f;
    public const float DefaultShortcutGap = 20f;
    public const float DefaultShadowOffset = 4f;
    public const float DefaultTrailingTextInset = 8f;

    /// <summary>Total bounding box of the menu popup background (including padding).</summary>
    public MenuRect Bounds { get; }

    /// <summary>Bounding box including subtle shadow tones.</summary>
    public MenuRect ShadowBounds { get; }

    /// <summary>Array of calculated layouts for each item.</summary>
    public MenuItemLayout[] Items { get; }

    /// <summary>Popup origin X coordinate in window space.</summary>
    public float X => Bounds.X;

    /// <summary>Popup origin Y coordinate in window space.</summary>
    public float Y => Bounds.Y;

    /// <summary>Total width of the popup menu.</summary>
    public float Width => Bounds.Width;

    /// <summary>Total height of the popup menu.</summary>
    public float Height => Bounds.Height;

    public ContextMenuLayout(MenuRect bounds, MenuRect shadowBounds, MenuItemLayout[] items)
    {
        Bounds = bounds;
        ShadowBounds = shadowBounds;
        Items = items ?? Array.Empty<MenuItemLayout>();
    }

    /// <summary>
    /// Computes the context menu layout, measuring items and clamping the bounds to stay entirely within the viewport.
    /// </summary>
    /// <param name="model">The menu state model.</param>
    /// <param name="viewportWidth">Viewport width in pixels.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    /// <param name="charWidth">Approximate character width in pixels for measuring text.</param>
    /// <param name="itemHeight">Height per item row.</param>
    /// <param name="separatorHeight">Height per separator row.</param>
    /// <param name="paddingX">Horizontal inner padding.</param>
    /// <param name="paddingY">Vertical inner padding.</param>
    /// <returns>Computed <see cref="ContextMenuLayout"/>.</returns>
    public static ContextMenuLayout Calculate(
        ContextMenuModel model,
        float viewportWidth,
        float viewportHeight,
        float charWidth = 8f,
        float itemHeight = DefaultItemHeight,
        float separatorHeight = DefaultSeparatorHeight,
        float paddingX = DefaultPaddingX,
        float paddingY = DefaultPaddingY)
    {
        ArgumentNullException.ThrowIfNull(model);

        var items = model.Items;
        if (items == null || items.Count == 0)
        {
            var emptyRect = new MenuRect(model.X, model.Y, 0f, 0f);
            return new ContextMenuLayout(emptyRect, emptyRect, Array.Empty<MenuItemLayout>());
        }

        var metrics = ResolveMetrics(itemHeight);
        float scale = metrics.Scale;
        float scaledPaddingX = paddingX * scale;
        float scaledPaddingY = paddingY * scale;
        float scaledItemHeight = Math.Max(itemHeight, DefaultItemHeight * scale);
        float scaledSeparatorHeight = Math.Max(separatorHeight, DefaultSeparatorHeight * scale);
        float shortcutGap = DefaultShortcutGap;
        float iconWidth = DefaultIconWidth * scale;
        float trailingTextInset = Math.Max(charWidth, DefaultTrailingTextInset * scale);

        float maxLabelWidth = 0f;
        float maxShortcutWidth = 0f;
        bool hasAnyIcon = false;
        float totalContentHeight = 0f;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.IsSeparator)
            {
                totalContentHeight += scaledSeparatorHeight;
                continue;
            }

            totalContentHeight += scaledItemHeight;
            hasAnyIcon |= !string.IsNullOrEmpty(item.Icon);
            maxLabelWidth = Math.Max(maxLabelWidth, (item.Label?.Length ?? 0) * charWidth);
            maxShortcutWidth = Math.Max(maxShortcutWidth, (item.Shortcut?.Length ?? 0) * charWidth);
        }

        float iconAreaWidth = hasAnyIcon ? iconWidth : 0f;
        float shortcutAreaWidth = maxShortcutWidth > 0f
            ? maxShortcutWidth + shortcutGap + trailingTextInset
            : trailingTextInset;
        float innerWidth = iconAreaWidth + maxLabelWidth + shortcutAreaWidth;
        float menuWidth = Math.Max(DefaultMinWidth * scale, innerWidth + (scaledPaddingX * 2f));
        float menuHeight = totalContentHeight + (scaledPaddingY * 2f);

        float originX = model.X;
        float originY = model.Y;
        if (viewportWidth > 0f && originX + menuWidth > viewportWidth)
            originX = Math.Max(0f, viewportWidth - menuWidth);
        if (viewportHeight > 0f && originY + menuHeight > viewportHeight)
            originY = Math.Max(0f, viewportHeight - menuHeight);
        originX = Math.Max(0f, originX);
        originY = Math.Max(0f, originY);

        var menuBounds = new MenuRect(originX, originY, menuWidth, menuHeight);
        var shadowBounds = new MenuRect(
            originX - metrics.Hairline,
            originY - metrics.Hairline,
            menuWidth + DefaultShadowOffset * scale,
            menuHeight + DefaultShadowOffset * scale);

        var itemLayouts = new MenuItemLayout[items.Count];
        float currentY = originY + scaledPaddingY;
        float itemContentWidth = menuWidth - (scaledPaddingX * 2f);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            float rowHeight = item.IsSeparator ? scaledSeparatorHeight : scaledItemHeight;
            var itemBounds = new MenuRect(originX + scaledPaddingX, currentY, itemContentWidth, rowHeight);

            if (item.IsSeparator)
            {
                itemLayouts[i] = new MenuItemLayout(
                    i, itemBounds, default, default, default, IsSeparator: true, IsDisabled: true);
            }
            else
            {
                float cursorX = itemBounds.Left;
                MenuRect iconRect = default;
                if (hasAnyIcon)
                {
                    iconRect = new MenuRect(cursorX, currentY, iconWidth, rowHeight);
                    cursorX += iconWidth;
                }

                float shortcutW = !string.IsNullOrEmpty(item.Shortcut) ? item.Shortcut.Length * charWidth : 0f;
                float shortcutColumnRight = itemBounds.Right - trailingTextInset;
                float shortcutColumnLeft = shortcutColumnRight - maxShortcutWidth;
                MenuRect shortcutRect = shortcutW > 0f
                    ? new MenuRect(shortcutColumnRight - shortcutW, currentY, shortcutW, rowHeight)
                    : default;
                float labelW = maxShortcutWidth > 0f
                    ? Math.Max(0f, shortcutColumnLeft - cursorX - shortcutGap)
                    : Math.Max(0f, shortcutColumnRight - cursorX);

                itemLayouts[i] = new MenuItemLayout(
                    i,
                    itemBounds,
                    iconRect,
                    new MenuRect(cursorX, currentY, labelW, rowHeight),
                    shortcutRect,
                    IsSeparator: false,
                    IsDisabled: item.IsDisabled);
            }

            currentY += rowHeight;
        }

        return new ContextMenuLayout(menuBounds, shadowBounds, itemLayouts);
    }
}
