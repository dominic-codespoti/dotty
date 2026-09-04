using System;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using static Dotty.Runtime.Rendering.ChromeStyleUtils;

namespace Dotty.Runtime.ContextMenu;

/// <summary>
/// GPU quad instance builder for context menus. Emits:
/// - <see cref="CellInstance"/> quads for icon, label, and shortcut glyphs via the
///   grid glyph pass.
/// - <see cref="ChromeQuadInstance"/> quads for pixel-precise rounded chrome: a
///   soft drop shadow, a thin-bordered rounded panel, rounded item hover pills,
///   and inset separator lines — matching the tab bar's flat, rounded style.
/// </summary>
public static class ContextMenuQuadBuilder
{
    private const float MenuRadius = 10f;
    private const float BorderThickness = 1f;
    private const float ItemPillRadius = 6f;

    /// <summary>
    /// Builds cell instances (glyphs) and chrome quads (panel, shadow, hover
    /// pills, separators) for the context menu. Returns the number of cell
    /// instances written; <paramref name="chromeWritten"/> receives the
    /// number of chrome quads written.
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
        Span<ChromeQuadInstance> chromeDestination,
        out int chromeWritten,
        float paddingLeft = 0f,
        float paddingTop = 0f)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);
        ArgumentNullException.ThrowIfNull(theme);

        chromeWritten = 0;

        if (!model.IsVisible || destination.IsEmpty || cellWidth <= 0 || cellHeight <= 0)
        {
            return 0;
        }

        int written = 0;

        // Colors
        uint themeBg = theme.Background != 0 ? theme.Background : 0xFF1E1E1E;
        uint themeFg = theme.Foreground != 0 ? theme.Foreground : 0xFFD4D4D4;

        uint menuBgColor = Darken(themeBg, 0.75f);
        uint borderColor = Darken(themeBg, 0.45f);
        uint hoverPillBg = Lighten(themeBg, 1.35f);
        uint separatorColor = Darken(themeBg, 0.55f);
        uint itemFgColor = themeFg;
        uint disabledFgColor = Darken(themeFg, 0.40f);
        uint shortcutFgColor = Darken(themeFg, 0.65f);

        // 1. Soft drop shadow behind the floating panel.
        EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
        {
            X = layout.Bounds.X - 2f,
            Y = layout.Bounds.Y + 3f,
            W = layout.Bounds.Width + 4f,
            H = layout.Bounds.Height + 4f,
            Radius = MenuRadius + 3f,
            Blur = 10f,
            TopR = 0f,
            TopG = 0f,
            TopB = 0f,
            TopA = 0.40f,
            BottomR = 0f,
            BottomG = 0f,
            BottomB = 0f,
            BottomA = 0.40f
        });

        // 2. Menu panel: thin flat border, flat fill (no gradient), rounded.
        var (brR, brG, brB, brA) = ToFloatColor(borderColor, 1f);
        EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
        {
            X = layout.Bounds.X,
            Y = layout.Bounds.Y,
            W = layout.Bounds.Width,
            H = layout.Bounds.Height,
            Radius = MenuRadius,
            Blur = 0f,
            TopR = brR,
            TopG = brG,
            TopB = brB,
            TopA = brA,
            BottomR = brR,
            BottomG = brG,
            BottomB = brB,
            BottomA = brA
        });

        var (bgR, bgG, bgB, bgA) = ToFloatColor(menuBgColor, 1f);
        EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
        {
            X = layout.Bounds.X + BorderThickness,
            Y = layout.Bounds.Y + BorderThickness,
            W = Math.Max(0f, layout.Bounds.Width - BorderThickness * 2f),
            H = Math.Max(0f, layout.Bounds.Height - BorderThickness * 2f),
            Radius = Math.Max(0f, MenuRadius - BorderThickness),
            Blur = 0f,
            TopR = bgR,
            TopG = bgG,
            TopB = bgB,
            TopA = bgA,
            BottomR = bgR,
            BottomG = bgG,
            BottomB = bgB,
            BottomA = bgA
        });

        // 3. Render items (hover pills, separators, icons, labels, shortcuts)
        var items = model.Items;
        for (int i = 0; i < layout.Items.Length && i < items.Count; i++)
        {
            var itemLayout = layout.Items[i];
            var item = items[i];

            if (itemLayout.IsSeparator)
            {
                var (sepR, sepG, sepB, sepA) = ToFloatColor(separatorColor, 0.8f);
                const float sepInset = 6f;
                float sepY = itemLayout.Bounds.Top + itemLayout.Bounds.Height * 0.5f;
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = itemLayout.Bounds.Left + sepInset,
                    Y = sepY,
                    W = Math.Max(1f, itemLayout.Bounds.Width - sepInset * 2f),
                    H = 1f,
                    Radius = 0f,
                    Blur = 0f,
                    TopR = sepR,
                    TopG = sepG,
                    TopB = sepB,
                    TopA = sepA,
                    BottomR = sepR,
                    BottomG = sepG,
                    BottomB = sepB,
                    BottomA = sepA
                });
                continue;
            }

            bool isHovered = (model.HoveredIndex == i) && !item.IsDisabled;

            // Hovered item background pill
            if (isHovered)
            {
                var (hR, hG, hB, hA) = ToFloatColor(hoverPillBg, 1f);
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = itemLayout.Bounds.Left,
                    Y = itemLayout.Bounds.Top,
                    W = itemLayout.Bounds.Width,
                    H = itemLayout.Bounds.Height,
                    Radius = ItemPillRadius,
                    Blur = 0f,
                    TopR = hR,
                    TopG = hG,
                    TopB = hB,
                    TopA = hA,
                    BottomR = hR,
                    BottomG = hG,
                    BottomB = hB,
                    BottomA = hA
                });
            }

            uint fgColor = item.IsDisabled ? disabledFgColor : (isHovered ? 0xFFFFFFFF : itemFgColor);
            float boxTop = itemLayout.Bounds.Top - paddingTop;
            int itemRow = (int)Math.Floor(boxTop / cellHeight);
            float itemOffsetY = ComputeCenteredOffsetY(typeface, fontSize, itemRow, cellHeight, boxTop, itemLayout.Bounds.Height);

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
                    atlas,
                    itemOffsetY);
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
                    atlas,
                    itemOffsetY);
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
                    atlas,
                    itemOffsetY);
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
        GlyphAtlas atlas,
        float extraOffsetY = 0f)
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
            if (!atlas.EnsureGlyph(key, out var glyphInfo)
                && !atlas.TryGetFallbackGlyph(out glyphInfo))
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
                OffY = (short)(glyphInfo.BaselineOffset + glyphInfo.TopBearing + extraOffsetY),
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
