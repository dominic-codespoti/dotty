using System;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using SkiaSharp;

namespace Dotty.Runtime.Tabs;

/// <summary>
/// Builds GPU <see cref="CellInstance"/> quads for rendering the tab bar in OpenGL / GPU terminal pipelines.
/// Emits instances for:
/// - Tab bar background strip across the top of the window
/// - Inactive tab background pills + tab title text + close button (×)
/// - Active tab background pill + accent indicator line + active title text + close button (×)
/// - New tab button (+) at the end of the tab bar
/// </summary>
public static class TabBarQuadBuilder
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
    /// Builds cell instances for the entire tab bar and writes them into <paramref name="destination"/>.
    /// Returns the number of cell instances written.
    /// </summary>
    public static int Build(
        TerminalTabManager tabManager,
        GlyphAtlas atlas,
        SKTypeface typeface,
        float fontSize,
        IColorScheme theme,
        float windowWidth,
        float cellWidth,
        float cellHeight,
        Span<CellInstance> destination,
        float barHeight = TabBarLayout.DefaultBarHeight)
    {
        ArgumentNullException.ThrowIfNull(tabManager);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);
        ArgumentNullException.ThrowIfNull(theme);

        if (windowWidth <= 0 || cellWidth <= 0 || cellHeight <= 0 || destination.IsEmpty)
        {
            return 0;
        }

        int written = 0;
        int tabCount = tabManager.Count;
        int activeIndex = tabManager.ActiveIndex;

        var layout = TabBarLayout.Calculate(windowWidth, tabCount, activeIndex, barHeight);

        // Derive palette
        uint themeBg = theme.Background != 0 ? theme.Background : 0xFF1E1E1E;
        uint themeFg = theme.Foreground != 0 ? theme.Foreground : 0xFFD4D4D4;
        uint accentColor = theme.AnsiBlue != 0 ? theme.AnsiBlue : 0xFF3B8EEA;
        // Background strip color: sleek dark header
        uint barBg = Darken(themeBg, 0.60f);
        // Inactive tab pill background: subtle contrast pill
        uint inactivePillBg = Darken(themeBg, 0.82f);
        // Active tab pill background: bright elevated theme background
        uint activePillBg = Lighten(themeBg, 1.40f);
        // Inactive text color: soft gray
        uint inactiveFg = Darken(themeFg, 0.65f);
        uint activeFg = themeFg;
        // 1. Tab bar background strip across the top of the window
        int barCols = (int)Math.Ceiling(windowWidth / cellWidth);
        int barRows = (int)Math.Ceiling(barHeight / cellHeight);

        ExtractRgb(barBg, out byte barBgR, out byte barBgG, out byte barBgB);
        for (int r = 0; r < barRows; r++)
        {
            for (int c = 0; c < barCols; c++)
            {
                if (written >= destination.Length) break;
                destination[written++] = new CellInstance
                {
                    Col = (ushort)c,
                    Row = (ushort)r,
                    BgR = barBgR,
                    BgG = barBgG,
                    BgB = barBgB,
                    BgA = 255
                };
            }
        }

        // 2. Render each tab
        for (int i = 0; i < layout.Tabs.Length && i < tabManager.Tabs.Count; i++)
        {
            var tabLayout = layout.Tabs[i];
            var tab = tabManager.Tabs[i];
            bool isActive = tabLayout.IsActive;

            uint pillBg = isActive ? activePillBg : inactivePillBg;
            ExtractRgb(pillBg, out byte pR, out byte pG, out byte pB);

            int startCol = (int)Math.Floor(tabLayout.TabBounds.Left / cellWidth);
            int endCol = (int)Math.Ceiling(tabLayout.TabBounds.Right / cellWidth);
            int startRow = (int)Math.Floor(tabLayout.TabBounds.Top / cellHeight);
            int endRow = (int)Math.Ceiling(tabLayout.TabBounds.Bottom / cellHeight);

            // Tab pill background cells
            for (int r = startRow; r < endRow; r++)
            {
                for (int c = startCol; c < endCol; c++)
                {
                    if (written >= destination.Length) break;
                    destination[written++] = new CellInstance
                    {
                        Col = (ushort)c,
                        Row = (ushort)r,
                        BgR = pR,
                        BgG = pG,
                        BgB = pB,
                        BgA = 255
                    };
                }
            }


            // Tab Title Text
            uint titleFg = isActive ? activeFg : inactiveFg;
            float textStartX = tabLayout.TextBounds.Left;
            float textBaselineRow = startRow;

            // If tab has an active bell alert, prepend vibrant alert dot
            if (tab.HasBellAlert)
            {
                uint alertColor = 0xFFFFB454; // Bright amber alert dot
                EmitString(destination, ref written, "●", textStartX, textBaselineRow, alertColor, isBold: true, cellWidth, typeface, fontSize, atlas);
                textStartX += cellWidth * 1.2f;
            }

            // Measure available chars to prevent title text spilling past text bounds
            float remainingWidth = Math.Max(0f, tabLayout.TextBounds.Right - textStartX);
            int maxChars = (int)Math.Max(1, remainingWidth / cellWidth);
            string title = tab.Title ?? "Terminal";
            if (title.Length > maxChars)
            {
                title = maxChars > 3 ? string.Concat(title.AsSpan(0, maxChars - 1), "…") : title.Substring(0, maxChars);
            }

            EmitString(destination, ref written, title, textStartX, textBaselineRow, titleFg, isBold: isActive, cellWidth, typeface, fontSize, atlas);
            // Close button (×)
            float closeX = tabLayout.CloseButtonBounds.Left + (tabLayout.CloseButtonBounds.Width - cellWidth) * 0.5f;
            uint closeFg = Darken(titleFg, 0.85f);
            EmitString(destination, ref written, "×", closeX, startRow, closeFg, isBold: false, cellWidth, typeface, fontSize, atlas);
        }

        // 4. New tab (+) button
        {
            uint newTabBg = Darken(themeBg, 0.85f);
            ExtractRgb(newTabBg, out byte ntBgR, out byte ntBgG, out byte ntBgB);

            int startCol = (int)Math.Floor(layout.NewTabButtonBounds.Left / cellWidth);
            int endCol = (int)Math.Ceiling(layout.NewTabButtonBounds.Right / cellWidth);
            int startRow = (int)Math.Floor(layout.NewTabButtonBounds.Top / cellHeight);
            int endRow = (int)Math.Ceiling(layout.NewTabButtonBounds.Bottom / cellHeight);

            for (int r = startRow; r < endRow; r++)
            {
                for (int c = startCol; c < endCol; c++)
                {
                    if (written >= destination.Length) break;
                    destination[written++] = new CellInstance
                    {
                        Col = (ushort)c,
                        Row = (ushort)r,
                        BgR = ntBgR,
                        BgG = ntBgG,
                        BgB = ntBgB,
                        BgA = 255
                    };
                }
            }

            float plusX = layout.NewTabButtonBounds.Left + (layout.NewTabButtonBounds.Width - cellWidth) * 0.5f;
            EmitString(destination, ref written, "+", plusX, startRow, inactiveFg, isBold: false, cellWidth, typeface, fontSize, atlas);
        }
        // 5. Divider border line separating tab bar header from terminal viewport
        uint dividerColor = Darken(themeBg, 0.45f);
        ExtractRgb(dividerColor, out byte divR, out byte divG, out byte divB);
        int lastBarRow = Math.Max(0, barRows - 1);
        for (int c = 0; c < barCols; c++)
        {
            if (written >= destination.Length) break;
            destination[written++] = new CellInstance
            {
                Col = (ushort)c,
                Row = (ushort)lastBarRow,
                BgR = divR,
                BgG = divG,
                BgB = divB,
                BgA = 255
            };
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
