using System;
using System.Buffers;
using System.Text;

namespace Dotty.Runtime.Input;

/// <summary>
/// Encodes clipboard text for delivery to a terminal program. Every C0 control except TAB/LF/CR, DEL and the
/// C1 range are replaced with spaces so pasted text can neither end a bracketed paste early nor smuggle
/// escape sequences to the program. Unbracketed LF and CRLF are normalised to CR.
/// </summary>
public static class PasteEncoder
{
    /// <summary>Returns the bytes to write to the PTY; empty for null or empty text.</summary>
    public static byte[] Encode(string text, bool bracketed)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<byte>();

        char[] scratch = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            int length = Sanitize(text, scratch, newlinesToCr: !bracketed);
            ReadOnlySpan<char> body = scratch.AsSpan(0, length);
            int bodyBytes = Encoding.UTF8.GetByteCount(body);
            ReadOnlySpan<byte> start = "\x1b[200~"u8;
            ReadOnlySpan<byte> end = "\x1b[201~"u8;

            if (!bracketed)
            {
                var plain = new byte[bodyBytes];
                Encoding.UTF8.GetBytes(body, plain);
                return plain;
            }

            var result = new byte[start.Length + bodyBytes + end.Length];
            start.CopyTo(result);
            Encoding.UTF8.GetBytes(body, result.AsSpan(start.Length, bodyBytes));
            end.CopyTo(result.AsSpan(start.Length + bodyBytes));
            return result;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> replacing neutralised controls with
    /// spaces. When <paramref name="newlinesToCr"/> is set, CRLF and LF become a single CR.
    /// <paramref name="destination"/> must be at least as long as <paramref name="source"/>.
    /// </summary>
    /// <returns>Characters written.</returns>
    public static int Sanitize(ReadOnlySpan<char> source, Span<char> destination, bool newlinesToCr)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("Destination must be at least as long as source.", nameof(destination));

        int written = 0;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\n' && newlinesToCr)
            {
                destination[written++] = '\r';
            }
            else if (c == '\r' && newlinesToCr)
            {
                destination[written++] = '\r';
                if (i + 1 < source.Length && source[i + 1] == '\n')
                    i++;
            }
            else
            {
                destination[written++] = IsNeutralisedControl(c) ? ' ' : c;
            }
        }

        return written;
    }

    /// <summary>True for C0 (except TAB/LF/CR), DEL and C1 characters.</summary>
    public static bool IsNeutralisedControl(char c) =>
        (c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0x7F || (c >= '\u0080' && c <= '\u009F');
}
