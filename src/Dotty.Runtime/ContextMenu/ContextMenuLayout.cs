using System;
using System.Collections.Generic;

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

        // 1. Measure required width & height
        float maxLabelWidth = 0f;
        float maxShortcutWidth = 0f;
        bool hasAnyIcon = false;
        float totalContentHeight = 0f;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.IsSeparator)
            {
                totalContentHeight += separatorHeight;
                continue;
            }

            totalContentHeight += itemHeight;

            if (!string.IsNullOrEmpty(item.Icon))
            {
                hasAnyIcon = true;
            }

            if (!string.IsNullOrEmpty(item.Label))
            {
                float lw = item.Label.Length * charWidth;
                if (lw > maxLabelWidth) maxLabelWidth = lw;
            }

            if (!string.IsNullOrEmpty(item.Shortcut))
            {
                float sw = item.Shortcut.Length * charWidth;
                if (sw > maxShortcutWidth) maxShortcutWidth = sw;
            }
        }

        float iconAreaWidth = hasAnyIcon ? DefaultIconWidth : 0f;
        float shortcutAreaWidth = maxShortcutWidth > 0f ? (maxShortcutWidth + DefaultShortcutGap) : 0f;
        float innerWidth = iconAreaWidth + maxLabelWidth + shortcutAreaWidth;
        float menuWidth = Math.Max(DefaultMinWidth, innerWidth + (paddingX * 2f));
        float menuHeight = totalContentHeight + (paddingY * 2f);

        // 2. Position & clamp to viewport
        float originX = model.X;
        float originY = model.Y;

        if (viewportWidth > 0f)
        {
            if (originX + menuWidth > viewportWidth)
            {
                originX = Math.Max(0f, viewportWidth - menuWidth);
            }
        }

        if (viewportHeight > 0f)
        {
            if (originY + menuHeight > viewportHeight)
            {
                originY = Math.Max(0f, viewportHeight - menuHeight);
            }
        }

        originX = Math.Max(0f, originX);
        originY = Math.Max(0f, originY);

        var menuBounds = new MenuRect(originX, originY, menuWidth, menuHeight);
        var shadowBounds = new MenuRect(
            originX - 1f,
            originY - 1f,
            menuWidth + DefaultShadowOffset,
            menuHeight + DefaultShadowOffset);

        // 3. Compute item bounds
        var itemLayouts = new MenuItemLayout[items.Count];
        float currentY = originY + paddingY;
        float itemContentWidth = menuWidth - (paddingX * 2f);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            float rowHeight = item.IsSeparator ? separatorHeight : itemHeight;
            var itemBounds = new MenuRect(originX + paddingX, currentY, itemContentWidth, rowHeight);

            if (item.IsSeparator)
            {
                itemLayouts[i] = new MenuItemLayout(
                    i,
                    itemBounds,
                    new MenuRect(0, 0, 0, 0),
                    new MenuRect(0, 0, 0, 0),
                    new MenuRect(0, 0, 0, 0),
                    IsSeparator: true,
                    IsDisabled: true);
            }
            else
            {
                float cursorX = itemBounds.Left;

                MenuRect iconRect = default;
                if (hasAnyIcon)
                {
                    iconRect = new MenuRect(cursorX, currentY, DefaultIconWidth, rowHeight);
                    cursorX += DefaultIconWidth;
                }

                float shortcutW = !string.IsNullOrEmpty(item.Shortcut) ? item.Shortcut.Length * charWidth : 0f;
                float shortcutColumnLeft = itemBounds.Right - maxShortcutWidth;
                MenuRect shortcutRect = default;
                if (shortcutW > 0f)
                {
                    // Keep every shortcut on the same right edge while the
                    // label column reserves room for the longest shortcut.
                    shortcutRect = new MenuRect(
                        itemBounds.Right - shortcutW,
                        currentY,
                        shortcutW,
                        rowHeight);
                }

                float labelW = maxShortcutWidth > 0f
                    ? Math.Max(0f, shortcutColumnLeft - cursorX - DefaultShortcutGap)
                    : Math.Max(0f, itemBounds.Right - cursorX);

                var labelRect = new MenuRect(cursorX, currentY, labelW, rowHeight);

                itemLayouts[i] = new MenuItemLayout(
                    i,
                    itemBounds,
                    iconRect,
                    labelRect,
                    shortcutRect,
                    IsSeparator: false,
                    IsDisabled: item.IsDisabled);
            }

            currentY += rowHeight;
        }

        return new ContextMenuLayout(menuBounds, shadowBounds, itemLayouts);
    }
}
