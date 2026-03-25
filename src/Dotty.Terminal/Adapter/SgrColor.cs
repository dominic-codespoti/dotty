using System;
using System.Collections.Concurrent;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Represents an ANSI SGR color in hex form and offers helpers to translate standard codes.
/// </summary>
public readonly record struct SgrColor(string Hex)
{
    private static readonly ConcurrentDictionary<uint, string> _rgbCache = new();

    public bool IsEmpty => string.IsNullOrEmpty(Hex);

    public override string ToString() => Hex;

    public uint ToArgb()
    {
        if (string.IsNullOrEmpty(Hex)) return 0;
        if (Hex.StartsWith("#") && Hex.Length == 7)
        {
            if (uint.TryParse(Hex.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            {
                return 0xFF000000 | rgb;
            }
        }
        return 0;
    }


    public static SgrColor FromRgb(byte r, byte g, byte b)
    {
        uint key = (uint)((r << 16) | (g << 8) | b);
        if (!_rgbCache.TryGetValue(key, out string? hex))
        {
            hex = $"#{r:X2}{g:X2}{b:X2}";
            _rgbCache.TryAdd(key, hex);
        }
        return new SgrColor(hex);
    }

    public static bool TryFromAnsiCode(int code, out SgrColor color)
    {
        color = code switch
        {
            30 => new("#000000"),
            31 => new("#AA0000"),
            32 => new("#00AA00"),
            33 => new("#AA5500"),
            34 => new("#0000AA"),
            35 => new("#AA00AA"),
            36 => new("#00AAAA"),
            37 => new("#AAAAAA"),
            90 => new("#555555"),
            91 => new("#FF5555"),
            92 => new("#55FF55"),
            93 => new("#FFFF55"),
            94 => new("#5555FF"),
            95 => new("#FF55FF"),
            96 => new("#55FFFF"),
            97 => new("#FFFFFF"),
            _ => default,
        };
        return !color.IsEmpty;
    }

    public static bool TryFromBackgroundCode(int code, out SgrColor color)
    {
        if (code is >= 40 and <= 47)
        {
            return TryFromAnsiCode(code - 10, out color);
        }
        if (code is >= 100 and <= 107)
        {
            return TryFromAnsiCode(code - 10, out color);
        }

        color = default;
        return false;
    }

    public static bool TryFrom256(int idx, out SgrColor color)
    {
        color = default;
        if (idx < 0 || idx > 255)
        {
            return false;
        }

        if (idx <= 15)
        {
            int code = idx < 8 ? 30 + idx : 90 + (idx - 8);
            return TryFromAnsiCode(code, out color);
        }

        if (idx >= 16 && idx <= 231)
        {
            int c = idx - 16;
            int r = c / 36;
            int g = (c / 6) % 6;
            int b = c % 6;
            int R = r == 0 ? 0 : 55 + r * 40;
            int G = g == 0 ? 0 : 55 + g * 40;
            int B = b == 0 ? 0 : 55 + b * 40;
            color = FromRgb((byte)R, (byte)G, (byte)B);
            return true;
        }

        int gray = 8 + (idx - 232) * 10;
        gray = Math.Clamp(gray, 0, 255);
        color = FromRgb((byte)gray, (byte)gray, (byte)gray);
        return true;
    }
}
