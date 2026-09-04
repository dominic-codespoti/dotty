using System;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using static Dotty.Runtime.Rendering.ChromeStyleUtils;

namespace Dotty.Runtime.Tabs;

/// <summary>
/// Builds GPU instances for rendering the tab bar in OpenGL / GPU terminal pipelines.
/// Emits:
/// - <see cref="CellInstance"/> quads for the tab bar background strip, the divider,
///   and all glyphs (title text, close ×, new-tab +) via the grid glyph pass.
/// - <see cref="ChromeQuadInstance"/> quads for pixel-precise rounded chrome: tab
///   pills (with a subtle gradient and, for the active tab, a soft drop shadow and
///   an accent top strip), the close-button hover circle, and the new-tab button.
/// </summary>
public static class TabBarQuadBuilder
{
    /// <summary>
    /// Builds cell instances (background strip + glyphs) and chrome quads (rounded
    /// pills, shadow, buttons) for the entire tab bar. Returns the number of cell
    /// instances written; <paramref name="chromeWritten"/> receives the number of
    /// chrome quads written.
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
        Span<ChromeQuadInstance> chromeDestination,
        out int chromeWritten,
        float barHeight = TabBarLayout.DefaultBarHeight,
        int hoveredTabIndex = -1,
        TabBarHitType hoveredHitType = TabBarHitType.None)
    {
        ArgumentNullException.ThrowIfNull(tabManager);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(typeface);
        ArgumentNullException.ThrowIfNull(theme);

        chromeWritten = 0;

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
        // Background strip color: sleek dark header, darker than either pill so
        // the floating rounded pills read as elevated surfaces above it.
        uint barBg = Darken(themeBg, 0.55f);
        // Inactive tab pill background: subtle contrast pill, with a slightly
        // lighter flat variant used on hover for feedback.
        uint inactivePillBg = Darken(themeBg, 0.80f);
        uint inactivePillBgHover = Darken(themeBg, 0.94f);
        // Active tab pill: flat elevated fill, no gradient.
        uint activePillBg = Lighten(themeBg, 1.45f);
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

        // 2. Render each tab: rounded pill chrome quad (+ shadow/accent for the
        // active tab) positioned from the pixel-precise layout rect, then title
        // text and the close glyph through the grid glyph pass.
        for (int i = 0; i < layout.Tabs.Length && i < tabManager.Tabs.Count; i++)
        {
            var tabLayout = layout.Tabs[i];
            var tab = tabManager.Tabs[i];
            bool isActive = tabLayout.IsActive;
            bool isTabHovered = hoveredTabIndex == i &&
                (hoveredHitType == TabBarHitType.SelectTab || hoveredHitType == TabBarHitType.CloseTab);
            var bounds = tabLayout.TabBounds;

            if (isActive)
            {
                // Soft drop shadow beneath the elevated active pill.
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = bounds.X - 3f,
                    Y = bounds.Y + 2f,
                    W = bounds.Width + 6f,
                    H = bounds.Height + 4f,
                    Radius = 11f,
                    Blur = 7f,
                    TopR = 0f,
                    TopG = 0f,
                    TopB = 0f,
                    TopA = 0.35f,
                    BottomR = 0f,
                    BottomG = 0f,
                    BottomB = 0f,
                    BottomA = 0.35f
                });

                var (pR, pG, pB, pA) = ToFloatColor(activePillBg, 1f);
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = bounds.X,
                    Y = bounds.Y,
                    W = bounds.Width,
                    H = bounds.Height,
                    Radius = 8f,
                    Blur = 0f,
                    TopR = pR,
                    TopG = pG,
                    TopB = pB,
                    TopA = pA,
                    BottomR = pR,
                    BottomG = pG,
                    BottomB = pB,
                    BottomA = pA
                });
            }
            else
            {
                uint pillBg = isTabHovered ? inactivePillBgHover : inactivePillBg;
                var (pR, pG, pB, pA) = ToFloatColor(pillBg, 1f);
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = bounds.X,
                    Y = bounds.Y,
                    W = bounds.Width,
                    H = bounds.Height,
                    Radius = 8f,
                    Blur = 0f,
                    TopR = pR,
                    TopG = pG,
                    TopB = pB,
                    TopA = pA,
                    BottomR = pR,
                    BottomG = pG,
                    BottomB = pB,
                    BottomA = pA
                });
            }

            int startRow = (int)Math.Floor(bounds.Top / cellHeight);
            float tabTextOffsetY = ComputeCenteredOffsetY(typeface, fontSize, startRow, cellHeight, bounds.Top, bounds.Height);

            // Tab Title Text
            uint titleFg = isActive ? activeFg : inactiveFg;
            float textStartX = tabLayout.TextBounds.Left;
            float textBaselineRow = startRow;

            // If tab has an active bell alert, prepend vibrant alert dot
            if (tab.HasBellAlert)
            {
                uint alertColor = 0xFFFFB454; // Bright amber alert dot
                EmitString(destination, ref written, "●", textStartX, textBaselineRow, alertColor, isBold: true, cellWidth, typeface, fontSize, atlas, tabTextOffsetY);
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

            EmitString(destination, ref written, title, textStartX, textBaselineRow, titleFg, isBold: isActive, cellWidth, typeface, fontSize, atlas, tabTextOffsetY);

            // Close button (×): circular hover backdrop, then glyph
            bool closeHovered = hoveredTabIndex == i && hoveredHitType == TabBarHitType.CloseTab;
            var closeBounds = tabLayout.CloseButtonBounds;
            if (closeHovered)
            {
                float diameter = Math.Min(closeBounds.Width, closeBounds.Height);
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = closeBounds.Left + (closeBounds.Width - diameter) * 0.5f,
                    Y = closeBounds.Top + (closeBounds.Height - diameter) * 0.5f,
                    W = diameter,
                    H = diameter,
                    Radius = diameter * 0.5f,
                    Blur = 0f,
                    TopR = 0.92f,
                    TopG = 0.30f,
                    TopB = 0.30f,
                    TopA = 0.85f,
                    BottomR = 0.92f,
                    BottomG = 0.30f,
                    BottomB = 0.30f,
                    BottomA = 0.85f
                });
            }

            float closeX = closeBounds.Left + (closeBounds.Width - cellWidth) * 0.5f;
            uint closeFg = closeHovered ? 0xFFFFFFFF : Darken(titleFg, 0.85f);
            float closeOffsetY = ComputeCenteredOffsetY(typeface, fontSize, startRow, cellHeight, closeBounds.Top, closeBounds.Height);
            EmitString(destination, ref written, "×", closeX, startRow, closeFg, isBold: false, cellWidth, typeface, fontSize, atlas, closeOffsetY);
        }

        // 4. New tab (+) button: rounded chrome quad + glyph
        {
            bool newTabHovered = hoveredHitType == TabBarHitType.NewTab;
            uint newTabBg = newTabHovered ? Darken(themeBg, 0.95f) : Darken(themeBg, 0.85f);
            var (nR, nG, nB, nA) = ToFloatColor(newTabBg, 1f);
            var nb = layout.NewTabButtonBounds;
            EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
            {
                X = nb.X,
                Y = nb.Y,
                W = nb.Width,
                H = nb.Height,
                Radius = 6f,
                Blur = 0f,
                TopR = nR,
                TopG = nG,
                TopB = nB,
                TopA = nA,
                BottomR = nR,
                BottomG = nG,
                BottomB = nB,
                BottomA = nA
            });

            int newTabRow = (int)Math.Floor(nb.Top / cellHeight);
            float plusX = nb.Left + (nb.Width - cellWidth) * 0.5f;
            float newTabOffsetY = ComputeCenteredOffsetY(typeface, fontSize, newTabRow, cellHeight, nb.Top, nb.Height);
            EmitString(destination, ref written, "+", plusX, newTabRow, newTabHovered ? activeFg : inactiveFg, isBold: false, cellWidth, typeface, fontSize, atlas, newTabOffsetY);
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
