using System;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Input;

/// <summary>What happened to the pointer in a <see cref="MouseReport"/>.</summary>
public enum MouseEventKind
{
    Press,
    Release,
    Motion,
}

/// <summary>
/// One pointer event to report, in the tracking mode and coordinate encoding the application selected.
/// <paramref name="Button"/> is 0 (left), 1 (middle), 2 (right), <see cref="MouseEncoder.ButtonNone"/> for motion with
/// no button held, or a wheel button (<see cref="MouseEncoder.WheelUp"/>..<see cref="MouseEncoder.WheelRight"/>).
/// <paramref name="Column"/>/<paramref name="Row"/> are 0-based cells; <paramref name="PixelX"/>/<paramref name="PixelY"/>
/// are 0-based framebuffer pixels inside the pane (used by SGR-pixels, mode 1016).
/// </summary>
public readonly record struct MouseReport(
    TerminalAdapter.MouseMode Mode,
    TerminalAdapter.MouseEncoding Encoding,
    int Button,
    MouseEventKind Kind,
    int Column,
    int Row,
    int PixelX,
    int PixelY,
    TerminalKeyModifiers Modifiers);

/// <summary>
/// Pure, allocation-free xterm mouse report encoder. Tracking modes: X10 (9, button presses only, no modifiers),
/// normal (1000), button-event (1002) and any-event (1003). Coordinate encodings: default (<c>CSI M Cb Cx Cy</c>,
/// positions up to 223), UTF-8 (1005, positions up to 2015), urxvt (1015, <c>CSI Cb ; x ; y M</c>), SGR (1006,
/// <c>CSI &lt; b ; x ; y M/m</c>) and SGR-pixels (1016, SGR with pixel positions). Positions that the selected encoding
/// cannot represent produce no report.
/// </summary>
public static class MouseEncoder
{
    /// <summary>Destination capacity that fits every report this encoder writes.</summary>
    public const int MaxSequenceLength = 32;

    /// <summary>Largest 1-based position the default (X10 byte) encoding can carry.</summary>
    public const int MaxLegacyPosition = 223;

    /// <summary>Largest 1-based position the UTF-8 (1005) encoding can carry: <c>2015 + 32</c> is the largest 2-byte UTF-8 scalar.</summary>
    public const int MaxUtf8Position = 2015;

    public const int ButtonNone = 3;
    public const int WheelUp = 64;
    public const int WheelDown = 65;
    public const int WheelLeft = 66;
    public const int WheelRight = 67;

    /// <summary>
    /// Writes the report for <paramref name="report"/> into <paramref name="destination"/> (at least
    /// <see cref="MaxSequenceLength"/> bytes). Returns the byte count, or zero when the mode does not report this event
    /// or the position cannot be encoded.
    /// </summary>
    public static int Encode(in MouseReport report, Span<byte> destination)
    {
        if (!TryGetButtonCode(report, out int code))
            return 0;

        bool pixels = report.Encoding == TerminalAdapter.MouseEncoding.SGRPixels;
        int x = (pixels ? report.PixelX : report.Column) + 1;
        int y = (pixels ? report.PixelY : report.Row) + 1;
        if (x < 1 || y < 1)
            return 0;

        int offset = 0;
        destination[offset++] = 0x1b;
        destination[offset++] = (byte)'[';
        switch (report.Encoding)
        {
            case TerminalAdapter.MouseEncoding.SGR:
            case TerminalAdapter.MouseEncoding.SGRPixels:
                destination[offset++] = (byte)'<';
                offset = WriteNumber(destination, offset, code);
                destination[offset++] = (byte)';';
                offset = WriteNumber(destination, offset, x);
                destination[offset++] = (byte)';';
                offset = WriteNumber(destination, offset, y);
                destination[offset++] = (byte)(report.Kind == MouseEventKind.Release ? 'm' : 'M');
                return offset;

            case TerminalAdapter.MouseEncoding.URXVT:
                offset = WriteNumber(destination, offset, code + 32);
                destination[offset++] = (byte)';';
                offset = WriteNumber(destination, offset, x);
                destination[offset++] = (byte)';';
                offset = WriteNumber(destination, offset, y);
                destination[offset++] = (byte)'M';
                return offset;

            case TerminalAdapter.MouseEncoding.UTF8:
                if (x > MaxUtf8Position || y > MaxUtf8Position)
                    return 0;
                destination[offset++] = (byte)'M';
                offset = WriteUtf8(destination, offset, code + 32);
                offset = WriteUtf8(destination, offset, x + 32);
                offset = WriteUtf8(destination, offset, y + 32);
                return offset;

            default:
                if (x > MaxLegacyPosition || y > MaxLegacyPosition)
                    return 0;
                destination[offset++] = (byte)'M';
                destination[offset++] = (byte)(code + 32);
                destination[offset++] = (byte)(x + 32);
                destination[offset++] = (byte)(y + 32);
                return offset;
        }
    }

