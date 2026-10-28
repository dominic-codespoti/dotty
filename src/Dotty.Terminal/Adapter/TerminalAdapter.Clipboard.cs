using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Dotty.Abstractions.Clipboard;

namespace Dotty.Terminal.Adapter;

// OSC 52 clipboard write/read requests.
public partial class TerminalAdapter
{
    /// <summary>Raised on the parser thread for OSC 52 "?" queries. Concrete adapter only (not on <c>ITerminalHandler</c>); <c>TerminalSession</c> forwards it.</summary>
    public event Action<ClipboardReadRequest>? ClipboardReadRequested;

    /// <summary>
    /// Bytes-native OSC entry point used by the parser (same pattern as <see cref="OnPrintAscii"/>).
    /// Only OSC 52 is handled here; <paramref name="data"/> is the UTF-8 payload after "52;".
    /// Base64 is decoded straight from the bytes; anything the strict decoder rejects
    /// (embedded whitespace, invalid UTF-8) takes the original char path so behavior is unchanged.
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
            ClipboardReadRequested?.Invoke(new ClipboardReadRequest(selection));
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
            if (Base64.DecodeFromUtf8(b64, rented, out _, out int written) == OperationStatus.Done)
            {
                string text = Encoding.UTF8.GetString(rented, 0, written);
                handler(new ClipboardWriteRequest(selection, text));
                return;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        OnOperatingSystemCommandUtf8Fallback(code, data);
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

    // Byte twin of Osc52Selectors.Parse: non-ASCII bytes decode to chars that are never selectors.
    private static ClipboardSelection ParseOsc52Selection(ReadOnlySpan<byte> pc)
    {
        if (pc.IsEmpty)
            return ClipboardSelection.Clipboard;

        var selection = ClipboardSelection.None;
        foreach (byte c in pc)
        {
            switch (c)
            {
                case (byte)'c':
                    selection |= ClipboardSelection.Clipboard;
                    break;
                case (byte)'p':
                case (byte)'s':
                    selection |= ClipboardSelection.Primary;
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
            ClipboardReadRequested?.Invoke(new ClipboardReadRequest(selection));
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
            if (!Convert.TryFromBase64Chars(data, rented, out int written))
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
