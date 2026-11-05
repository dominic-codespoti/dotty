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
                SendTwoNumberReply("\x1b[".AsSpan(), _buffer.CursorRowForReport + 1, _buffer.CursorColForReport + 1, 'R');
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
        (_buffer.CursorColForReport + 1).TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = ';';
        reply[length++] = '1';
        reply[length++] = 'R';
        handler(reply[..length]);
    }

    /// <summary>Private DSR resource reports. Absent devices report their defined absence.</summary>

    public void OnPrivateDeviceStatusReport(int code, int identifier)
    {
        switch (code)
        {
            case 15: // Printer port: report actual sink readiness.
                ReplyPrivateStatus(PrinterStatus switch
                {
                    global::Dotty.Terminal.Adapter.PrinterStatus.Ready => 10,
                    global::Dotty.Terminal.Adapter.PrinterStatus.NotReady => 11,
                    _ => 13,
                });
                break;
            case 25: // No UDK facility; it is not writable.
                ReplyPrivateStatus(21);
                break;
            case 26: // Report keyboard fields supported by current conformance; host keyboard uses PCXAL profile.
                if (_conformanceLevel <= 2)
                    ReplyPrivateStatus(27, 0);
                else if (_conformanceLevel == 3)
                    ReplyPrivateStatus(27, 0, 0);
                else
                    ReplyPrivateStatus(27, 0, 0, 5);
                break;

            case 55: // Locator unavailable.
                ReplyPrivateStatus(53);
                break;
            case 56: // Locator unavailable; type is unknown.
                ReplyPrivateStatus(57, 0);
                break;
            case 62: // No macro storage.
                ReplyMacroSpace();
                break;
            case 63: // No macro checksum; preserve the caller's identifier.
                ReplyMacroChecksum(identifier);
                break;
            case 75: // No data-integrity errors.
                ReplyPrivateStatus(70);
                break;
            case 85: // SSU is unsupported.
                ReplyPrivateStatus(83);
                break;
            case 998:
                HandleVisibilityQuery();
                break;
        }
    }

    private void ReplyPrivateStatus(int value)
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        Span<char> reply = stackalloc char[24];
        "\x1b[?".AsSpan().CopyTo(reply);
        int length = 3;
        value.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = 'n';
        handler(reply[..length]);
    }
    private void ReplyPrivateStatus(int first, int second, int third)
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        Span<char> reply = stackalloc char[32];
        "\x1b[?".AsSpan().CopyTo(reply);
        int length = 3;
        first.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        second.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = ';';
        third.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = 'n';
        handler(reply[..length]);
    }

    private void ReplyPrivateStatus(int first, int second, int third, int fourth)
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        Span<char> reply = stackalloc char[32];
        "\x1b[?".AsSpan().CopyTo(reply);
        int length = 3;
        first.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        second.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = ';';
        third.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = ';';
        fourth.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = 'n';
        handler(reply[..length]);
    }

    private void ReplyPrivateStatus(int first, int second)
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        Span<char> reply = stackalloc char[24];
        "\x1b[?".AsSpan().CopyTo(reply);
        int length = 3;
        first.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        second.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = 'n';
        handler(reply[..length]);
    }

    private void ReplyMacroSpace()
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        handler("\x1b[0*{".AsSpan());
    }

    private void ReplyMacroChecksum(int identifier)
    {
        var handler = ReplyRequested;
        if (handler is null) return;
        Span<char> reply = stackalloc char[40];
        "\x1bP".AsSpan().CopyTo(reply);
        int length = 2;
        identifier.TryFormat(reply[length..], out int written);
        length += written;
        "!~0000\x1b\\".AsSpan().CopyTo(reply[length..]);
        length += 8;
        handler(reply[..length]);
    }
}
