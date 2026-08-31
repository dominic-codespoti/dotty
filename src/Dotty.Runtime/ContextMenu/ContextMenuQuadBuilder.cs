using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using SkiaSharp;

namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// GPU quad instance builder for context menus.
/// Emits instanced cell quads for floating elevated dark background box, borders,
/// hover highlights, icons, labels, shortcuts, and separators.
/// </summary>
public static class ContextMenuQuadBuilder
{
    private static void ExtractRgb(uint argb, out byte r, out byte g, out byte b)
    {
        r = (byte)((argb >> 16) & 0xFF);
        g = (byte)((argb >> 8) & 0xFF);
        b = (byte)(argb & 0xFF);
    }

    private static uint Darken(uint color, float factor)
    {
        byte a = (byte)((color >> 24) & 0xFF);
        byte r = (byte)(((color >> 16) & 0xFF) * factor);
        byte g = (byte)(((color >> 8) & 0xFF) * factor);
        byte b = (byte)((color & 0xFF) * factor);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static uint Lighten(uint color, float factor)
    {
        byte a = (byte)((color >> 24) & 0xFF);
        byte r = (byte)Math.Min(255, ((color >> 16) & 0xFF) * factor);
        byte g = (byte)Math.Min(255, ((color >> 8) & 0xFF) * factor);
        byte b = (byte)Math.Min(255, (color & 0xFF) * factor);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>
    /// Builds cell instances for the context menu and writes them into <paramref name="destination"/>.
    /// </summary>
    public static int Build(
        ContextMenuModel model,
        ContextMenuLayout layout,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize,
        IColorScheme theme,
        float cellWidth,
        float cellHeight,
        Span<CellInstance> destination,
        float paddingLeft = 0f,
        float paddingTop = 0f)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);
        ArgumentNullException.ThrowIfNull(theme);

        if (!model.IsVisible || destination.IsEmpty || cellWidth <= 0 || cellHeight <= 0)
        {
            return 0;
        }

        int written = 0;

        // Colors
        uint themeBg = theme.Background != 0 ? theme.Background : 0xFF1E1E1E;
        uint themeFg = theme.Foreground != 0 ? theme.Foreground : 0xFFD4D4D4;

        // Context menu styling
        uint shadowColor = 0x80000000;
        uint menuBgColor = Darken(themeBg, 0.75f);
        uint borderColor = Darken(themeBg, 0.40f);
        uint hoverPillBg = Lighten(themeBg, 1.35f);
        uint separatorColor = Darken(themeBg, 0.50f);
        uint itemFgColor = themeFg;
        uint disabledFgColor = Darken(themeFg, 0.40f);
        uint shortcutFgColor = Darken(themeFg, 0.65f);

        // 1. Shadow tone quads
        ExtractRgb(shadowColor, out byte sR, out byte sG, out byte sB);
        int shadowStartCol = (int)Math.Floor((layout.ShadowBounds.Left - paddingLeft) / cellWidth);
        int shadowEndCol = (int)Math.Ceiling((layout.ShadowBounds.Right - paddingLeft) / cellWidth);
        int shadowStartRow = (int)Math.Floor((layout.ShadowBounds.Top - paddingTop) / cellHeight);
        int shadowEndRow = (int)Math.Ceiling((layout.ShadowBounds.Bottom - paddingTop) / cellHeight);

        for (int r = shadowStartRow; r < shadowEndRow; r++)
        {
            for (int c = shadowStartCol; c < shadowEndCol; c++)
            {
                if (written >= destination.Length) break;
                destination[written++] = new CellInstance
                {
                    Col = (ushort)Math.Max(0, c),
                    Row = (ushort)Math.Max(0, r),
                    BgR = sR,
                    BgG = sG,
                    BgB = sB,
                    BgA = 128
                };
            }
        }

        // 2. Menu background box
        ExtractRgb(menuBgColor, out byte bgR, out byte bgG, out byte bgB);
        int menuStartCol = (int)Math.Floor((layout.Bounds.Left - paddingLeft) / cellWidth);
        int menuEndCol = (int)Math.Ceiling((layout.Bounds.Right - paddingLeft) / cellWidth);
        int menuStartRow = (int)Math.Floor((layout.Bounds.Top - paddingTop) / cellHeight);
        int menuEndRow = (int)Math.Ceiling((layout.Bounds.Bottom - paddingTop) / cellHeight);

        for (int r = menuStartRow; r < menuEndRow; r++)
        {
            for (int c = menuStartCol; c < menuEndCol; c++)
            {
                if (written >= destination.Length) break;
                destination[written++] = new CellInstance
                {
                    Col = (ushort)Math.Max(0, c),
                    Row = (ushort)Math.Max(0, r),
                    BgR = bgR,
                    BgG = bgG,
                    BgB = bgB,
                    BgA = 255
                };
            }
        }

        // 3. Sleek border around menu
        ExtractRgb(borderColor, out byte bR, out byte bG, out byte bB);
        for (int c = menuStartCol; c < menuEndCol; c++)
        {
            // Top border
            if (written < destination.Length)
            {
                destination[written++] = new CellInstance
                {
                    Col = (ushort)Math.Max(0, c),
                    Row = (ushort)Math.Max(0, menuStartRow),
                    BgR = bR,
                    BgG = bG,
                    BgB = bB,
                    BgA = 255
                };
            }
            // Bottom border
            if (written < destination.Length && menuEndRow - 1 > menuStartRow)
            {
                destination[written++] = new CellInstance
                {
                    Col = (ushort)Math.Max(0, c),
                    Row = (ushort)Math.Max(0, menuEndRow - 1),
                    BgR = bR,
                    BgG = bG,
                    BgB = bB,
                    BgA = 255
                };
            }
        }

        for (int r = menuStartRow; r < menuEndRow; r++)
        {
            // Left border
            if (written < destination.Length)
            {
                destination[written++] = new CellInstance
                {
                    Col = (ushort)Math.Max(0, menuStartCol),
                    Row = (ushort)Math.Max(0, r),
                    BgR = bR,
                    BgG = bG,
                    BgB = bB,
                    BgA = 255
                };
            }
            // Right border
            if (written < destination.Length && menuEndCol - 1 > menuStartCol)
            {
                destination[written++] = new CellInstance
                {
                    Col = (ushort)Math.Max(0, menuEndCol - 1),
                    Row = (ushort)Math.Max(0, r),
                    BgR = bR,
                    BgG = bG,
                    BgB = bB,
                    BgA = 255
                };
            }
        }

        // 4. Render items (Hover pills, Separators, Icons, Labels, Shortcuts)
        var items = model.Items;
        for (int i = 0; i < layout.Items.Length && i < items.Count; i++)
        {
            var itemLayout = layout.Items[i];
            var item = items[i];

            if (itemLayout.IsSeparator)
            {
                // Separator line
                ExtractRgb(separatorColor, out byte sepR, out byte sepG, out byte sepB);
                int sepStartCol = (int)Math.Floor((itemLayout.Bounds.Left - paddingLeft) / cellWidth);
                int sepEndCol = (int)Math.Ceiling((itemLayout.Bounds.Right - paddingLeft) / cellWidth);
                int sepRow = (int)Math.Floor((itemLayout.Bounds.Top + (itemLayout.Bounds.Height * 0.5f) - paddingTop) / cellHeight);

                for (int c = sepStartCol; c < sepEndCol; c++)
                {
                    if (written >= destination.Length) break;
                    destination[written++] = new CellInstance
                    {
                        Col = (ushort)Math.Max(0, c),
                        Row = (ushort)Math.Max(0, sepRow),
                        BgR = sepR,
                        BgG = sepG,
                        BgB = sepB,
                        BgA = 255
                    };
                }
                continue;
            }

            bool isHovered = (model.HoveredIndex == i) && !item.IsDisabled;

            // Hovered item background pill
            if (isHovered)
            {
                ExtractRgb(hoverPillBg, out byte hpR, out byte hpG, out byte hpB);
                int pillStartCol = (int)Math.Floor((itemLayout.Bounds.Left - paddingLeft) / cellWidth);
                int pillEndCol = (int)Math.Ceiling((itemLayout.Bounds.Right - paddingLeft) / cellWidth);
                int pillStartRow = (int)Math.Floor((itemLayout.Bounds.Top - paddingTop) / cellHeight);
                int pillEndRow = (int)Math.Ceiling((itemLayout.Bounds.Bottom - paddingTop) / cellHeight);

                for (int r = pillStartRow; r < pillEndRow; r++)
                {
                    for (int c = pillStartCol; c < pillEndCol; c++)
                    {
                        if (written >= destination.Length) break;
                        destination[written++] = new CellInstance
                        {
                            Col = (ushort)Math.Max(0, c),
                            Row = (ushort)Math.Max(0, r),
                            BgR = hpR,
                            BgG = hpG,
                            BgB = hpB,
                            BgA = 255
                        };
                    }
                }
            }

            uint fgColor = item.IsDisabled ? disabledFgColor : (isHovered ? 0xFFFFFFFF : itemFgColor);
            int itemRow = (int)Math.Floor((itemLayout.Bounds.Top - paddingTop) / cellHeight);

            // Icon
            if (!string.IsNullOrEmpty(item.Icon) && itemLayout.IconBounds.Width > 0)
            {
                EmitString(
                    destination,
                    ref written,
                    item.Icon,
                    itemLayout.IconBounds.Left - paddingLeft,
                    itemRow,
                    fgColor,
                    isBold: false,
                    cellWidth,
                    typeface,
                    fontSize,
                    atlas);
            }

            // Label
            if (!string.IsNullOrEmpty(item.Label))
            {
                EmitString(
                    destination,
                    ref written,
                    item.Label,
                    itemLayout.LabelBounds.Left - paddingLeft,
                    itemRow,
                    fgColor,
                    isBold: isHovered,
                    cellWidth,
                    typeface,
                    fontSize,
                    atlas);
            }

            // Shortcut
            if (!string.IsNullOrEmpty(item.Shortcut) && itemLayout.ShortcutBounds.Width > 0)
            {
                uint scFg = item.IsDisabled ? disabledFgColor : shortcutFgColor;
                EmitString(
                    destination,
                    ref written,
                    item.Shortcut,
                    itemLayout.ShortcutBounds.Left - paddingLeft,
                    itemRow,
                    scFg,
                    isBold: false,
                    cellWidth,
                    typeface,
                    fontSize,
                    atlas);
            }
        }

        return written;
    }

