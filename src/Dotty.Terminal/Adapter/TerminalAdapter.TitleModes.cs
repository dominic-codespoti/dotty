using System;
using System.Buffers;
using System.Text;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    private string? _iconTitle;
    private bool _titleSetHexMode;
    private bool _titleQueryHexMode;
    private bool _titleSetUtf8Mode = true;
    private bool _titleQueryUtf8Mode = true;

    /// <summary>The current icon title, independently retained from the window title.</summary>
    public string? IconTitle => _iconTitle;

    /// <summary>Title-mode reports require explicit host opt-in.</summary>
    public bool TitleReportEnabled { get; set; }

    /// <summary>Receives exact reply bytes for title reports which may contain Latin-1.</summary>
    public event Action<ReadOnlyMemory<byte>>? RawReplyBytesRequested;

    /// <summary>Sets the title bytes for OSC 0, 1, or 2 without a lossy parser character conversion.</summary>
    public void OnSetTitle(int code, ReadOnlySpan<byte> payload)
    {
        if (code is not (0 or 1 or 2)) return;

        Span<byte> decodedBytes = _titleSetHexMode
            ? stackalloc byte[Math.Min(payload.Length / 2, MaxTitleChars * 4)]
            : Span<byte>.Empty;
        scoped ReadOnlySpan<byte> titleBytes = payload;
        if (_titleSetHexMode)
        {
            if (!TryDecodeTitleHex(payload, decodedBytes, out int byteCount)) return;
            titleBytes = decodedBytes[..byteCount];
        }

        Span<char> title = stackalloc char[Math.Min(titleBytes.Length, MaxTitleChars)];
        int charCount = DecodeTitle(titleBytes, _titleSetUtf8Mode, title);

        ReadOnlySpan<char> value = title[..charCount];
        string? retainedTitle = _windowTitle;
        if (retainedTitle is null || !value.SequenceEqual(retainedTitle.AsSpan()))
        {
            retainedTitle = _iconTitle;
            if (retainedTitle is null || !value.SequenceEqual(retainedTitle.AsSpan()))
                retainedTitle = new string(value);
        }

        if (code is 0 or 1)
            _iconTitle = retainedTitle;
        if (code is 0 or 2)
        {
            _windowTitle = retainedTitle;
            TitleChanged?.Invoke(retainedTitle);
            RequestRender();
        }
    }

    /// <summary>Sets the independent title-setting and title-query encoding modes.</summary>
    public void OnSetTitleModes(ReadOnlySpan<int> modes, bool enabled)
    {
        if (modes.IsEmpty)
        {
            ResetTitleModes();
            return;
        }

        foreach (int mode in modes)
        {
            switch (mode)
            {
                case 0: _titleSetHexMode = enabled; break;
                case 1: _titleQueryHexMode = enabled; break;
                case 2: _titleSetUtf8Mode = enabled; break;
                case 3: _titleQueryUtf8Mode = enabled; break;
            }
        }
    }

    internal void ResetTitleModes()
    {
        _titleSetHexMode = false;
        _titleQueryHexMode = false;
        _titleSetUtf8Mode = true;
        _titleQueryUtf8Mode = true;
    }

    internal void ReplyTitle(bool icon)
    {
        if (!TitleReportEnabled) return;
        var handler = RawReplyBytesRequested;
        if (handler is null) return;

        string? title = icon ? _iconTitle : _windowTitle;
        title ??= string.Empty;
        int encodedLength = _titleQueryUtf8Mode
            ? Encoding.UTF8.GetByteCount(title.AsSpan())
            : Encoding.Latin1.GetByteCount(title.AsSpan());
        int payloadLength = _titleQueryHexMode ? encodedLength * 2 : encodedLength;
        byte[] reply = new byte[payloadLength + 5];
        reply[0] = 0x1b;
        reply[1] = (byte)']';
        reply[2] = icon ? (byte)'L' : (byte)'l';
        Span<byte> payload = reply.AsSpan(3, payloadLength);
        Span<byte> encoded = _titleQueryHexMode ? payload[..encodedLength] : payload;
        if (_titleQueryUtf8Mode)
            Encoding.UTF8.GetBytes(title.AsSpan(), encoded);
        else
            Encoding.Latin1.GetBytes(title.AsSpan(), encoded);
        if (_titleQueryHexMode)
        {
            const string hex = "0123456789ABCDEF";
            for (int i = encodedLength - 1; i >= 0; i--)
            {
                byte value = encoded[i];
                payload[i * 2] = (byte)hex[value >> 4];
                payload[i * 2 + 1] = (byte)hex[value & 15];
            }
        }
        reply[^2] = 0x1b;
        reply[^1] = (byte)'\\';
        handler(reply);
    }
    private static bool TryDecodeTitleHex(ReadOnlySpan<byte> source, Span<byte> destination, out int length)
    {
        if ((source.Length & 1) != 0)
        {
            length = 0;
            return false;
        }

        int total = source.Length / 2;
        int stored = Math.Min(total, destination.Length);
        for (int i = 0; i < total; i++)
        {
            int high = HexValue(source[i * 2]);
            int low = HexValue(source[i * 2 + 1]);
            if (high < 0 || low < 0)
            {
                length = 0;
                return false;
            }
            if (i < stored)
                destination[i] = (byte)((high << 4) | low);
        }
        length = stored;
        return true;
    }

    private static int DecodeTitle(ReadOnlySpan<byte> source, bool utf8, Span<char> destination)
    {
        int limit = Math.Min(source.Length, MaxTitleChars * 4);
        int consumed = 0;
        int written = 0;
        while (consumed < limit && written < destination.Length)
        {
            Rune rune;
            int bytesConsumed;
            if (utf8)
            {
                OperationStatus status = Rune.DecodeFromUtf8(source[consumed..limit], out rune, out bytesConsumed);
                if (status != OperationStatus.Done)
                {
                    rune = Rune.ReplacementChar;
                    bytesConsumed = 1;
                }
            }
            else
            {
                rune = new Rune(source[consumed]);
                bytesConsumed = 1;
            }
            consumed += bytesConsumed;
            int scalar = rune.Value;
            if (scalar < 0x20 || scalar is >= 0x7f and <= 0x9f) continue;
            int runeLength = rune.Utf16SequenceLength;
            if (written + runeLength > destination.Length) break;
            written += rune.EncodeToUtf16(destination[written..]);
        }
        return written;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => -1
    };
}
