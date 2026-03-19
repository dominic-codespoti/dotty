using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Parses SGR parameter lists and produces updated cell attributes.
/// </summary>
public static class SgrParser
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
                case 7:
                    attributes.Inverse = true;
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
                    hasMore = enumerator.MoveNext();
                    break;
                case 27:
                    attributes.Inverse = false;
                    hasMore = enumerator.MoveNext();
                    break;
                case 39:
                    attributes.Foreground = null;
                    hasMore = enumerator.MoveNext();
                    break;
                case 49:
                    attributes.Background = null;
                    hasMore = enumerator.MoveNext();
                    break;
                case 59:
                    attributes.UnderlineColor = null;
                    hasMore = enumerator.MoveNext();
                    break;
                case 38:
                    hasMore = TryParseExtendedColor(ref enumerator, out var fg);
                    if (hasMore || fg.HasValue)
                    {
                        if (fg.HasValue) attributes.Foreground = fg;
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                case 48:
                    hasMore = TryParseExtendedColor(ref enumerator, out var bg);
                    if (hasMore || bg.HasValue)
                    {
                        if (bg.HasValue) attributes.Background = bg;
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                case 58:
                    hasMore = TryParseExtendedColor(ref enumerator, out var ul);
                    if (hasMore || ul.HasValue)
                    {
                        if (ul.HasValue) attributes.UnderlineColor = ul;
                    }
                    else
                    {
                        hasMore = enumerator.MoveNext();
                    }
                    break;
                default:
                    if (SgrColor.TryFromAnsiCode(code, out var fgColor))
                    {
                        attributes.Foreground = fgColor;
                    }
                    else if (SgrColor.TryFromBackgroundCode(code, out var bgColor))
                    {
                        attributes.Background = bgColor;
                    }
                    hasMore = enumerator.MoveNext();
                    break;
            }
        }

        return attributes;
    }

    private static bool TryParseExtendedColor(ref ParametersEnumerator enumerator, out SgrColor? color)
    {
        color = null;
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
                        color = SgrColor.FromRgb((byte)r, (byte)g, (byte)b);
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
                if (SgrColor.TryFrom256(idx, out var c))
                {
                    color = c;
                }
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
