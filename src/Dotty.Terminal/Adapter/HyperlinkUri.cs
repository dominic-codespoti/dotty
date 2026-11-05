using System;

namespace Dotty.Terminal.Adapter;

/// <summary>Result of <see cref="HyperlinkUri.Validate"/>.</summary>
public enum HyperlinkUriStatus
{
    Ok,
    Empty,
    TooLong,
    /// <summary>C0/C1 controls, DEL, or Unicode line/paragraph separators.</summary>
    ControlChar,
    /// <summary>Bidi/invisible format characters or unpaired surrogates.</summary>
    FormatChar,
}

/// <summary>
/// Allocation-free structural validation for OSC 8 hyperlink targets. This is the
/// ingest gate (nothing that fails is ever stored) and the first step of the
/// activation pipeline. It never strips or rewrites input: a bad URI is rejected whole.
/// Scheme filtering deliberately lives at activation time, not here.
/// </summary>
public static class HyperlinkUri
{
    /// <summary>Maximum accepted URI length in UTF-16 code units.</summary>
    public const int MaxLength = 8192;

    /// <summary>Maximum accepted length of the OSC 8 <c>id=</c> parameter value.</summary>
    public const int MaxIdLength = 250;

    /// <summary>
    /// Validates a URI. Space (U+0020) is allowed (it is percent-escaped when the
    /// URI is canonicalized at activation time).
    /// </summary>
    public static HyperlinkUriStatus Validate(ReadOnlySpan<char> uri)
    {
        if (uri.IsEmpty)
            return HyperlinkUriStatus.Empty;
        if (uri.Length > MaxLength)
            return HyperlinkUriStatus.TooLong;

        for (int i = 0; i < uri.Length; i++)
        {
            char c = uri[i];
            if (c >= 0x20 && c < 0x7F)
                continue;

            if (c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == '\u2028' || c == '\u2029')
                return HyperlinkUriStatus.ControlChar;

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < uri.Length && char.IsLowSurrogate(uri[i + 1]))
                {
                    i++;
                    continue;
                }
                return HyperlinkUriStatus.FormatChar;
            }

            if (char.IsLowSurrogate(c) || IsFormatChar(c))
                return HyperlinkUriStatus.FormatChar;
        }

        return HyperlinkUriStatus.Ok;
    }

    /// <summary>
    /// Splits an OSC 8 payload (<c>params;uri</c>, with the leading <c>8;</c> already
    /// consumed). <paramref name="id"/> is the value of the <c>id=</c> parameter (empty
    /// when absent). The URI is everything after the first <c>;</c> and may itself
    /// contain semicolons. Returns false when there is no separator, when the params
    /// contain control/format characters, or when the id exceeds
    /// <see cref="MaxIdLength"/>. The URI part is not validated here.
    /// </summary>
    public static bool TryParseOsc8Payload(
        ReadOnlySpan<char> payload,
        out ReadOnlySpan<char> id,
        out ReadOnlySpan<char> uri)
    {
        id = default;
        uri = default;

        int semi = payload.IndexOf(';');
        if (semi < 0)
            return false;

        ReadOnlySpan<char> parameters = payload.Slice(0, semi);
        for (int i = 0; i < parameters.Length; i++)
        {
            char c = parameters[i];
            if (c < 0x20 || (c >= 0x7F && c <= 0x9F) || c == '\u2028' || c == '\u2029'
                || char.IsSurrogate(c) || IsFormatChar(c))
                return false;
        }

        ReadOnlySpan<char> foundId = default;
        ReadOnlySpan<char> rest = parameters;
        while (!rest.IsEmpty)
        {
            int colon = rest.IndexOf(':');
            ReadOnlySpan<char> pair = colon >= 0 ? rest.Slice(0, colon) : rest;
            rest = colon >= 0 ? rest.Slice(colon + 1) : default;
            if (pair.StartsWith("id=", StringComparison.Ordinal))
            {
                foundId = pair.Slice(3);
                break;
            }
        }

        if (foundId.Length > MaxIdLength)
            return false;

        id = foundId;
        uri = payload.Slice(semi + 1);
        return true;
    }

    private static bool IsFormatChar(char c) =>
        c == '\u00AD'
        || (c >= '\u200B' && c <= '\u200F')
        || (c >= '\u202A' && c <= '\u202E')
        || (c >= '\u2060' && c <= '\u2064')
        || (c >= '\u2066' && c <= '\u2069')
        || c == '\uFEFF';
}
