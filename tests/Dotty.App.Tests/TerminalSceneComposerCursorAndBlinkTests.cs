using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Runtime.Tabs;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>Cursor and SGR 5/6 blink drawing in <see cref="TerminalSceneComposer"/> with the default configuration.</summary>
public sealed class TerminalSceneComposerCursorAndBlinkTests : IDisposable
{
    private const float CellWidth = 10f;
    private const float CellHeight = 20f;

    private readonly TerminalTab _tab = new(rows: 4, columns: 10);
    private readonly TerminalTabManager _manager = new(SilentPty.Create);
    private readonly GlyphAtlas _atlas = new(SKTypeface.Default, 14f, initialSize: 256);
    private readonly TerminalSceneComposer _composer;
    private readonly IColorScheme _theme = BuiltInThemes.DarkPlus;

    public TerminalSceneComposerCursorAndBlinkTests()
    {
        _composer = new TerminalSceneComposer(_atlas, SKTypeface.Default, 14f);
        _tab.Session.Adapter.Buffer.SetCursor(0, 0);
        _tab.Session.Adapter.Buffer.WriteText("ab".AsSpan(), CellAttributes.Default);
    }

    public void Dispose()
    {
        _composer.Dispose();
        _atlas.Dispose();
        _manager.Dispose();
        _tab.Dispose();
    }

