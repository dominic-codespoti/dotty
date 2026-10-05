using System;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using static Dotty.Runtime.Rendering.ChromeStyleUtils;

namespace Dotty.Runtime.ContextMenu;

/// <summary>Framebuffer-pixel rectangle shared by popup painting and hit testing.</summary>
public readonly record struct MenuRect(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Contains(float px, float py) =>
        Width > 0 && Height > 0 && px >= Left && px < Right && py >= Top && py < Bottom;
    public SKRect ToSKRect() => new(Left, Top, Right, Bottom);
}

public readonly record struct MenuItemLayout(
    int Index, MenuRect Bounds, MenuRect IconBounds, MenuRect LabelBounds,
    MenuRect ShortcutBounds, bool IsSeparator, bool IsDisabled);

/// <summary>Measured popup columns and a scrollable, viewport-bounded content area.</summary>
public sealed class ContextMenuLayout
{
    public const float DefaultMinWidth = 180f;
    public const float DefaultItemHeight = 30f;
    public const float DefaultSeparatorHeight = 10f;
    public const float DefaultPaddingX = 8f;
    public const float DefaultPaddingY = 8f;
    public const float DefaultIconWidth = 24f;
    public const float DefaultShortcutGap = 20f;
    public const float DefaultShadowOffset = 4f;
    public const float DefaultTrailingTextInset = 8f;

    public MenuRect Bounds { get; }
    public MenuRect ShadowBounds { get; }
    public MenuRect ContentBounds { get; }
    public MenuItemLayout[] Items { get; }
    public float MaximumScrollOffset { get; }
    public float ItemHeight { get; }
    public float Scale { get; }
    public float X => Bounds.X;
    public float Y => Bounds.Y;
    public float Width => Bounds.Width;
    public float Height => Bounds.Height;

    private ContextMenuLayout(MenuRect bounds, MenuRect shadowBounds, MenuRect contentBounds,
        MenuItemLayout[] items, float maximumScrollOffset, float itemHeight, float scale)
    {
        Bounds = bounds;
        ShadowBounds = shadowBounds;
        ContentBounds = contentBounds;
        Items = items;
        MaximumScrollOffset = maximumScrollOffset;
        ItemHeight = itemHeight;
        Scale = scale;
    }

    public static ContextMenuLayout Calculate(ContextMenuModel model, float viewportWidth,
        float viewportHeight, SKTypeface? typeface = null, float fontSize = 14f, float scale = 1f)
    {
        ArgumentNullException.ThrowIfNull(model);
        typeface ??= SKTypeface.Default;
        float availableWidth = Math.Max(0f, viewportWidth);
        float availableHeight = Math.Max(0f, viewportHeight);
        if (model.Items.Count == 0 || availableWidth == 0 || availableHeight == 0)
            return new(default, default, default, Array.Empty<MenuItemLayout>(), 0f, 0f, scale);

        var fontMetrics = GetFontMetrics(typeface, fontSize);
        float itemHeight = Math.Max(DefaultItemHeight * scale,
            fontMetrics.Ascent + fontMetrics.Descent + 8f * scale);
        float separatorHeight = DefaultSeparatorHeight * scale;
        float paddingX = Math.Min(DefaultPaddingX * scale, availableWidth / 4f);
        float paddingY = Math.Min(DefaultPaddingY * scale, availableHeight / 4f);
        float labelWidth = 0f, shortcutWidth = 0f, contentHeight = 0f;
        bool hasIcons = false;
        var items = model.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            contentHeight += item.IsSeparator ? separatorHeight : itemHeight;
            if (item.IsSeparator) continue;
            hasIcons |= item.Icon != UiIcon.None;
            labelWidth = Math.Max(labelWidth, MeasureUiText(item.Label, typeface, fontSize));
            shortcutWidth = Math.Max(shortcutWidth, MeasureUiText(item.Shortcut, typeface, fontSize));
        }

