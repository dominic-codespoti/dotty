using System;

namespace Dotty.Runtime.Hyperlinks;

/// <summary>
/// Detects OSC 8 links whose visible text names a different web host than the real target
/// (<c>https://paypal.com</c> pointing at <c>https://evil.test</c>). Bare file-name-like text
/// and text that is not recognizably a web address are deliberately not compared.
/// </summary>
public static class HyperlinkTextMismatch
{
    /// <summary>True when <paramref name="displayText"/> names a host unrelated to <paramref name="target"/>.</summary>
    public static bool IsMismatch(string? displayText, Uri target)
    {
        if (string.IsNullOrWhiteSpace(displayText))
            return false;
        if (!IsWeb(target))
            return false;

        string? textHost = ExtractHost(displayText.AsSpan().Trim());
        if (textHost is null)
            return false;

        string targetHost = Normalize(target.IdnHost);
        textHost = Normalize(textHost);
        if (textHost.Length == 0 || targetHost.Length == 0)
            return false;

        return !(string.Equals(textHost, targetHost, StringComparison.Ordinal)
            || textHost.EndsWith("." + targetHost, StringComparison.Ordinal)
            || targetHost.EndsWith("." + textHost, StringComparison.Ordinal));
    }

    private static bool IsWeb(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static string Normalize(string host)
    {
        string h = host.ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h.Substring(4) : h;
    }

    private static string? ExtractHost(ReadOnlySpan<char> text)
    {
        int sep = text.IndexOf("://", StringComparison.Ordinal);
        string candidate;
        if (sep >= 0)
        {
            int start = sep;
            while (start > 0 && IsSchemeChar(text[start - 1]))
                start--;
            int end = sep + 3;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
                end++;
            candidate = text.Slice(start, end - start).ToString();
        }
        else if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            int end = 0;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
                end++;
            candidate = "https://" + text.Slice(0, end).ToString();
        }
        else
        {
            return null;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) && uri.HostNameType == UriHostNameType.Dns
            ? uri.IdnHost
            : null;
    }

    private static bool IsSchemeChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';
}
