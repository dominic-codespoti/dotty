using System;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.ContextMenu;
using Dotty.Runtime.Rendering;
using Dotty.Runtime.Search;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public class ContextMenuTests
{
    private static ContextMenuModel TerminalMenu(float x = 0f, float y = 0f) => new(x, y,
        DefaultContextMenus.BuildTerminalMenu(true, () => { }, () => { }, () => { },
            () => { }, () => { }, () => { }));

    [Theory]
    [InlineData(10f, 1f)]
    [InlineData(18f, 1.25f)]
    [InlineData(32f, 2f)]
    public void MeasuredMenu_FitsFullLabelsAndShortcutsWhenSpaceIsAvailable(float fontSize, float scale)
    {
        using var font = new SKFont(SKTypeface.Default, fontSize)
        { Subpixel = false, Hinting = SKFontHinting.Full };
        var model = TerminalMenu();
        var layout = ContextMenuLayout.Calculate(model, 4000f, 3000f, SKTypeface.Default, fontSize, scale);
        for (int i = 0; i < model.Items.Count; i++)
        {
            if (model.Items[i].IsSeparator) continue;
            var row = layout.Items[i];
            Assert.True(row.LabelBounds.Width + 0.01f >= font.MeasureText(model.Items[i].Label));
            Assert.True(row.ShortcutBounds.Width + 0.01f >= font.MeasureText(model.Items[i].Shortcut ?? ""));
            Assert.True(row.LabelBounds.Right < row.ShortcutBounds.Left);
            Assert.True(row.ShortcutBounds.Right <= layout.ContentBounds.Right);
        }
    }
    [Theory]
    [InlineData(499.1f, 14f)]
    [InlineData(712.025f, 14.3f)]
    [InlineData(1023.9f, 28f)]
    public void FullWidthUiText_PaintsEveryCharacterAtFractionalPopupPositions(float x, float fontSize)
    {
        const string text = "Ctrl+Shift+PageDown";
        using var atlas = new GlyphAtlas(SKTypeface.Default, fontSize, initialSize: 512);
        float width = ChromeStyleUtils.MeasureUiText(text, SKTypeface.Default, fontSize);
        var box = new SKRect(x, 40f, x + width, 40f + fontSize * 2f);
        var glyphs = new CellInstance[64];
        int written = 0;
        ChromeStyleUtils.EmitUiText(glyphs, ref written, text, box, box, 0xFFFFFFFF,
            atlas, SKTypeface.Default, fontSize, 10f, 20f);
        Assert.Equal(19, written);
        Assert.True(atlas.EnsureGlyph(new GlyphKey("n", SKTypeface.Default, fontSize, false), out var last));
        Assert.Equal(last.X, glyphs[written - 1].GlyphX);
        Assert.Equal(last.Y, glyphs[written - 1].GlyphY);
    }


    [Theory]
    [InlineData(180f, 120f, 14f, 1f)]
    [InlineData(350f, 250f, 28f, 2f)]
    [InlineData(60f, 35f, 18f, 1.25f)]
    public void ConstrainedMenu_BoundsAndPaintedInkStayInsideViewport(float width, float height, float fontSize, float scale)
    {
        var model = TerminalMenu(900f, 700f);
        var layout = ContextMenuLayout.Calculate(model, width, height, SKTypeface.Default, fontSize, scale);
        Assert.InRange(layout.Bounds.Left, 0f, width);
        Assert.InRange(layout.Bounds.Top, 0f, height);
        Assert.InRange(layout.Bounds.Right, 0f, width);
        Assert.InRange(layout.Bounds.Bottom, 0f, height);
        using var atlas = new GlyphAtlas(SKTypeface.Default, fontSize, initialSize: 512);
        var glyphs = new CellInstance[1024];
        var chrome = new ChromeQuadInstance[64];
        int count = ContextMenuQuadBuilder.Build(model, layout, atlas, SKTypeface.Default, fontSize,
            BuiltInThemes.DarkPlus, 10f, 20f, glyphs, chrome, out _);
        for (int i = 0; i < count; i++)
        {
            float x = glyphs[i].Col * 10f + glyphs[i].OffX;
            float y = glyphs[i].Row * 20f + glyphs[i].OffY;
            Assert.InRange(x, layout.Bounds.Left, layout.Bounds.Right);
            Assert.InRange(y, layout.Bounds.Top, layout.Bounds.Bottom);
            Assert.True(x + glyphs[i].GlyphW <= layout.Bounds.Right);
            Assert.True(y + glyphs[i].GlyphH <= layout.Bounds.Bottom);
        }
        Assert.Equal(-1, ContextMenuHitTester.HitTest(layout, width - 1f, height + 1f));
    }

    [Fact]
    public void KeyboardFocus_RevealsOffscreenActionAndHitTestingUsesScrolledRows()
    {
        var model = TerminalMenu();
        ContextMenuLayout.Calculate(model, 300f, 120f);
        Assert.True(model.FocusLast());
        var layout = ContextMenuLayout.Calculate(model, 300f, 120f);
        int last = model.Items.Count - 1;
        var row = layout.Items[last].Bounds;
        Assert.True(model.ScrollOffset > 0f);
        Assert.True(row.Top >= layout.ContentBounds.Top && row.Bottom <= layout.ContentBounds.Bottom);
        Assert.Equal(last, ContextMenuHitTester.HitTest(layout, row.Left + 1f, row.Top + row.Height / 2f));
        Assert.Equal(-1, ContextMenuHitTester.HitTest(layout, row.Left + 1f, layout.ContentBounds.Top - 1f));
        Assert.True(model.FocusFirst());
        layout = ContextMenuLayout.Calculate(model, 300f, 120f);
        Assert.Equal(0f, model.ScrollOffset);
        Assert.Equal(0, ContextMenuHitTester.HitTest(layout, layout.Items[0].Bounds.Left + 1f,
            layout.Items[0].Bounds.Top + 1f));
    }

    [Theory]
    [InlineData(1000f, 600f, 1f)]
    [InlineData(180f, 80f, 2f)]
    [InlineData(30f, 15f, 1.25f)]
    public void SearchOverlay_ConstrainsEveryControlToViewport(float width, float height, float scale)
    {
        var layout = SearchOverlayLayout.Compute(width, height, "long query", 9000, 10000, scale: scale);
        foreach (var rect in new[] { layout.InputBoxRect, layout.MatchCountRect, layout.PrevButtonRect,
            layout.NextButtonRect, layout.CloseButtonRect })
        {
            Assert.True(rect.Width >= 0f && rect.Height >= 0f);
            Assert.InRange(rect.X, 0f, width);
            Assert.InRange(rect.Y, 0f, height);
            Assert.True(rect.X + rect.Width <= width + 0.001f);
            Assert.True(rect.Y + rect.Height <= height + 0.001f);
        }
    }

    [Fact]
    public void ContextMenuModel_KeyboardFocusSkipsUnavailableRowsAndWraps()
    {
        bool invoked = false;
        var model = new ContextMenuModel(items: new[]
        {
            ContextMenuItem.Item("disabled", "Disabled", () => { }, isDisabled: true),
            ContextMenuItem.Separator(),
            ContextMenuItem.Item("first", "First", () => { }),
            ContextMenuItem.Item("last", "Last", () => invoked = true)
        });
        Assert.True(model.MoveFocus(1));
        Assert.Equal(2, model.HoveredIndex);
        Assert.True(model.MoveFocus(-1));
        Assert.Equal(3, model.HoveredIndex);
        Assert.True(model.ExecuteFocused());
        Assert.True(invoked);
        Assert.False(model.IsVisible);
    }
}