    /// <summary>
    /// Alternate scroll (mode 1007): writes up to <paramref name="count"/> cursor-up (<paramref name="up"/>) or
    /// cursor-down controls, <c>SS3 A/B</c> under DECCKM and <c>CSI A/B</c> otherwise, limited by the capacity of
    /// <paramref name="destination"/> (three bytes each). Returns the byte count.
    /// </summary>
    public static int EncodeAlternateScroll(bool up, bool applicationCursorKeys, int count, Span<byte> destination)
    {
        int sequences = Math.Min(Math.Max(0, count), destination.Length / 3);
        byte introducer = applicationCursorKeys ? (byte)'O' : (byte)'[';
        byte final = up ? (byte)'A' : (byte)'B';
        int offset = 0;
        for (int i = 0; i < sequences; i++)
        {
            destination[offset++] = 0x1b;
            destination[offset++] = introducer;
            destination[offset++] = final;
        }
        return offset;
    }

    // The xterm button code before the +32 offset of the byte encodings.
    private static bool TryGetButtonCode(in MouseReport report, out int code)
    {
        code = 0;
        var mode = report.Mode;
        int button = report.Button;
        bool wheel = button is >= WheelUp and <= WheelRight;
        if (mode == TerminalAdapter.MouseMode.None || (!wheel && button is < 0 or > ButtonNone))
            return false;

        switch (report.Kind)
        {
            case MouseEventKind.Press:
                if (button == ButtonNone)
                    return false;
                break;
            case MouseEventKind.Release:
                // Wheel buttons have no release; X10 reports presses only.
                if (wheel || button == ButtonNone || mode == TerminalAdapter.MouseMode.X10)
                    return false;
                break;
            case MouseEventKind.Motion:
                if (wheel)
                    return false;
                if (mode == TerminalAdapter.MouseMode.AnyEvent)
                    break;
                if (mode != TerminalAdapter.MouseMode.ButtonEvent || button == ButtonNone)
                    return false;
                break;
            default:
                return false;
        }

        if (mode == TerminalAdapter.MouseMode.X10)
        {
            // X10 compatibility: buttons 1-3 only, no modifier bits (xterm ignores the wheel here too).
            if (wheel)
                return false;
            code = button;
            return true;
        }

        bool sgr = report.Encoding is TerminalAdapter.MouseEncoding.SGR or TerminalAdapter.MouseEncoding.SGRPixels;
        code = report.Kind == MouseEventKind.Release && !sgr ? ButtonNone : button;
        if (report.Kind == MouseEventKind.Motion)
            code += 32;
        var modifiers = report.Modifiers;
        if ((modifiers & TerminalKeyModifiers.Shift) != 0) code += 4;
        if ((modifiers & TerminalKeyModifiers.Alt) != 0) code += 8;
        if ((modifiers & TerminalKeyModifiers.Control) != 0) code += 16;
        return true;
    }

    private static int WriteUtf8(Span<byte> destination, int offset, int value)
    {
        if (value < 0x80)
        {
            destination[offset++] = (byte)value;
            return offset;
        }

        destination[offset++] = (byte)(0xC0 | (value >> 6));
        destination[offset++] = (byte)(0x80 | (value & 0x3F));
        return offset;
    }

    private static int WriteNumber(Span<byte> destination, int offset, int value)
    {
        Span<byte> digits = stackalloc byte[10];
        int count = 0;
        do
        {
            digits[count++] = (byte)('0' + value % 10);
            value /= 10;
        }
        while (value != 0);

        while (count > 0)
            destination[offset++] = digits[--count];
        return offset;
    }
}

/// <summary>
/// Motion de-duplication for one reporting pane (xterm behaviour): a motion report at the same cell, or the same pixel
/// under SGR-pixels, as the last report sent is dropped; presses and releases always pass and update the position.
/// </summary>
public struct MouseMotionFilter
{
    private int _x;
    private int _y;
    private bool _pixels;
    private bool _hasLast;

    /// <summary>True when <paramref name="report"/> should be sent; records its position when it is.</summary>
    public bool Admit(in MouseReport report)
    {
        bool pixels = report.Encoding == TerminalAdapter.MouseEncoding.SGRPixels;
        int x = pixels ? report.PixelX : report.Column;
        int y = pixels ? report.PixelY : report.Row;
        if (report.Kind == MouseEventKind.Motion && _hasLast && _pixels == pixels && _x == x && _y == y)
            return false;

        _x = x;
        _y = y;
        _pixels = pixels;
        _hasLast = true;
        return true;
    }

    /// <summary>Forgets the last position (reporting pane changed).</summary>
    public void Reset() => this = default;
}
