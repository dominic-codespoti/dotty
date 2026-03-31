using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Parses SGR parameter lists and produces updated cell attributes.
/// Uses zero-allocation ARGB colors.
/// </summary>
public static class SgrParserArgb
{
    public static CellAttributes Apply(ReadOnlySpan<char> parameters, in CellAttributes current)
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
                    attributes.Underline = true;
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
                    attributes.DoubleUnderline = true;
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
                    attributes.Underline = false;
                    attributes.DoubleUnderline = false;
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
                    attributes.Foreground = default;
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
                    attributes.UnderlineColor = default;
                    hasMore = enumerator.MoveNext();
                    break;
                case 38:
                    hasMore = TryParseExtendedColor(ref enumerator, out var fg);
                    if (hasMore || !fg.IsEmpty)
                    {
                        if (!fg.IsEmpty) attributes.Foreground = fg;
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                case 48:
                    hasMore = TryParseExtendedColor(ref enumerator, out var bg);
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
                    hasMore = TryParseExtendedColor(ref enumerator, out var ul);
                    if (hasMore || !ul.IsEmpty)
                    {
                        if (!ul.IsEmpty) attributes.UnderlineColor = ul;
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                default:
                    var fgColor = SgrColorArgb.FromAnsiCode(code);
                    if (!fgColor.IsEmpty)
                    {
                        attributes.Foreground = fgColor;
                    }
                    else if (SgrColorArgb.TryFromBackgroundCode(code, out var bgColor))
                    {
                        attributes.Background = bgColor;
                    }
                    hasMore = enumerator.MoveNext();
                    break;
            }
        }

        return attributes;
    }

    private static bool TryParseExtendedColor(ref ParametersEnumerator enumerator, out SgrColorArgb color)
    {
        color = default;
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
                color = SgrColorArgb.From256(idx);
                return enumerator.MoveNext();
            }
            return false;
        }

        return enumerator.MoveNext();
    }

    private ref struct ParametersEnumerator
    {
        private ReadOnlySpan<char> _span;
        public int Current { get; private set; }

        public ParametersEnumerator(ReadOnlySpan<char> span)
        {
            _span = span;
            Current = -1;
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
            if (slice.IsEmpty)
            {
                Current = 0;
                return;
            }

            int val = 0;
            for (int i = 0; i < slice.Length; i++)
            {
                char c = slice[i];
                if (c >= '0' && c <= '9')
                {
                    val = val * 10 + (c - '0');
                }
                else
                {
                    Current = 0; // fallback just in case
                    return;
                }
            }
            Current = val;
        }
    }
}
