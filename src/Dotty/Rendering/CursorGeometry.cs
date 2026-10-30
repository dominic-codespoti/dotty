using System;
using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;

namespace Dotty.Silk.Rendering;

/// <summary>How the cursor is drawn while the window does not have keyboard focus (<c>cursor.unfocusedStyle</c>).</summary>
public enum CursorUnfocusedStyle : byte
{
    /// <summary>An outline of the cursor cell(s), whatever the focused shape.</summary>
    Hollow,

    /// <summary>The focused shape (a filled block keeps recolouring its text).</summary>
    Same,

    /// <summary>No cursor.</summary>
    Hidden,
}

/// <summary>Colour of the text under a filled block cursor (<c>cursor.text</c>).</summary>
public enum CursorTextMode : byte
{
    /// <summary>The cell's background colour, or its foreground when that background equals the cursor colour.</summary>
    Auto,

    /// <summary>The cell's own foreground colour.</summary>
    CellForeground,
}

/// <summary>One solid cursor rectangle in framebuffer pixels, already snapped to whole pixels.</summary>
public readonly record struct CursorRect(float X, float Y, float Width, float Height);

/// <summary>
/// Output of <see cref="CursorGeometry.Plan"/>: up to four solid rectangles (drawn as chrome quads between the cell
/// backgrounds and the glyphs) and whether the glyph under the cursor takes the cursor-text colour.
/// </summary>
public struct CursorPlan
{
    private CursorRect _r0, _r1, _r2, _r3;

    /// <summary>Number of rectangles, 0-4.</summary>
    public int RectCount { get; private set; }

    /// <summary>True for a filled block: the glyph under it is drawn in the cursor-text colour.</summary>
    public bool RecolorsText { get; internal set; }

    public readonly CursorRect this[int index] => index switch
    {
        0 when RectCount > 0 => _r0,
        1 when RectCount > 1 => _r1,
        2 when RectCount > 2 => _r2,
        3 when RectCount > 3 => _r3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal void Add(float x, float y, float width, float height)
    {
        if (width <= 0f || height <= 0f)
            return;
        var rect = new CursorRect(x, y, width, height);
        switch (RectCount)
        {
            case 0: _r0 = rect; break;
            case 1: _r1 = rect; break;
            case 2: _r2 = rect; break;
            case 3: _r3 = rect; break;
            default: throw new InvalidOperationException("A cursor plan holds at most four rectangles.");
        }
        RectCount++;
    }
}

/// <summary>
/// Pure, allocation-free cursor geometry and style resolution. Edges are snapped with <c>floor(v + 0.5)</c>, the
/// same rule the cell shader uses for backgrounds, so a block covers exactly the cell background it sits on.
/// </summary>
public static class CursorGeometry
{
    /// <summary>Beam width and underline height in logical pixels (multiplied by the display scale).</summary>
    public const float BarThickness = 2f;

    /// <summary>Hollow outline thickness in logical pixels (multiplied by the display scale).</summary>
    public const float OutlineThickness = 1f;

    /// <summary>
    /// The style to draw: the application's DECSCUSR style when it has set one, otherwise the configured
    /// <c>cursor.shape</c>/<c>cursor.blink</c> (DECSCUSR 0, DECSTR and RIS return to the configured style).
    /// </summary>
    public static (TerminalCursorShape Shape, bool Blinking) ResolveStyle(
        bool appStyleIsDefault,
        TerminalCursorShape appShape,
        bool appBlinking,
        TerminalCursorShape configShape,
        bool configBlink) =>
        appStyleIsDefault ? (configShape, configBlink) : (appShape, appBlinking);

