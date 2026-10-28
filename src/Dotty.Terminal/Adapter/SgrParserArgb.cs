using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Parses SGR parameter lists and produces updated cell attributes.
/// Uses zero-allocation ARGB colors.
/// </summary>
public static class SgrParserArgb
{
    public static CellAttributes Apply(ReadOnlySpan<char> parameters, in CellAttributes current, TerminalPalette palette)
    {
        if (parameters.IsEmpty)
        {
            return CellAttributes.Default;
        }

        var enumerator = new ParametersEnumerator(parameters);
        if (!enumerator.MoveNext())
        {
            return CellAttributes.Default;
        }

        var attributes = current;
        bool hasMore = true;

        while (hasMore)
        {
            int code = enumerator.Current;

            switch (code)
            {
                case 0:
                    attributes = CellAttributes.Default;
                    hasMore = enumerator.MoveNext();
                    break;
                case 1:
                    attributes.Bold = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 2:
                    attributes.Faint = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 3:
                    attributes.Italic = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 4:
                    // SGR 4 with colon subparameter: 4 → single, 4:3 → curl, 4:4 → dotted, 4:5 → dashed
                    attributes.UnderlineStyle = enumerator.SubParameter switch
                    {
                        3 => UnderlineStyle.Curl,
                        4 => UnderlineStyle.Dotted,
                        5 => UnderlineStyle.Dashed,
                        _ => UnderlineStyle.Single,
                    };
                    hasMore = enumerator.MoveNext();
                    break;
                case 5:
                    attributes.SlowBlink = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 7:
                    attributes.Inverse = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 8:
                    attributes.Invisible = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 9:
                    attributes.Strikethrough = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 21:
                    attributes.UnderlineStyle = UnderlineStyle.Double;
                    hasMore = enumerator.MoveNext();
                    break;
                case 22:
                    attributes.Bold = false;
                    attributes.Faint = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 23:
                    attributes.Italic = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 24:
                    attributes.UnderlineStyle = UnderlineStyle.None;
                    hasMore = enumerator.MoveNext();
                    break;
                case 25:
                    attributes.SlowBlink = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 27:
                    attributes.Inverse = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 28:
                    attributes.Invisible = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 29:
                    attributes.Strikethrough = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 39:
                    attributes.ClearForeground();
                    hasMore = enumerator.MoveNext();
                    break;
                case 49:
                    attributes.Background = default;
                    hasMore = enumerator.MoveNext();
                    break;
                case 53:
                    attributes.Overline = true;
                    hasMore = enumerator.MoveNext();
                    break;
                case 55:
                    attributes.Overline = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 59:
                    attributes.ClearUnderlineColor();
                    hasMore = enumerator.MoveNext();
                    break;
                case 38:
                    hasMore = TryParseExtendedColor(ref enumerator, palette, out var fg, out int fgIndex);
                    if (hasMore || !fg.IsEmpty)
                    {
                        if (!fg.IsEmpty)
                        {
                            if (fgIndex >= 0) attributes.SetForegroundIndexed(fgIndex, fg);
                            else attributes.SetForegroundRgb(fg);
                        }
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                case 48:
                    hasMore = TryParseExtendedColor(ref enumerator, palette, out var bg, out _);
                    if (hasMore || !bg.IsEmpty)
                    {
                        if (!bg.IsEmpty) attributes.Background = bg;
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                case 58:
                    hasMore = TryParseExtendedColor(ref enumerator, palette, out var ul, out int ulIndex);
                    if (hasMore || !ul.IsEmpty)
                    {
                        if (!ul.IsEmpty)
                        {
                            if (ulIndex >= 0) attributes.SetUnderlineColorIndexed(ulIndex, ul);
                            else attributes.SetUnderlineColorRgb(ul);
                        }
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                default:
                    var fgColor = SgrColorArgb.FromAnsiCode(code, palette);
                    if (!fgColor.IsEmpty)
                    {
                        attributes.SetForegroundIndexed(code <= 37 ? code - 30 : code - 90 + 8, fgColor);
                    }
                    else if (SgrColorArgb.TryFromBackgroundCode(code, palette, out var bgColor))
                    {
                        attributes.Background = bgColor;
                    }
                    hasMore = enumerator.MoveNext();
                    break;
            }
        }

        return attributes;
    }

    private static bool TryParseExtendedColor(ref ParametersEnumerator enumerator, TerminalPalette palette, out SgrColorArgb color, out int paletteIndex)
    {
        color = default;
        paletteIndex = -1;
        if (enumerator.HasSubText)
        {
            // Colon form (38:5:n, 38:2:r:g:b, 38:2:cs:r:g:b): the whole colour sits in this one parameter.
            ParseColonColor(enumerator.SubText, palette, out color, out paletteIndex);
            return enumerator.MoveNext();
        }
        if (!enumerator.MoveNext()) return false;

        int mode = enumerator.Current;
        if (mode == 2)
        {
            if (enumerator.MoveNext())
            {
                int r = enumerator.Current;
                if (enumerator.MoveNext())
                {
                    int g = enumerator.Current;
                    if (enumerator.MoveNext())
                    {
                        int b = enumerator.Current;
                        color = SgrColorArgb.FromRgb((byte)r, (byte)g, (byte)b);
                        return enumerator.MoveNext();
                    }
                }
            }
            return false;
        }

        if (mode == 5)
        {
            if (enumerator.MoveNext())
            {
                int idx = enumerator.Current;
                color = SgrColorArgb.From256(idx, palette);
                if (!color.IsEmpty) paletteIndex = idx;
                return enumerator.MoveNext();
            }
            return false;
        }

        return enumerator.MoveNext();
    }

    /// <summary>
    /// Parses the text after the first colon of an extended colour parameter: <c>5:n</c>, <c>2:r:g:b</c> or
    /// <c>2:cs:r:g:b</c> (the colour-space slot may be empty: <c>2::r:g:b</c>). Malformed input (unknown mode,
    /// missing or non-numeric components, a value above 255) yields no colour.
    /// </summary>
    private static void ParseColonColor(ReadOnlySpan<char> text, TerminalPalette palette, out SgrColorArgb color, out int paletteIndex)
    {
        color = default;
        paletteIndex = -1;
        Span<int> parts = stackalloc int[8];
        int count = 0;
        while (true)
        {
            int colon = text.IndexOf(':');
            ReadOnlySpan<char> part = colon < 0 ? text : text.Slice(0, colon);
            if (count == parts.Length) return;
            if (!TryParseComponent(part, out parts[count++])) return;
            if (colon < 0) break;
            text = text.Slice(colon + 1);
        }

        if (parts[0] == 5 && count == 2)
        {
            int idx = parts[1];
            if (idx < 0 || idx > 255) return;
            color = SgrColorArgb.From256(idx, palette);
            if (!color.IsEmpty) paletteIndex = idx;
        }
        else if (parts[0] == 2 && (count == 4 || count == 5))
        {
            int first = count - 3;
            int r = parts[first], g = parts[first + 1], b = parts[first + 2];
            if (r < 0 || g < 0 || b < 0 || r > 255 || g > 255 || b > 255) return;
            color = SgrColorArgb.FromRgb((byte)r, (byte)g, (byte)b);
        }
    }

    // An empty component is 0 (as in "2::r:g:b"); anything else must be decimal digits.
    private static bool TryParseComponent(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        for (int i = 0; i < part.Length; i++)
        {
            char c = part[i];
            if (c < '0' || c > '9') return false;
            value = value * 10 + (c - '0');
            if (value > 100000) value = 100000;
        }
        return true;
    }

    private ref struct ParametersEnumerator
    {
        private ReadOnlySpan<char> _span;
        public int Current { get; private set; }
        public int SubParameter { get; private set; }

        /// <summary>True when the current parameter contained a colon.</summary>
        public bool HasSubText { get; private set; }

        /// <summary>Text after the first colon of the current parameter (valid when <see cref="HasSubText"/>).</summary>
        public ReadOnlySpan<char> SubText { get; private set; }

        public ParametersEnumerator(ReadOnlySpan<char> span)
        {
            _span = span;
            Current = -1;
            SubParameter = -1;
        }

        public bool MoveNext()
        {
            if (_span.IsEmpty)
            {
                return false;
            }

            int index = _span.IndexOf(';');
            if (index == -1)
            {
                ParseCurrent(_span);
                _span = ReadOnlySpan<char>.Empty;
                return true;
            }

            ParseCurrent(_span.Slice(0, index));
            _span = _span.Slice(index + 1);
            return true;
        }

        private void ParseCurrent(ReadOnlySpan<char> slice)
        {
            SubParameter = -1;
            HasSubText = false;
            SubText = default;
            if (slice.IsEmpty)
            {
                Current = 0;
                return;
            }

            // Check for colon subparameter (e.g. "4:3" → code=4, sub=3).
            int colon = slice.IndexOf(':');
            ReadOnlySpan<char> main = colon >= 0 ? slice.Slice(0, colon) : slice;
            ReadOnlySpan<char> sub = colon >= 0 ? slice.Slice(colon + 1) : default;
            HasSubText = colon >= 0;
            SubText = sub;

            int val = 0;
            for (int i = 0; i < main.Length; i++)
            {
                char c = main[i];
                if (c >= '0' && c <= '9')
                {
                    val = val * 10 + (c - '0');
                }
                else
                {
                    Current = 0;
                    return;
                }
            }
            Current = val;

            if (!sub.IsEmpty)
            {
                int subVal = 0;
                for (int i = 0; i < sub.Length; i++)
                {
                    char c = sub[i];
                    if (c >= '0' && c <= '9')
                        subVal = subVal * 10 + (c - '0');
                    else
                        return;
                }
                SubParameter = subVal;
            }
        }
    }
}
