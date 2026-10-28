using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Unicode;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Strict, allocation-light OSC 7 (<c>file://host/path</c>) parser. Pure: no filesystem access,
/// never throws, and platform rules are selected by argument so they are testable everywhere.
/// </summary>
public static class Osc7WorkingDirectory
{
    /// <summary>Maximum decoded path length in UTF-16 chars.</summary>
    public const int MaxPathLength = 4096;

    /// <summary>Worst case payload: a 3-byte BMP char percent-encoded is 9 payload chars.</summary>
    public const int MaxPayloadLength = 9 * MaxPathLength + 16;

    private const int MaxDecodedBytes = 3 * MaxPathLength;
    private const string Prefix = "file://";

    private static readonly Lazy<IReadOnlyList<string>> s_defaultLocalHostNames = new(BuildDefaultLocalHostNames);

    /// <summary>Names (besides localhost/loopback) that denote this machine.</summary>
    public static IReadOnlyList<string> DefaultLocalHostNames => s_defaultLocalHostNames.Value;

    private static IReadOnlyList<string> BuildDefaultLocalHostNames()
    {
        var names = new List<string>(2);
        try { AddName(names, Environment.MachineName); } catch (InvalidOperationException) { }
        try { AddName(names, Dns.GetHostName()); } catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidOperationException) { }
        return names;
    }

    private static void AddName(List<string> names, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        foreach (string existing in names)
            if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) return;
        names.Add(name);
    }

    /// <summary>Parses an OSC 7 payload (without the leading <c>7;</c>) into a normalized local directory path.</summary>
    public static bool TryParse(ReadOnlySpan<char> payload, bool windows, IReadOnlyList<string> localHostNames, out string path)
    {
        path = "";
        if (payload.Length == 0 || payload.Length > MaxPayloadLength)
            return false;

        // 2. Raw scan.
        foreach (char c in payload)
        {
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F) || c == '\uFFFD' || c == '?' || c == '#'
                || (windows && c == '\\'))
                return false;
        }

        // 3. Scheme.
        if (!payload.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        ReadOnlySpan<char> rest = payload[Prefix.Length..];

        // 4. Authority.
        int slash = rest.IndexOf('/');
        if (slash < 0)
            return false;
        ReadOnlySpan<char> host = rest[..slash];
        ReadOnlySpan<char> rawPath = rest[slash..];

        // 5. Host policy.
        if (!IsLocalHost(host, localHostNames))
            return false;

        // 6. Strict percent-decode into UTF-8 bytes.
        Span<byte> bytes = stackalloc byte[MaxDecodedBytes];
        int count = 0;
        for (int i = 0; i < rawPath.Length;)
        {
            char c = rawPath[i];
            if (c == '%')
            {
                if (i + 2 >= rawPath.Length)
                    return false;
                int hi = HexValue(rawPath[i + 1]);
                int lo = HexValue(rawPath[i + 2]);
                if (hi < 0 || lo < 0 || count >= bytes.Length)
                    return false;
                bytes[count++] = (byte)((hi << 4) | lo);
                i += 3;
            }
            else if (c < 0x80)
            {
                if (count >= bytes.Length) return false;
                bytes[count++] = (byte)c;
                i++;
            }
            else
            {
                if (Rune.DecodeFromUtf16(rawPath[i..], out Rune rune, out int consumed) != OperationStatus.Done
                    || count + rune.Utf8SequenceLength > bytes.Length)
                    return false;
                count += rune.EncodeToUtf8(bytes[count..]);
                i += consumed;
            }
        }

        ReadOnlySpan<byte> decodedBytes = bytes[..count];
        if (!Utf8.IsValid(decodedBytes))
            return false;
        int charCount = Encoding.UTF8.GetCharCount(decodedBytes);
        if (charCount == 0 || charCount > MaxPathLength)
            return false;
        Span<char> decoded = stackalloc char[MaxPathLength];
        charCount = Encoding.UTF8.GetChars(decodedBytes, decoded);
        decoded = decoded[..charCount];

        // 7. Post-decode scan.
        foreach (char c in decoded)
        {
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F) || c == '\uFFFD')
                return false;
        }

        // 8/9. Platform rules and normalization.
        Span<char> output = stackalloc char[MaxPathLength + 2];
        int length;
        ReadOnlySpan<char> segments;
        char separator;
        int rootLength;
        if (windows)
        {
            if (decoded.Length < 3 || decoded[0] != '/' || !char.IsAsciiLetter(decoded[1]) || decoded[2] != ':')
                return false;
            if (decoded.Length > 3 && decoded[3] != '/')
                return false;
            segments = decoded[3..];
            foreach (char c in segments)
            {
                if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\')
                    return false;
            }
            output[0] = decoded[1];
            output[1] = ':';
            rootLength = 2;
            separator = '\\';
        }
        else
        {
            if (decoded[0] != '/')
                return false;
            segments = decoded;
            rootLength = 0;
            separator = '/';
        }

        length = rootLength;
        while (!segments.IsEmpty)
        {
            int next = segments.IndexOf('/');
            ReadOnlySpan<char> segment = next < 0 ? segments : segments[..next];
            segments = next < 0 ? default : segments[(next + 1)..];
            if (segment.IsEmpty || segment.SequenceEqual("."))
                continue;
            if (segment.SequenceEqual(".."))
            {
                int last = output[rootLength..length].LastIndexOf(separator);
                length = last < 0 ? rootLength : rootLength + last;
                continue;
            }
            output[length++] = separator;
            segment.CopyTo(output[length..]);
            length += segment.Length;
        }
        if (length == rootLength)
            output[length++] = separator;

        path = new string(output[..length]);
        return true;
    }

    private static bool IsLocalHost(ReadOnlySpan<char> host, IReadOnlyList<string> localHostNames)
    {
        if (host.IsEmpty)
            return true;
        if (host.SequenceEqual("[::1]"))
            return true;
        if (host.IndexOfAny('@', '%', ':') >= 0)
            return false;
        if (host[^1] == '.')
            host = host[..^1];
        if (host.IsEmpty)
            return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.SequenceEqual("127.0.0.1"))
            return true;
        for (int i = 0; i < localHostNames.Count; i++)
        {
            if (host.Equals(localHostNames[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
