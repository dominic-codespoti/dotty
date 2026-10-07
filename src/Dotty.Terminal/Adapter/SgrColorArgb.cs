using System;

namespace Dotty.Terminal.Adapter;

/// <summary>Zero-allocation SGR color representation using ARGB values.</summary>
public readonly record struct SgrColorArgb(uint Argb)
{
    public bool IsEmpty => Argb == 0;
    public byte A => (byte)(Argb >> 24);
    public byte R => (byte)(Argb >> 16);
    public byte G => (byte)(Argb >> 8);
    public byte B => (byte)Argb;

    public static SgrColorArgb FromRgb(byte r, byte g, byte b) =>
        new(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b);

    public static SgrColorArgb FromAnsiCode(int code, TerminalPalette palette)
    {
        int index = code switch
        {
            >= 30 and <= 37 => code - 30,
            >= 90 and <= 97 => code - 90 + 8,
            _ => -1,
        };
        return index < 0 ? default : new SgrColorArgb(palette[index]);
    }

    public static bool TryFromBackgroundCode(int code, TerminalPalette palette, out SgrColorArgb color)
    {
        if (code is >= 40 and <= 47) { color = FromAnsiCode(code - 10, palette); return true; }
        if (code is >= 100 and <= 107) { color = FromAnsiCode(code - 10, palette); return true; }
        color = default;
        return false;
    }

    public static SgrColorArgb From256(int index, TerminalPalette palette) =>
        (uint)index < 256 ? new SgrColorArgb(palette[index]) : default;

    public static bool TryFrom256(int index, TerminalPalette palette, out SgrColorArgb color)
    {
        if ((uint)index >= 256) { color = default; return false; }
        color = new SgrColorArgb(palette[index]);
        return true;
    }

    internal static uint[] CreateStockPalette()
    {
        var palette = new uint[256];
        uint[] ansi =
        [
            0xFF000000u, 0xFFAA0000u, 0xFF00AA00u, 0xFFAA5500u,
            0xFF0000AAu, 0xFFAA00AAu, 0xFF00AAAAu, 0xFFAAAAAAu,
            0xFF555555u, 0xFFFF5555u, 0xFF55FF55u, 0xFFFFFF55u,
            0xFF5555FFu, 0xFFFF55FFu, 0xFF55FFFFu, 0xFFFFFFFFu,
        ];
        ansi.CopyTo(palette, 0);
        for (int index = 16; index <= 231; index++)
        {
            int c = index - 16;
            int r = c / 36, g = (c / 6) % 6, b = c % 6;
            palette[index] = FromRgb((byte)(r == 0 ? 0 : 55 + r * 40),
                (byte)(g == 0 ? 0 : 55 + g * 40), (byte)(b == 0 ? 0 : 55 + b * 40)).Argb;
        }
        for (int index = 232; index < 256; index++)
        {
            byte gray = (byte)Math.Clamp(8 + (index - 232) * 10, 0, 255);
            palette[index] = FromRgb(gray, gray, gray).Argb;
        }
        return palette;
    }

    internal static uint StockColorAt(int index)
    {
        if ((uint)index >= 256) return 0;
        if (index < 16)
        {
            return index switch
            {
                0 => 0xFF000000u,
                1 => 0xFFAA0000u,
                2 => 0xFF00AA00u,
                3 => 0xFFAA5500u,
                4 => 0xFF0000AAu,
                5 => 0xFFAA00AAu,
                6 => 0xFF00AAAAu,
                7 => 0xFFAAAAAAu,
                8 => 0xFF555555u,
                9 => 0xFFFF5555u,
                10 => 0xFF55FF55u,
                11 => 0xFFFFFF55u,
                12 => 0xFF5555FFu,
                13 => 0xFFFF55FFu,
                14 => 0xFF55FFFFu,
                _ => 0xFFFFFFFFu,
            };
        }
        if (index < 232)
        {
            int c = index - 16, r = c / 36, g = (c / 6) % 6, b = c % 6;
            return FromRgb((byte)(r == 0 ? 0 : 55 + r * 40), (byte)(g == 0 ? 0 : 55 + g * 40), (byte)(b == 0 ? 0 : 55 + b * 40)).Argb;
        }
        byte gray = (byte)Math.Clamp(8 + (index - 232) * 10, 0, 255);
        return FromRgb(gray, gray, gray).Argb;
    }

    public string ToHexString() => $"#{R:X2}{G:X2}{B:X2}";
}