    private static void EmitString(
        Span<CellInstance> destination,
        ref int written,
        string text,
        float startPxX,
        float baselineRow,
        uint fgColor,
        bool isBold,
        float cellWidth,
        SKTypeface typeface,
        float fontSize,
        GlyphAtlas atlas)
    {
        if (string.IsNullOrEmpty(text)) return;
        ExtractRgb(fgColor, out byte fgR, out byte fgG, out byte fgB);

        float curX = startPxX;
        for (int i = 0; i < text.Length;)
        {
            if (written >= destination.Length) break;

            int len = char.IsSurrogatePair(text, i) ? 2 : 1;
            string grapheme = text.Substring(i, len);
            i += len;

            if (char.IsWhiteSpace(grapheme[0]))
            {
                curX += cellWidth;
                continue;
            }

            var key = new GlyphKey(grapheme, typeface, fontSize, isBold);
            if (!atlas.EnsureGlyph(key, out var glyphInfo))
            {
                curX += cellWidth;
                continue;
            }

            int col = (int)Math.Round(curX / cellWidth);
            int row = (int)baselineRow;
            int pixelColOffset = (int)(curX - (col * cellWidth));

            destination[written++] = new CellInstance
            {
                Col = (ushort)Math.Max(0, col),
                Row = (ushort)Math.Max(0, row),
                OffX = (short)(glyphInfo.LeftBearing + pixelColOffset),
                OffY = (short)(glyphInfo.BaselineOffset + glyphInfo.TopBearing),
                GlyphX = (short)glyphInfo.X,
                GlyphY = (short)glyphInfo.Y,
                GlyphW = (short)glyphInfo.Width,
                GlyphH = (short)glyphInfo.Height,
                FgR = fgR,
                FgG = fgG,
                FgB = fgB,
                Flags = isBold ? CellFlags.Bold : (byte)0,
                BgA = 0
            };

            curX += glyphInfo.Advance > 0 ? glyphInfo.Advance : cellWidth;
        }
    }
}
