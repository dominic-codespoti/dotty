using System;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Parses SGR parameter lists and produces updated cell attributes.
/// </summary>
public static class SgrParser
{
    public static CellAttributes Apply(ReadOnlySpan<char> parameters, in CellAttributes current)
    {
        var s = parameters.ToString();
        if (string.IsNullOrEmpty(s))
        {
            return CellAttributes.Default;
        }

        var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return CellAttributes.Default;
        }

        var attributes = current;
        int i = 0;
        while (i < parts.Length)
        {
            if (!int.TryParse(parts[i], out var code))
            {
                i++;
                continue;
            }

            switch (code)
            {
                case 0:
                    attributes = CellAttributes.Default;
                    i++;
                    break;
                case 1:
                    attributes.Bold = true;
                    i++;
                    break;
                case 2:
                    attributes.Faint = true;
                    i++;
                    break;
                case 3:
                    attributes.Italic = true;
                    i++;
                    break;
                case 4:
                    attributes.Underline = true;
                    i++;
                    break;
                case 7:
                    attributes.Inverse = true;
                    i++;
                    break;
                case 22:
                    attributes.Bold = false;
                    attributes.Faint = false;
                    i++;
                    break;
                case 23:
                    attributes.Italic = false;
                    i++;
                    break;
                case 24:
                    attributes.Underline = false;
                    i++;
                    break;
                case 27:
                    attributes.Inverse = false;
                    i++;
                    break;
                case 39:
                    attributes.Foreground = null;
                    i++;
                    break;
                case 49:
                    attributes.Background = null;
                    i++;
                    break;
                case 59:
                    attributes.UnderlineColor = null;
                    i++;
                    break;
                case 38:
                    if (TryParseExtendedColor(parts, ref i, out var fg))
                    {
                        attributes.Foreground = fg;
                    }
                    else
                    {
                        i++;
                    }
                    break;
                case 48:
                    if (TryParseExtendedColor(parts, ref i, out var bg))
                    {
                        attributes.Background = bg;
                    }
                    else
                    {
                        i++;
                    }
                    break;
                case 58:
                    if (TryParseExtendedColor(parts, ref i, out var ul))
                    {
                        attributes.UnderlineColor = ul;
                    }
                    else
                    {
                        i++;
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
                    i++;
                    break;
            }
        }

        return attributes;
    }

    private static bool TryParseExtendedColor(string[] parts, ref int index, out SgrColor color)
    {
        color = default;
        if (index + 1 >= parts.Length)
        {
            return false;
        }

        var mode = parts[index + 1];
        if (mode == "2")
        {
            if (index + 4 < parts.Length &&
                byte.TryParse(parts[index + 2], out var r) &&
                byte.TryParse(parts[index + 3], out var g) &&
                byte.TryParse(parts[index + 4], out var b))
            {
                color = SgrColor.FromRgb(r, g, b);
                index += 5;
                return true;
            }
            return false;
        }

        if (mode == "5")
        {
            if (index + 2 < parts.Length && int.TryParse(parts[index + 2], out var idx))
            {
                if (SgrColor.TryFrom256(idx, out color))
                {
                    index += 3;
                    return true;
                }
                return false;
            }
            return false;
        }

        return false;
    }
}
