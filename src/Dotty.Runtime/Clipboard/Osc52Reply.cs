using System;
using System.Buffers;
using System.Buffers.Text;
using Dotty.Abstractions.Clipboard;

namespace Dotty.Runtime.Clipboard;

/// <summary>Builds the terminal-to-application reply for an OSC 52 clipboard read.</summary>
public static class Osc52Reply
{
    private const byte Esc = 0x1B;


    // ESC \ (string terminator)
    private const int SuffixLength = 2;

    // Far above any configurable read limit (8 MiB); keeps the reply array and Base64 length math within int range.
    private const int MaxPayloadBytes = 512 * 1024 * 1024;

    /// <summary>
    /// Encodes <c>ESC ] 52 ; c|p ; base64(<paramref name="utf8"/>) ESC \</c>. The selection letter is <c>c</c> when
    /// <paramref name="selection"/> includes <see cref="ClipboardSelection.Clipboard"/>, otherwise <c>p</c> for
    /// <see cref="ClipboardSelection.Primary"/>. Always terminated with ST. An empty payload is a valid reply
    /// (an empty clipboard). Returns false, with an empty <paramref name="reply"/>, when no selection is set or the
    /// payload is too large to encode.
    /// </summary>
    public static bool TryEncode(ClipboardSelection selection, ReadOnlySpan<byte> utf8, out byte[] reply)
    {
        ReadOnlySpan<char> selector =
            (selection & ClipboardSelection.Clipboard) != 0 ? "c" :
            (selection & ClipboardSelection.Primary) != 0 ? "p" :
            (selection & ClipboardSelection.Select) != 0 ? "s" : default;
        return TryEncode(selector, utf8, out reply);
    }

    public static bool TryEncode(ReadOnlySpan<char> selector, ReadOnlySpan<byte> utf8, out byte[] reply)
    {
        if (selector.IsEmpty || utf8.Length > MaxPayloadBytes)
        {
            reply = Array.Empty<byte>();
            return false;
        }

        int prefixLength = 6 + selector.Length;
        int encodedLength = Base64.GetMaxEncodedToUtf8Length(utf8.Length);
        byte[] buffer = new byte[prefixLength + encodedLength + SuffixLength];
        buffer[0] = Esc;
        buffer[1] = (byte)']';
        buffer[2] = (byte)'5';
        buffer[3] = (byte)'2';
        buffer[4] = (byte)';';
        for (int index = 0; index < selector.Length; index++)
        {
            char value = selector[index];
            if (value > 0x7F)
            {
                reply = Array.Empty<byte>();
                return false;
            }
            buffer[5 + index] = (byte)value;
        }
        buffer[5 + selector.Length] = (byte)';';

        OperationStatus status = Base64.EncodeToUtf8(
            utf8,
            buffer.AsSpan(prefixLength, encodedLength),
            out _,
            out int written);
        if (status != OperationStatus.Done || written != encodedLength)
        {
            reply = Array.Empty<byte>();
            return false;
        }

        buffer[prefixLength + encodedLength] = Esc;
        buffer[prefixLength + encodedLength + 1] = (byte)'\\';
        reply = buffer;
        return true;
    }
}
