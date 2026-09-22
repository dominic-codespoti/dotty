using System;
using System.Collections.Generic;
using System.Threading;
using Dotty.Abstractions.Config;
using Dotty.Rendering.Gpu;
using SkiaSharp;
using static Dotty.Runtime.Rendering.ChromeStyleUtils;

namespace Dotty.Runtime.Tabs;

/// <summary>
/// Builds the pixel-precise tab rail and its glyph content. Surfaces, accents,
/// hover states, and separators are emitted as chrome quads; text and symbols
/// remain CellInstance glyphs so they use the normal atlas pipeline.
/// </summary>
public static class TabBarQuadBuilder
{
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
        if (windowWidth <= 0f || cellWidth <= 0f || cellHeight <= 0f)
            return 0;

        int written = 0;
        var palette = ResolvePalette(theme);
        var metrics = ResolveMetrics(cellHeight);
        var layout = TabBarLayout.Calculate(windowWidth, tabManager.Count, tabManager.ActiveIndex, barHeight);

        // The rail and divider are deliberately pixel-precise. No cell-grid
        // background instances are emitted, avoiding seams when cell size and
        // the configured bar height do not line up exactly.
        EmitSolid(chromeDestination, ref chromeWritten,
            0f, 0f, windowWidth, Math.Max(0f, barHeight), 0f, palette.Rail);
        float dividerHeight = Math.Min(metrics.Hairline, Math.Max(0f, barHeight));
        EmitSolid(chromeDestination, ref chromeWritten,
            0f, Math.Max(0f, barHeight - dividerHeight), windowWidth, dividerHeight, 0f, palette.Divider);

        for (int i = 0; i < layout.Tabs.Length && i < tabManager.Tabs.Count; i++)
        {
            var tabLayout = layout.Tabs[i];
            var tab = tabManager.Tabs[i];
            bool isActive = tabLayout.IsActive;
            bool tabHovered = hoveredTabIndex == i &&
                (hoveredHitType == TabBarHitType.SelectTab || hoveredHitType == TabBarHitType.CloseTab);
            var bounds = tabLayout.TabBounds;
            if (bounds.Width <= 0f || bounds.Height <= 0f)
                continue;

            if (isActive)
            {
                // A restrained shadow gives the active tab elevation without
                // changing the interaction geometry.
                EmitSolid(chromeDestination, ref chromeWritten,
                    bounds.X - metrics.Scale,
                    bounds.Y + metrics.Scale,
                    bounds.Width + metrics.Scale * 2f,
                    bounds.Height + metrics.Scale * 2f,
                    metrics.Radius,
                    palette.Shadow,
                    0.28f,
                    metrics.ShadowBlur);
                EmitSolid(chromeDestination, ref chromeWritten,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    metrics.Radius, palette.SurfaceRaised);

                // Crisp accent indicator at the active tab's lower edge.
                EmitSolid(chromeDestination, ref chromeWritten,
                    bounds.X, Math.Max(bounds.Top, bounds.Bottom - metrics.Hairline),
                    bounds.Width, Math.Min(metrics.Hairline, bounds.Height),
                    0f, palette.Accent);
            }
            else
            {
                EmitSolid(chromeDestination, ref chromeWritten,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    metrics.RadiusSmall,
                    tabHovered ? palette.SurfaceHover : palette.Surface);
            }

            int startRow = (int)Math.Floor(bounds.Top / cellHeight);
            float textOffsetY = ComputeCenteredOffsetY(
                typeface, fontSize, startRow, cellHeight, bounds.Top, bounds.Height);
            uint titleColor = isActive ? palette.TextPrimary : palette.TextSecondary;
            float textStartX = tabLayout.TextBounds.Left;
            float textRight = tabLayout.TextBounds.Right;

            if (tab.HasBellAlert)
            {
                EmitString(destination, ref written, "●", textStartX, startRow,
                    palette.Warning, isBold: true, cellWidth, typeface, fontSize, atlas,
                    textOffsetY, textRight);
                textStartX += cellWidth * 1.2f;
            }

            // EmitString clips by measured glyph bounds, not UTF-16 length, so
            // a surrogate pair is always retained or omitted as one glyph.
            EmitString(destination, ref written, tab.Title ?? "Terminal",
                textStartX, startRow, titleColor, isBold: isActive,
                cellWidth, typeface, fontSize, atlas, textOffsetY,
                textRight, appendEllipsis: true);

            var closeBounds = tabLayout.CloseButtonBounds;
            bool closeHovered = hoveredTabIndex == i && hoveredHitType == TabBarHitType.CloseTab;
            if (closeHovered)
            {
                float diameter = Math.Min(closeBounds.Width, closeBounds.Height);
                EmitSolid(chromeDestination, ref chromeWritten,
                    closeBounds.Left + (closeBounds.Width - diameter) * 0.5f,
                    closeBounds.Top + (closeBounds.Height - diameter) * 0.5f,
                    diameter, diameter, diameter * 0.5f, palette.Danger, 0.88f);
            }

            float closeX = closeBounds.Left + (closeBounds.Width - cellWidth) * 0.5f;
            float closeOffsetY = ComputeCenteredOffsetY(
                typeface, fontSize, startRow, cellHeight, closeBounds.Top, closeBounds.Height);
            EmitString(destination, ref written, "×", closeX, startRow,
                closeHovered ? palette.TextPrimary : palette.TextMuted,
                isBold: false, cellWidth, typeface, fontSize, atlas, closeOffsetY);
        }

