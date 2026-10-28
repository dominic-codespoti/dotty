using System;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    /// <summary>DSR (<c>CSI Ps n</c>): 5 = status (<c>CSI 0 n</c>), 6 = CPR. Other codes are silent, as in xterm.</summary>
    public void OnDeviceStatusReport(int code)
    {
        switch (code)
        {
            case 5:
                ReplyRequested?.Invoke("\x1b[0n".AsSpan());
                break;
            case 6:
                SendTwoNumberReply("\x1b[".AsSpan(), _buffer.CursorRowForReport + 1, _buffer.CursorCol + 1, 'R');
                break;
        }
    }

    /// <summary>DECXCPR (<c>CSI ? 6 n</c>): <c>CSI ? row ; col ; page R</c>, page is always 1.</summary>
    public void OnCursorPositionReport()
    {
        var handler = ReplyRequested;
        if (handler is null)
            return;

        Span<char> reply = stackalloc char[48];
        "\x1b[?".AsSpan().CopyTo(reply);
        int length = 3;
        (_buffer.CursorRowForReport + 1).TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        (_buffer.CursorCol + 1).TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = ';';
        reply[length++] = '1';
        reply[length++] = 'R';
        handler(reply[..length]);
    }

    /// <summary>
    /// Private DSR (<c>CSI ? Ps n</c>, Ps != 6). Only 998 (visibility query, answered with
    /// <c>CSI ? 999 ; Ps n</c>) is handled; every other code is silent, as in xterm.
    /// </summary>
    public void OnPrivateDeviceStatusReport(int code)
    {
        if (code == 998)
            HandleVisibilityQuery();
    }
}