    [Fact]
    public void SgrBlinkTextFollowsTheSharedPhaseThroughTheRowCache()
    {
        var buffer = _tab.Session.Adapter.Buffer;
        _tab.Session.Adapter.OnSetCursorShape(2); // steady cursor, so only the text drives BlinkActive
        buffer.SetCursor(2, 0);
        buffer.WriteText("Z".AsSpan(), new CellAttributes { SlowBlink = true });
        buffer.SetCursor(3, 9);

        bool HasGlyphAt(TerminalSceneFrame frame, int row, int column)
        {
            foreach (CellInstance instance in frame.AsSpan())
            {
                if (instance.Row == row && instance.Col == column && instance.GlyphW > 0)
                    return true;
            }
            return false;
        }

        Assert.True(HasGlyphAt(Compose(blinkPhaseOn: true), 2, 0));
        Assert.True(_composer.BlinkActive);
        TerminalSceneFrame off = Compose(blinkPhaseOn: false);
        Assert.False(HasGlyphAt(off, 2, 0));
        Assert.True(HasGlyphAt(off, 0, 0)); // non-blinking text stays
        Assert.True(_composer.BlinkActive);
        Assert.True(HasGlyphAt(Compose(blinkPhaseOn: true), 2, 0));

        buffer.SetCursor(2, 0);
        buffer.WriteText("Z".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(3, 9);
        Compose(blinkPhaseOn: true);
        Assert.False(_composer.BlinkActive);
    }

    [Fact]
    public void FocusedBlockFillsTheCellAndDrawsTheTextInTheCellBackground()
    {
        _tab.Session.Adapter.Buffer.SetCursor(0, 1);

        TerminalSceneFrame frame = Compose(blinkPhaseOn: true);

        Assert.Equal(new[] { (10f, 0f, 10f, 20f) }, CursorQuads(frame));
        Assert.True(_composer.BlinkActive);
        CellInstance glyph = InstanceAt(frame, row: 0, column: 1);
        var background = new SgrColorArgb(_theme.Background);
        Assert.Equal((background.R, background.G, background.B), (glyph.FgR, glyph.FgG, glyph.FgB));
    }

    [Fact]
    public void BlinkOffPhaseHidesABlinkingCursorOnly()
    {
        _tab.Session.Adapter.Buffer.SetCursor(0, 1);
        Assert.Empty(CursorQuads(Compose(blinkPhaseOn: false)));

        _tab.Session.Adapter.OnSetCursorShape(2); // steady block
        TerminalSceneFrame frame = Compose(blinkPhaseOn: false);

        Assert.Equal(new[] { (10f, 0f, 10f, 20f) }, CursorQuads(frame));
        Assert.False(_composer.BlinkActive);
    }

    [Theory]
    [InlineData(6, 0f, 0f, 2f, 20f)]   // steady beam at the left edge
    [InlineData(4, 0f, 18f, 10f, 2f)]  // steady underline at the bottom
    public void DecscusrShapesAreDrawnAsBars(int decscusr, float x, float y, float width, float height)
    {
        _tab.Session.Adapter.Buffer.SetCursor(0, 0);
        _tab.Session.Adapter.OnSetCursorShape(decscusr);

        TerminalSceneFrame frame = Compose(blinkPhaseOn: true);

        Assert.Equal(new[] { (x, y, width, height) }, CursorQuads(frame));
        CellInstance glyph = InstanceAt(frame, row: 0, column: 0);
        var foreground = new SgrColorArgb(_theme.Foreground);
        Assert.Equal((foreground.R, foreground.G, foreground.B), (glyph.FgR, glyph.FgG, glyph.FgB));
    }

    [Fact]
    public void UnfocusedWindowDrawsAHollowOutline()
    {
        _tab.Session.Adapter.Buffer.SetCursor(0, 0);

        TerminalSceneFrame frame = Compose(blinkPhaseOn: true, windowFocused: false);

        Assert.Equal(
            new[] { (0f, 0f, 10f, 1f), (0f, 19f, 10f, 1f), (0f, 1f, 1f, 18f), (9f, 1f, 1f, 18f) },
            CursorQuads(frame));
    }

    [Fact]
    public void DectcemHiddenCursorIsNotDrawn()
    {
        _tab.Session.Adapter.Buffer.SetCursorVisible(false);

        Assert.Empty(CursorQuads(Compose(blinkPhaseOn: true)));
    }

    [Fact]
    public void CursorOnAWideCharacterSpansBothColumns()
    {
        var buffer = _tab.Session.Adapter.Buffer;
        buffer.SetCursor(1, 0);
        buffer.WriteText("界".AsSpan(), CellAttributes.Default);
        buffer.SetCursor(1, 1); // the continuation cell resolves to its lead

        TerminalSceneFrame frame = Compose(blinkPhaseOn: true);

        Assert.Equal(new[] { (0f, 20f, 20f, 20f) }, CursorQuads(frame));
    }

    private TerminalSceneFrame Compose(bool blinkPhaseOn, bool windowFocused = true) =>
        _composer.Compose(
            _tab, _manager, _theme, new SgrColorArgb(_theme.Foreground), new SgrColorArgb(0x803385DB),
            framebufferWidth: 100, framebufferHeight: 80, cellWidth: CellWidth, cellHeight: CellHeight, scale: 1,
            rows: 4, columns: 10, padding: new PaddingUserConfig { Left = 0, Top = 0, Right = 0, Bottom = 0 },
            showTabBar: false, cursorVisible: blinkPhaseOn, scrollbarHovered: false, scrollbarDragging: false,
            searchOverlay: new SearchOverlayRenderState(false, string.Empty, -1, 0, null),
            activeContextMenu: null, windowFocused: windowFocused);

    /// <summary>Opaque chrome quads in the cursor colour that fit inside one or two cells (excludes pane-sized chrome).</summary>
    private (float X, float Y, float W, float H)[] CursorQuads(TerminalSceneFrame frame)
    {
        var cursor = new SgrColorArgb(_theme.Foreground);
        var quads = new List<(float X, float Y, float W, float H)>();
        foreach (ChromeQuadInstance quad in frame.AsChromeSpan())
        {
            bool cursorColour = quad.TopA == 1f
                && quad.TopR == cursor.R / 255f && quad.TopG == cursor.G / 255f && quad.TopB == cursor.B / 255f;
            if (cursorColour && quad.W <= 2 * CellWidth && quad.H <= CellHeight)
                quads.Add((quad.X, quad.Y, quad.W, quad.H));
        }
        return quads.ToArray();
    }

    private static CellInstance InstanceAt(TerminalSceneFrame frame, int row, int column)
    {
        foreach (CellInstance instance in frame.AsSpan())
        {
            if (instance.Row == row && instance.Col == column)
                return instance;
        }
        Assert.Fail($"No instance at ({row},{column}).");
        return default;
    }
}