        float iconWidth = hasIcons ? DefaultIconWidth * scale : 0f;
        float gap = shortcutWidth > 0f ? DefaultShortcutGap * scale : 0f;
        float trailingInset = DefaultTrailingTextInset * scale;
        float width = Math.Min(availableWidth, Math.Max(DefaultMinWidth * scale,
            paddingX * 2f + iconWidth + labelWidth + gap + shortcutWidth + trailingInset));
        float height = Math.Min(availableHeight, contentHeight + paddingY * 2f);
        bool scrollable = contentHeight + paddingY * 2f > height;
        if (scrollable)
            paddingY = Math.Min(16f * scale, height / 4f);
        float x = Math.Clamp(model.X, 0f, Math.Max(0f, availableWidth - width));
        float y = Math.Clamp(model.Y, 0f, Math.Max(0f, availableHeight - height));
        var bounds = new MenuRect(x, y, width, height);
        var content = new MenuRect(x + paddingX, y + paddingY,
            Math.Max(0f, width - paddingX * 2f), Math.Max(0f, height - paddingY * 2f));
        float maxScroll = Math.Max(0f, contentHeight - content.Height);
        float offset = Math.Clamp(model.ScrollOffset, 0f, maxScroll);
        if (model.RevealFocusedItem && model.HoveredIndex >= 0 && model.HoveredIndex < items.Count)
        {
            float focusedTop = 0f;
            for (int i = 0; i < model.HoveredIndex; i++)
                focusedTop += items[i].IsSeparator ? separatorHeight : itemHeight;
            float focusedHeight = items[model.HoveredIndex].IsSeparator ? separatorHeight : itemHeight;
            if (focusedTop < offset) offset = focusedTop;
            else if (focusedTop + focusedHeight > offset + content.Height)
                offset = focusedTop + focusedHeight - content.Height;
            offset = Math.Clamp(offset, 0f, maxScroll);
        }
        model.ScrollOffset = offset;
        model.RevealFocusedItem = false;

        // Retain labels first at very narrow sizes; otherwise share the two text columns.
        float textSpace = Math.Max(0f, content.Width - trailingInset);
        if (textSpace < iconWidth + 40f * scale) iconWidth = 0f;
        textSpace = Math.Max(0f, textSpace - iconWidth);
        float shortcutColumnWidth = textSpace + 0.01f >= labelWidth + gap + shortcutWidth
            ? shortcutWidth
            : textSpace >= 100f * scale ? Math.Min(shortcutWidth, textSpace * 0.48f) : 0f;
        if (shortcutColumnWidth == 0f) gap = 0f;
        else gap = Math.Min(gap, textSpace * 0.1f);
        float labelColumnWidth = Math.Max(0f, textSpace - shortcutColumnWidth - gap);
        var rows = new MenuItemLayout[items.Count];
        float currentY = content.Y - offset;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            float rowHeight = item.IsSeparator ? separatorHeight : itemHeight;
            var row = new MenuRect(content.X, currentY, content.Width, rowHeight);
            float shortcutW = Math.Min(shortcutColumnWidth, MeasureUiText(item.Shortcut, typeface, fontSize));
            float shortcutRight = content.Right - Math.Min(trailingInset, content.Width);
            rows[i] = new(i, row,
                new(content.X, currentY, iconWidth, rowHeight),
                new(content.X + iconWidth, currentY, labelColumnWidth, rowHeight),
                new(shortcutRight - shortcutW, currentY, shortcutW, rowHeight),
                item.IsSeparator, item.IsDisabled);
            currentY += rowHeight;
        }
        float shadow = DefaultShadowOffset * scale;
        var shadowBounds = new MenuRect(x, y,
            Math.Min(width + shadow, availableWidth - x), Math.Min(height + shadow, availableHeight - y));
        return new(bounds, shadowBounds, content, rows, maxScroll, itemHeight, scale);
    }
}
