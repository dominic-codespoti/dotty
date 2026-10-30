using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class CursorGeometryTests
{
    private const TerminalCursorShape Block = TerminalCursorShape.Block;
    private const TerminalCursorShape Beam = TerminalCursorShape.Beam;
    private const TerminalCursorShape Underline = TerminalCursorShape.Underline;
    private const CursorUnfocusedStyle Hollow = CursorUnfocusedStyle.Hollow;
    private const CursorUnfocusedStyle Same = CursorUnfocusedStyle.Same;
    private const CursorUnfocusedStyle Hidden = CursorUnfocusedStyle.Hidden;

    public static IEnumerable<object[]> PlanCases()
    {
        // shape, focused, unfocused style, visible, left, top, cell w, cell h, span, scale, recolours text, expected rects
        yield return Case(Block, true, Hollow, true, 10f, 20f, 8f, 16f, 1, 1f, true, R(10, 20, 8, 16));
        yield return Case(Block, true, Hollow, true, 10f, 20f, 8f, 16f, 2, 1f, true, R(10, 20, 16, 16));
        // Fractional origins snap like the cell shader (floor(v + 0.5)), so the block covers the cell background exactly.
        yield return Case(Block, true, Hollow, true, 12.5f, 22.4f, 9.6f, 19.2f, 1, 1.2f, true, R(13, 22, 9, 20));
        yield return Case(Beam, true, Hollow, true, 10f, 20f, 8f, 16f, 1, 1f, false, R(10, 20, 2, 16));
        yield return Case(Beam, true, Hollow, true, 10f, 20f, 8f, 16f, 2, 1f, false, R(10, 20, 2, 16));
        yield return Case(Beam, true, Hollow, true, 20f, 40f, 16f, 32f, 1, 2f, false, R(20, 40, 4, 32));
        yield return Case(Beam, true, Hollow, true, 10f, 20f, 8f, 16f, 1, 0.25f, false, R(10, 20, 1, 16));
        yield return Case(Beam, true, Hollow, true, 10f, 20f, 1f, 16f, 1, 3f, false, R(10, 20, 1, 16));
        yield return Case(Underline, true, Hollow, true, 10f, 20f, 8f, 16f, 1, 1f, false, R(10, 34, 8, 2));
        yield return Case(Underline, true, Hollow, true, 10f, 20f, 8f, 16f, 2, 1.5f, false, R(10, 33, 16, 3));
        // Off phase of a blinking cursor.
        yield return Case(Block, true, Hollow, false, 10f, 20f, 8f, 16f, 1, 1f, false);
        // Unfocused: hollow outlines the cell(s) whatever the shape; same keeps the shape; hidden draws nothing.
        yield return Case(Beam, false, Hollow, true, 10f, 20f, 8f, 16f, 1, 1f, false,
            R(10, 20, 8, 1), R(10, 35, 8, 1), R(10, 21, 1, 14), R(17, 21, 1, 14));
        yield return Case(Block, false, Hollow, true, 10f, 20f, 8f, 16f, 2, 2f, false,
            R(10, 20, 16, 2), R(10, 34, 16, 2), R(10, 22, 2, 12), R(24, 22, 2, 12));
        yield return Case(Block, false, Hollow, true, 10f, 20f, 1f, 16f, 1, 1f, false, R(10, 20, 1, 16));
        yield return Case(Block, false, Same, true, 10f, 20f, 8f, 16f, 1, 1f, true, R(10, 20, 8, 16));
        yield return Case(Underline, false, Same, true, 10f, 20f, 8f, 16f, 1, 1f, false, R(10, 34, 8, 2));
        yield return Case(Block, false, Hidden, true, 10f, 20f, 8f, 16f, 1, 1f, false);
        // Hidden only applies while unfocused.
        yield return Case(Beam, true, Hidden, true, 10f, 20f, 8f, 16f, 1, 1f, false, R(10, 20, 2, 16));
        // Degenerate cells draw nothing.
        yield return Case(Block, true, Hollow, true, 10f, 20f, 0f, 16f, 1, 1f, false);
    }

    [Theory]
    [MemberData(nameof(PlanCases))]
    public void Plan_ProducesSnappedRectangles(
        TerminalCursorShape shape, bool focused, CursorUnfocusedStyle unfocusedStyle, bool visible,
        float left, float top, float cellWidth, float cellHeight, int span, float scale,
        bool expectedRecolor, CursorRect[] expected)
    {
        CursorPlan plan = CursorGeometry.Plan(shape, focused, unfocusedStyle, visible, left, top, cellWidth, cellHeight, span, scale);

        var actual = new CursorRect[plan.RectCount];
        for (int i = 0; i < plan.RectCount; i++)
            actual[i] = plan[i];
        Assert.Equal(expected, actual);
        Assert.Equal(expectedRecolor, plan.RecolorsText);
    }

    [Theory]
    // appDefault, appShape, appBlink, configShape, configBlink, expected shape, expected blink
    [InlineData(true, TerminalCursorShape.Block, true, TerminalCursorShape.Beam, false, TerminalCursorShape.Beam, false)]
    [InlineData(true, TerminalCursorShape.Block, true, TerminalCursorShape.Underline, true, TerminalCursorShape.Underline, true)]
    [InlineData(false, TerminalCursorShape.Block, false, TerminalCursorShape.Beam, true, TerminalCursorShape.Block, false)]
    [InlineData(false, TerminalCursorShape.Underline, true, TerminalCursorShape.Block, false, TerminalCursorShape.Underline, true)]
    public void ResolveStyle_UsesTheConfigUntilTheApplicationSetsDecscusr(
        bool appDefault, TerminalCursorShape appShape, bool appBlink,
        TerminalCursorShape configShape, bool configBlink,
        TerminalCursorShape expectedShape, bool expectedBlink)
    {
        var (shape, blinking) = CursorGeometry.ResolveStyle(appDefault, appShape, appBlink, configShape, configBlink);

        Assert.Equal(expectedShape, shape);
        Assert.Equal(expectedBlink, blinking);
    }

    [Theory]
    // mode, cell fg, cell bg, cursor, expected
    [InlineData(CursorTextMode.Auto, 0xFFD4D4D4u, 0xFF1E1E1Eu, 0xFFD4D4D4u, 0xFF1E1E1Eu)]
    [InlineData(CursorTextMode.Auto, 0xFFFF0000u, 0xFF0000FFu, 0xFFD4D4D4u, 0xFF0000FFu)]
    // Inverse cell: its background equals the cursor colour, so the text keeps its own colour to stay readable.
    [InlineData(CursorTextMode.Auto, 0xFF1E1E1Eu, 0xFFD4D4D4u, 0xFFD4D4D4u, 0xFF1E1E1Eu)]
    // Only RGB is compared and the result is opaque.
    [InlineData(CursorTextMode.Auto, 0xFF102030u, 0x80D4D4D4u, 0xFFD4D4D4u, 0xFF102030u)]
    [InlineData(CursorTextMode.CellForeground, 0xFFFF0000u, 0xFF0000FFu, 0xFFD4D4D4u, 0xFFFF0000u)]
    public void ResolveTextColor_PicksAReadableColour(CursorTextMode mode, uint foreground, uint background, uint cursor, uint expected)
    {
        var color = CursorGeometry.ResolveTextColor(mode, new SgrColorArgb(foreground), new SgrColorArgb(background), new SgrColorArgb(cursor));

        Assert.Equal(new SgrColorArgb(expected), color);
    }

    [Theory]
    [InlineData("Block", TerminalCursorShape.Block)]
    [InlineData("beam", TerminalCursorShape.Beam)]
    [InlineData("UNDERLINE", TerminalCursorShape.Underline)]
    [InlineData("bar", TerminalCursorShape.Block)]
    [InlineData(null, TerminalCursorShape.Block)]
    public void ParseShape_FallsBackToBlock(string? value, TerminalCursorShape expected) =>
        Assert.Equal(expected, CursorGeometry.ParseShape(value));

    [Theory]
    [InlineData("hollow", CursorUnfocusedStyle.Hollow)]
    [InlineData("Same", CursorUnfocusedStyle.Same)]
    [InlineData("HIDDEN", CursorUnfocusedStyle.Hidden)]
    [InlineData("outline", CursorUnfocusedStyle.Hollow)]
    [InlineData(null, CursorUnfocusedStyle.Hollow)]
    public void ParseUnfocusedStyle_FallsBackToHollow(string? value, CursorUnfocusedStyle expected) =>
        Assert.Equal(expected, CursorGeometry.ParseUnfocusedStyle(value));

    [Theory]
    [InlineData("auto", CursorTextMode.Auto)]
    [InlineData("Cell-Foreground", CursorTextMode.CellForeground)]
    [InlineData("#ffffff", CursorTextMode.Auto)]
    [InlineData(null, CursorTextMode.Auto)]
    public void ParseTextMode_FallsBackToAuto(string? value, CursorTextMode expected) =>
        Assert.Equal(expected, CursorGeometry.ParseTextMode(value));

    private static object[] Case(
        TerminalCursorShape shape, bool focused, CursorUnfocusedStyle style, bool visible,
        float left, float top, float width, float height, int span, float scale, bool recolor,
        params CursorRect[] rects) =>
        [shape, focused, style, visible, left, top, width, height, span, scale, recolor, rects];

    private static CursorRect R(float x, float y, float width, float height) => new(x, y, width, height);
}
