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
        out int chromeWritten)
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

        var palette = ResolvePalette(theme);
        var metrics = ResolveMetrics(20f * layout.Scale);
        uint itemFgColor = palette.TextPrimary;
        uint disabledFgColor = palette.TextMuted;
        uint shortcutFgColor = palette.TextSecondary;

        // 1. Elevated shadow behind the floating panel.
        var (shadowR, shadowG, shadowB, _) = ToFloatColor(palette.Shadow, 0.36f);
        EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
        {
            X = layout.ShadowBounds.X,
            Y = layout.ShadowBounds.Y + metrics.Scale * 2f,
            W = layout.ShadowBounds.Width,
            H = layout.ShadowBounds.Height,
            Radius = metrics.RadiusLarge,
            Blur = metrics.ShadowBlur,
            TopR = shadowR,
            TopG = shadowG,
            TopB = shadowB,
            TopA = 0.36f,
            BottomR = shadowR,
            BottomG = shadowG,
            BottomB = shadowB,
            BottomA = 0.36f
        });

        // 2. Raised border and inset surface.
        var (brR, brG, brB, brA) = ToFloatColor(palette.Border, 1f);
        EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
        {
            X = layout.Bounds.X,
            Y = layout.Bounds.Y,
            W = layout.Bounds.Width,
            H = layout.Bounds.Height,
            Radius = metrics.RadiusLarge,
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

        var (bgR, bgG, bgB, bgA) = ToFloatColor(palette.SurfaceRaised, 1f);
        float border = metrics.Hairline;
        EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
        {
            X = layout.Bounds.X + border,
            Y = layout.Bounds.Y + border,
            W = Math.Max(0f, layout.Bounds.Width - border * 2f),
            H = Math.Max(0f, layout.Bounds.Height - border * 2f),
            Radius = Math.Max(0f, metrics.RadiusLarge - border),
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
            var visible = IntersectUiRect(itemLayout.Bounds.ToSKRect(), layout.ContentBounds.ToSKRect());
            if (visible.Width <= 0f || visible.Height <= 0f) continue;

            if (itemLayout.IsSeparator)
            {
                var (sepR, sepG, sepB, sepA) = ToFloatColor(palette.Divider, 0.86f);
                float sepInset = layout.Scale * 8f;
                float sepY = itemLayout.Bounds.Top + itemLayout.Bounds.Height * 0.5f;
                if (sepY < visible.Top || sepY + metrics.Hairline > visible.Bottom) continue;
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = itemLayout.Bounds.Left + sepInset,
                    Y = sepY,
                    W = Math.Max(0f, itemLayout.Bounds.Width - sepInset * 2f),
                    H = metrics.Hairline,
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

            // Accent-tinted hover surface.
            if (isHovered)
            {
                var (hR, hG, hB, hA) = ToFloatColor(palette.SurfaceHover, 1f);
                EmitChrome(chromeDestination, ref chromeWritten, new ChromeQuadInstance
                {
                    X = visible.Left,
                    Y = visible.Top,
                    W = visible.Width,
                    H = visible.Height,
                    Radius = metrics.Radius,
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

            uint fgColor = item.IsDisabled ? disabledFgColor : itemFgColor;
            uint iconColor = item.IsDisabled ? disabledFgColor
                : (isHovered ? palette.Accent : palette.TextSecondary);
            var clip = layout.ContentBounds.ToSKRect();
            EmitUiIcon(destination, ref written, item.Icon, itemLayout.IconBounds.ToSKRect(), clip,
                iconColor, atlas, cellWidth, cellHeight, 16f * layout.Scale);
            EmitUiText(destination, ref written, item.Label, itemLayout.LabelBounds.ToSKRect(), clip,
                fgColor, atlas, typeface, fontSize, cellWidth, cellHeight);
            EmitUiText(destination, ref written, item.Shortcut, itemLayout.ShortcutBounds.ToSKRect(), clip,
                item.IsDisabled ? disabledFgColor : shortcutFgColor, atlas, typeface, fontSize,
                cellWidth, cellHeight);
        }

        if (layout.MaximumScrollOffset > 0f)
        {
            float indicatorHeight = layout.ContentBounds.Top - layout.Bounds.Top;
            var clip = layout.Bounds.ToSKRect();
            if (model.ScrollOffset > 0f)
                EmitUiIcon(destination, ref written, UiIcon.ChevronUp,
                    new SKRect(layout.X, layout.Y, layout.Bounds.Right, layout.ContentBounds.Top),
                    clip, shortcutFgColor, atlas, cellWidth, cellHeight,
                    indicatorHeight);
            if (model.ScrollOffset < layout.MaximumScrollOffset)
                EmitUiIcon(destination, ref written, UiIcon.ChevronDown,
                    new SKRect(layout.X, layout.ContentBounds.Bottom, layout.Bounds.Right, layout.Bounds.Bottom),
                    clip, shortcutFgColor, atlas, cellWidth, cellHeight,
                    indicatorHeight);
        }

        return written;
    }

}