        var newTab = layout.NewTabButtonBounds;
        if (newTab.Width > 0f && newTab.Height > 0f)
        {
            bool newTabHovered = hoveredHitType == TabBarHitType.NewTab;
            EmitSolid(chromeDestination, ref chromeWritten,
                newTab.X, newTab.Y, newTab.Width, newTab.Height,
                metrics.RadiusSmall,
                newTabHovered ? palette.SurfaceHover : palette.Surface);

            int newTabRow = (int)Math.Floor(newTab.Top / cellHeight);
            float plusX = newTab.Left + (newTab.Width - cellWidth) * 0.5f;
            float plusOffsetY = ComputeCenteredOffsetY(
                typeface, fontSize, newTabRow, cellHeight, newTab.Top, newTab.Height);
            EmitString(destination, ref written, "+", plusX, newTabRow,
                newTabHovered ? palette.Accent : palette.TextSecondary,
                isBold: newTabHovered, cellWidth, typeface, fontSize, atlas, plusOffsetY,
                newTab.Right);
        }

        return written;
    }

    private static void EmitSolid(
        Span<ChromeQuadInstance> destination,
        ref int written,
        float x,
        float y,
        float width,
        float height,
        float radius,
        uint color,
        float alpha = 1f,
        float blur = 0f)
    {
        var (r, g, b, _) = ToFloatColor(color, alpha);
        EmitChrome(destination, ref written, new ChromeQuadInstance
        {
            X = x,
            Y = y,
            W = Math.Max(0f, width),
            H = Math.Max(0f, height),
            Radius = Math.Max(0f, radius),
            Blur = Math.Max(0f, blur),
            TopR = r,
            TopG = g,
            TopB = b,
            TopA = alpha,
            BottomR = r,
            BottomG = g,
            BottomB = b,
            BottomA = alpha
        });
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
        float extraOffsetY = 0f,
        float maxRightPx = float.PositiveInfinity,
        bool appendEllipsis = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        ExtractRgb(fgColor, out byte fgR, out byte fgG, out byte fgB);

        ReadOnlySpan<char> span = text.AsSpan();
        float curX = startPxX;
        int initialWritten = written;
        float lastGlyphStart = startPxX;
        bool clipped = false;
        for (int i = 0; i < span.Length;)
        {
            int len = i + 1 < span.Length && char.IsSurrogatePair(span[i], span[i + 1]) ? 2 : 1;
            string grapheme = GlyphTextCache.Get(span, i, len);
            i += len;

            if (char.IsWhiteSpace(span[i - len]))
            {
                curX += cellWidth;
                if (curX > maxRightPx)
                {
                    clipped = i < span.Length;
                    break;
                }
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
            int pixelColOffset = (int)(curX - (col * cellWidth));
            float drawLeft = col * cellWidth + pixelColOffset + glyphInfo.LeftBearing;
            float drawRight = drawLeft + glyphInfo.Width;
            if (drawLeft < startPxX || drawRight > maxRightPx)
            {
                clipped = true;
                break;
            }

            if (written >= destination.Length)
                break;

            lastGlyphStart = curX;
            destination[written++] = new CellInstance
            {
                Col = (ushort)Math.Max(0, col),
                Row = (ushort)Math.Max(0, (int)baselineRow),
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

        if (appendEllipsis && clipped && written > initialWritten)
        {
            written--;
            curX = lastGlyphStart;
        }

        if (appendEllipsis && clipped && written < destination.Length)
        {
            EmitString(destination, ref written, "…", curX, baselineRow,
                fgColor, isBold, cellWidth, typeface, fontSize, atlas,
                extraOffsetY, maxRightPx);
        }
    }

    internal static class GlyphTextCache
    {
        private static readonly string?[] Ascii = new string?[128];
        private static readonly Dictionary<char, string> UnicodeSingles = new();
        private static readonly Dictionary<int, string> SurrogatePairs = new();
        private static readonly object UnicodeLock = new();
        private static readonly object PairLock = new();

        internal static string Get(ReadOnlySpan<char> text, int index, int length)
        {
            if (length == 1)
            {
                char value = text[index];
                if (value < Ascii.Length)
                {
                    string? cached = Ascii[value];
                    if (cached != null) return cached;

                    string created = text.Slice(index, 1).ToString();
                    return Interlocked.CompareExchange(ref Ascii[value], created, null) ?? created;
                }

                lock (UnicodeLock)
                {
                    if (UnicodeSingles.TryGetValue(value, out string? cached))
                        return cached;

                    string created = text.Slice(index, 1).ToString();
                    UnicodeSingles.Add(value, created);
                    return created;
                }
            }

            char first = text[index];
            char second = text[index + 1];
            int pair = (first << 16) | second;
            lock (PairLock)
            {
                if (SurrogatePairs.TryGetValue(pair, out string? cached))
                    return cached;

                string created = text.Slice(index, 2).ToString();
                SurrogatePairs.Add(pair, created);
                return created;
            }
        }
    }
}
