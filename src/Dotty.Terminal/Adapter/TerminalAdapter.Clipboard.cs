using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Dotty.Abstractions.Clipboard;

namespace Dotty.Terminal.Adapter;

// OSC 52 clipboard write/read requests.
public partial class TerminalAdapter
{
    private static readonly string Osc52CutBuffer0ReplySelector = "0";
    private const int MaxOsc52DecodedBytes = 786_432;
    /// <summary>Raised on the parser thread for OSC 52 "?" queries. Concrete adapter only (not on <c>ITerminalHandler</c>); <c>TerminalSession</c> forwards it.</summary>
    public event Action<ClipboardReadRequest>? ClipboardReadRequested;

    /// <summary>
    /// Bytes-native OSC entry point used by the parser (same pattern as <see cref="OnPrintAscii"/>).
    /// Only OSC 52 is handled here; <paramref name="data"/> is the UTF-8 payload after "52;".
    /// Base64 is decoded straight from the bytes. The fallback mirrors xterm's paste64 decoder by ignoring
    /// non-alphabet characters and discarding incomplete trailing groups.
    /// </summary>
    internal void OnOperatingSystemCommandUtf8(int code, ReadOnlySpan<byte> data)
    {
        if (code != 52)
        {
            OnOperatingSystemCommandUtf8Fallback(code, data);
            return;
        }

        int semi = data.IndexOf((byte)';');
        if (semi < 0)
            return;

        var selection = ParseOsc52Selection(data[..semi]);
        if (selection == ClipboardSelection.None)
            return;

        var b64 = data[(semi + 1)..];
        if (b64.Length == 1 && b64[0] == (byte)'?')
        {
            ClipboardReadRequested?.Invoke(new ClipboardReadRequest(selection, GetOsc52ReplySelector(data[..semi])));
            return;
        }

        var handler = ClipboardWriteRequested;
        if (handler is null)
            return;

        if (b64.IsEmpty)
        {
            handler(new ClipboardWriteRequest(selection, string.Empty));
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(Base64.GetMaxDecodedFromUtf8Length(b64.Length));
        try
        {
            int written = Base64.DecodeFromUtf8(b64, rented, out _, out int decoded) == OperationStatus.Done
                ? decoded
                : DecodeXtermBase64(b64, rented);
            if (written > MaxOsc52DecodedBytes)
                return;
            string text = Encoding.UTF8.GetString(rented, 0, written);
            handler(new ClipboardWriteRequest(selection, text));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void OnOperatingSystemCommandUtf8Fallback(int code, ReadOnlySpan<byte> data)
    {
        // Rare path: reproduce the parser's char decode and take the original handler.
        char[] chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(data.Length));
        try
        {
            int count = Encoding.UTF8.GetChars(data, chars);
            OnOperatingSystemCommand(code, chars.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }
    // xterm's Paste64 decoder ignores non-alphabet characters and only emits complete bytes.
    private static int DecodeXtermBase64(ReadOnlySpan<byte> encoded, Span<byte> decoded)
    {
        int written = 0;
        int accumulator = 0;
        int bits = 0;
        foreach (byte value in encoded)
        {
            int six = Base64Value(value);
            if (six < 0)
                continue;
            accumulator = (accumulator << 6) | six;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                decoded[written++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }
        return written;
    }

    private static int DecodeXtermBase64(ReadOnlySpan<char> encoded, Span<byte> decoded)
    {
        int written = 0;
        int accumulator = 0;
        int bits = 0;
        foreach (char value in encoded)
        {
            int six = Base64Value(value);
            if (six < 0)
                continue;
            accumulator = (accumulator << 6) | six;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                decoded[written++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }
        return written;
    }

    private static int Base64Value(int value) =>
        value is >= 'A' and <= 'Z' ? value - 'A' :
        value is >= 'a' and <= 'z' ? value - 'a' + 26 :
        value is >= '0' and <= '9' ? value - '0' + 52 :
        value == '+' ? 62 :
        value == '/' ? 63 :
        -1;

    // Canonical selectors are static to keep the hot OSC 52 read-query path allocation-free.
    private static string GetOsc52ReplySelector(ReadOnlySpan<byte> selector)
    {
        if (selector.IsEmpty)
            return "s0";
        if (selector.SequenceEqual("c"u8))
            return "c";
        if (selector.SequenceEqual("p"u8))
            return "p";
        if (selector.SequenceEqual("s"u8))
            return "s";
        if (selector.SequenceEqual("s0"u8))
            return "s0";
        if (selector.SequenceEqual("0"u8))
            return Osc52CutBuffer0ReplySelector;
        return Encoding.ASCII.GetString(selector);
    }

    private static string GetOsc52ReplySelector(ReadOnlySpan<char> selector)
    {
        if (selector.IsEmpty)
            return "s0";
        if (selector.SequenceEqual("c"))
            return "c";
        if (selector.SequenceEqual("p"))
            return "p";
        if (selector.SequenceEqual("s"))
            return "s";
        if (selector.SequenceEqual("s0"))
            return "s0";
        if (selector.SequenceEqual("0"))
            return Osc52CutBuffer0ReplySelector;
        return selector.ToString();
    }

    // Byte twin of Osc52Selectors.Parse: non-ASCII bytes decode to chars that are never selectors.
    private static ClipboardSelection ParseOsc52Selection(ReadOnlySpan<byte> pc)
    {
        if (pc.IsEmpty)
            return ClipboardSelection.Select | ClipboardSelection.CutBuffer0;

        var selection = ClipboardSelection.None;
        foreach (byte c in pc)
        {
            switch (c)
            {
                case (byte)'c':
                    selection |= ClipboardSelection.Clipboard;
                    break;
                case (byte)'p':
                    selection |= ClipboardSelection.Primary;
                    break;
                case (byte)'s':
                    selection |= ClipboardSelection.Select;
                    break;
                case (byte)'0':
                    selection |= ClipboardSelection.CutBuffer0;
                    break;
            }
        }
        return selection;
    }

    private void HandleOsc52(ReadOnlySpan<char> payload)
    {
        int semi = payload.IndexOf(';');
        if (semi < 0)
            return;

        var selection = Osc52Selectors.Parse(payload[..semi]);
        if (selection == ClipboardSelection.None)
            return;

        var data = payload[(semi + 1)..];
        if (data.Length == 1 && data[0] == '?')
        {
            ClipboardReadRequested?.Invoke(new ClipboardReadRequest(selection, GetOsc52ReplySelector(payload[..semi])));
            return;
        }

        var handler = ClipboardWriteRequested;
        if (handler is null)
            return;

        if (data.IsEmpty)
        {
            handler(new ClipboardWriteRequest(selection, string.Empty));
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length / 4 * 3 + 3);
        try
        {
            int written = Convert.TryFromBase64Chars(data, rented, out int decoded)
                ? decoded
                : DecodeXtermBase64(data, rented);
            if (written > MaxOsc52DecodedBytes)
                return;
            string text = Encoding.UTF8.GetString(rented, 0, written);
            handler(new ClipboardWriteRequest(selection, text));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