    /// <summary>
    /// Rectangles for one cursor. <paramref name="visible"/> is false only in the off phase of a blinking cursor.
    /// <paramref name="cellSpan"/> is 2 on a wide character (the beam stays at the left edge).
    /// </summary>
    public static CursorPlan Plan(
        TerminalCursorShape shape,
        bool focused,
        CursorUnfocusedStyle unfocusedStyle,
        bool visible,
        float cellLeft,
        float cellTop,
        float cellWidth,
        float cellHeight,
        int cellSpan,
        float scale)
    {
        var plan = new CursorPlan();
        if (!visible || (!focused && unfocusedStyle == CursorUnfocusedStyle.Hidden))
            return plan;

        int span = cellSpan >= 2 ? 2 : 1;
        float left = Snap(cellLeft);
        float top = Snap(cellTop);
        float right = Snap(cellLeft + cellWidth * span);
        float bottom = Snap(cellTop + cellHeight);
        float width = right - left;
        float height = bottom - top;
        if (!(width > 0f) || !(height > 0f))
            return plan;

        scale = float.IsFinite(scale) && scale > 0f ? scale : 1f;
        if (!focused && unfocusedStyle == CursorUnfocusedStyle.Hollow)
        {
            float outline = MathF.Min(Thickness(OutlineThickness, scale), MathF.Floor(MathF.Min(width, height) / 2f));
            if (outline < 1f)
            {
                plan.Add(left, top, width, height);
                return plan;
            }
            plan.Add(left, top, width, outline);
            plan.Add(left, bottom - outline, width, outline);
            plan.Add(left, top + outline, outline, height - 2f * outline);
            plan.Add(right - outline, top + outline, outline, height - 2f * outline);
            return plan;
        }

        float bar = Thickness(BarThickness, scale);
        switch (shape)
        {
            case TerminalCursorShape.Beam:
                plan.Add(left, top, MathF.Min(bar, width), height);
                break;
            case TerminalCursorShape.Underline:
            {
                float thickness = MathF.Min(bar, height);
                plan.Add(left, bottom - thickness, width, thickness);
                break;
            }
            default:
                plan.Add(left, top, width, height);
                plan.RecolorsText = true;
                break;
        }
        return plan;
    }

    /// <summary>Colour of the glyph under a filled block cursor.</summary>
    public static SgrColorArgb ResolveTextColor(
        CursorTextMode mode,
        SgrColorArgb cellForeground,
        SgrColorArgb cellBackground,
        SgrColorArgb cursorColor)
    {
        if (mode == CursorTextMode.CellForeground)
            return cellForeground;
        bool backgroundMatchesCursor = cellBackground.R == cursorColor.R
            && cellBackground.G == cursorColor.G
            && cellBackground.B == cursorColor.B;
        var chosen = backgroundMatchesCursor ? cellForeground : cellBackground;
        return SgrColorArgb.FromRgb(chosen.R, chosen.G, chosen.B);
    }

    /// <summary><c>cursor.shape</c>: <c>Block</c> (default), <c>Beam</c>, <c>Underline</c>; case-insensitive.</summary>
    public static TerminalCursorShape ParseShape(string? value)
    {
        if (string.Equals(value, "Beam", StringComparison.OrdinalIgnoreCase))
            return TerminalCursorShape.Beam;
        if (string.Equals(value, "Underline", StringComparison.OrdinalIgnoreCase))
            return TerminalCursorShape.Underline;
        return TerminalCursorShape.Block;
    }

    /// <summary><c>cursor.unfocusedStyle</c>: <c>hollow</c> (default), <c>same</c>, <c>hidden</c>; case-insensitive.</summary>
    public static CursorUnfocusedStyle ParseUnfocusedStyle(string? value)
    {
        if (string.Equals(value, "same", StringComparison.OrdinalIgnoreCase))
            return CursorUnfocusedStyle.Same;
        if (string.Equals(value, "hidden", StringComparison.OrdinalIgnoreCase))
            return CursorUnfocusedStyle.Hidden;
        return CursorUnfocusedStyle.Hollow;
    }

    /// <summary><c>cursor.text</c>: <c>auto</c> (default) or <c>cell-foreground</c>; case-insensitive.</summary>
    public static CursorTextMode ParseTextMode(string? value) =>
        string.Equals(value, "cell-foreground", StringComparison.OrdinalIgnoreCase)
            ? CursorTextMode.CellForeground
            : CursorTextMode.Auto;

    private static float Snap(float value) => MathF.Floor(value + 0.5f);

    private static float Thickness(float logicalPixels, float scale) => MathF.Max(1f, MathF.Round(logicalPixels * scale));
}
