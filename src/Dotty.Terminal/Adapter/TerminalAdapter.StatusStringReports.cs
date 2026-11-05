using System;


namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    private int _attributeChangeExtent;

    public void OnSetAttributeChangeExtent(int mode)
    {
        if (mode is >= 0 and <= 2)
            _attributeChangeExtent = mode;
    }

    public void OnRequestStatusString(ReadOnlySpan<byte> request)
    {
        Span<char> value = stackalloc char[128];
        int length = 0;
        bool supported = true;
        if (request.SequenceEqual("*x"u8))
        {
            length = FormatInteger(_attributeChangeExtent, value);
        }
        else if (request.SequenceEqual("$}"u8))
        {
            value[0] = '0';
            length = 1;
        }
        else if (request.SequenceEqual("\"q"u8))
        {
            value[0] = _buffer.IsProtectionActive && !_buffer.RespectIsoProtection ? '1' : '0';
            length = 1;
        }
        else if (request.SequenceEqual("\"p"u8))
        {
            value[0] = '6';
            length = 1;
            value[length++] = (char)('0' + _conformanceLevel);
            value[length++] = ';';
            value[length++] = '1'; // Seven-bit controls are the implemented policy.
        }
        else if (request.SequenceEqual(" q"u8))
        {
            length = FormatInteger(CursorShape, value);
        }
        else if (request.SequenceEqual("t"u8) || request.SequenceEqual("*|"u8))
        {
            length = FormatInteger(_buffer.Rows, value);
        }
        else if (request.SequenceEqual("s"u8))
        {
            if (_conformanceLevel < 4)
                supported = false;
            else
            {
                length = FormatInteger(_buffer.LeftMargin + 1, value);
                value[length++] = ';';
                length += FormatInteger(_buffer.RightMargin + 1, value[length..]);
            }
        }
        else if (request.SequenceEqual("r"u8))
        {
            length = FormatInteger(_buffer.ScrollTop + 1, value);
            value[length++] = ';';
            length += FormatInteger(_buffer.ScrollBottom + 1, value[length..]);
        }
        else if (request.SequenceEqual("$~"u8))
        {
            value[0] = '0'; // No status line is implemented.
            length = 1;
        }
        else if (request.SequenceEqual("m"u8))
        {
            length = FormatSgr(_currentAttributes, value);
        }
        else
            supported = false;

        SendStatusString(value[..length], request, supported);
    }

    private static int FormatInteger(int number, Span<char> destination)
    {
        number.TryFormat(destination, out int written);
        return written;
    }

    private static int FormatSgr(CellAttributes attributes, Span<char> destination)
    {
        int length = 0;
        destination[length++] = '0';
        if (attributes.Bold) AppendSgr(1, destination, ref length);
        if (attributes.Faint) AppendSgr(2, destination, ref length);
        if (attributes.Italic) AppendSgr(3, destination, ref length);
        if (attributes.UnderlineStyle == UnderlineStyle.Single)
            AppendSgr(4, destination, ref length);
        else if (attributes.UnderlineStyle != UnderlineStyle.None)
        {
            destination[length++] = ';';
            destination[length++] = '4';
            destination[length++] = ':';
            ((int)attributes.UnderlineStyle).TryFormat(destination[length..], out int written);
            length += written;
        }
        if (attributes.SlowBlink) AppendSgr(5, destination, ref length);
        if (attributes.Inverse) AppendSgr(7, destination, ref length);
        if (attributes.Invisible) AppendSgr(8, destination, ref length);
        if (attributes.Strikethrough) AppendSgr(9, destination, ref length);
        if (attributes.Overline) AppendSgr(53, destination, ref length);
        AppendColor(attributes.ForegroundKind, attributes.ForegroundIndex, attributes.Foreground, 38, destination, ref length);
        AppendColor(attributes.BackgroundKind, attributes.BackgroundIndex, attributes.Background, 48, destination, ref length);
        AppendColor(attributes.UnderlineColorKind, attributes.UnderlineColorIndex, attributes.UnderlineColor, 58, destination, ref length);
        return length;
    }

    private static void AppendSgr(int value, Span<char> destination, ref int length)
    {
        destination[length++] = ';';
        value.TryFormat(destination[length..], out int written);
        length += written;
    }
    private static void AppendColor(ColorKind kind, int index, SgrColorArgb color, int channel, Span<char> destination, ref int length)
    {
        if (kind == ColorKind.Indexed)
        {
            AppendSgr(channel, destination, ref length);
            AppendSgr(5, destination, ref length);
            AppendSgr(index, destination, ref length);
        }
        else if (kind == ColorKind.Rgb)
        {
            AppendSgr(channel, destination, ref length);
            AppendSgr(2, destination, ref length);
            AppendSgr(color.R, destination, ref length);
            AppendSgr(color.G, destination, ref length);
            AppendSgr(color.B, destination, ref length);
        }
    }

    private void SendStatusString(ReadOnlySpan<char> value, ReadOnlySpan<byte> request, bool supported)
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        Span<char> reply = stackalloc char[256];
        "\x1bP".AsSpan().CopyTo(reply);
        int length = 2;
        reply[length++] = supported ? '1' : '0';
        "$r".AsSpan().CopyTo(reply[length..]);
        length += 2;
        if (supported)
        {
            value.CopyTo(reply[length..]);
            length += value.Length;
            foreach (byte b in request)
                reply[length++] = (char)b;
        }
        reply[length++] = '\x1b';
        reply[length++] = '\\';
        handler(reply[..length]);
    }

    public void OnChangeRectangleAttributes(int top, int left, int bottom, int right, ReadOnlySpan<int> parameters, bool reverse)
    {
        if (_conformanceLevel < 4 || parameters.IsEmpty)
            return;
        _buffer.ChangeRectangleAttributes(top, left, bottom, right, parameters, reverse, _attributeChangeExtent == 2);
        RequestRender();
    }

    public void OnSetScreenLines(int lines)
    {
        if (_conformanceLevel >= 4 && lines is >= 1 and <= 255)
            RequestWindowResize(lines, 0);
    }
}
