using System;
using System.Collections.Concurrent;

namespace Dotty.Terminal.Adapter;

/// <summary>
/// Zero-allocation SGR color representation using uint ARGB values instead of hex strings.
/// Avoids string allocations and provides O(1) equality checks.
/// </summary>
public readonly record struct SgrColorArgb(uint Argb)
{
    public bool IsEmpty => Argb == 0;
    
    public byte A => (byte)(Argb >> 24);
    public byte R => (byte)(Argb >> 16);
    public byte G => (byte)(Argb >> 8);
    public byte B => (byte)Argb;

    public static SgrColorArgb FromRgb(byte r, byte g, byte b)
    {
        return new SgrColorArgb(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b);
    }

    public static SgrColorArgb FromAnsiCode(int code)
    {
        return code switch
        {
            30 => new(0xFF000000u), // Black
            31 => new(0xFFAA0000u), // Red
            32 => new(0xFF00AA00u), // Green
            33 => new(0xFFAA5500u), // Yellow
            34 => new(0xFF0000AAu), // Blue
            35 => new(0xFFAA00AAu), // Magenta
            36 => new(0xFF00AAAAu), // Cyan
            37 => new(0xFFAAAAAAu), // White
            90 => new(0xFF555555u), // Bright Black
            91 => new(0xFFFF5555u), // Bright Red
            92 => new(0xFF55FF55u), // Bright Green
            93 => new(0xFFFFFF55u), // Bright Yellow
            94 => new(0xFF5555FFu), // Bright Blue
            95 => new(0xFFFF55FFu), // Bright Magenta
            96 => new(0xFF55FFFFu), // Bright Cyan
            97 => new(0xFFFFFFFFu), // Bright White
            _ => default,
        };
    }

    public static bool TryFromBackgroundCode(int code, out SgrColorArgb color)
    {
        if (code is >= 40 and <= 47)
        {
            color = FromAnsiCode(code - 10);
            return !color.IsEmpty;
        }
        if (code is >= 100 and <= 107)
        {
            color = FromAnsiCode(code - 10);
            return !color.IsEmpty;
        }

        color = default;
        return false;
    }

    // 256-color palette cache - lazily initialized, but first 16 can be overridden with theme colors
    private static SgrColorArgb[] _palette256 = InitializePalette256();

    /// <summary>
    /// Sets the first 16 ANSI colors (indices 0-15) from a theme palette.
    /// Call this during app startup to apply the user's color theme.
    /// </summary>
    /// <param name="ansiColors">Array of 16 ARGB colors: 0-7 normal, 8-15 bright</param>
    public static void SetAnsiPalette(uint[] ansiColors)
    {
        if (ansiColors?.Length != 16)
            throw new ArgumentException("ANSI palette must have exactly 16 colors", nameof(ansiColors));
        
        // Update the first 16 entries with theme colors
        for (int i = 0; i < 16; i++)
        {
            _palette256[i] = new SgrColorArgb(ansiColors[i]);
        }
    }

    private static SgrColorArgb[] InitializePalette256()
    {
        var palette = new SgrColorArgb[256];
        
        // First 16 colors are ANSI
        for (int i = 0; i < 16; i++)
        {
            int code = i < 8 ? 30 + i : 90 + (i - 8);
            palette[i] = FromAnsiCode(code);
        }

        // 16-231: 6x6x6 color cube
        for (int idx = 16; idx <= 231; idx++)
        {
            int c = idx - 16;
            int r = c / 36;
            int g = (c / 6) % 6;
            int b = c % 6;
            int R = r == 0 ? 0 : 55 + r * 40;
            int G = g == 0 ? 0 : 55 + g * 40;
            int B = b == 0 ? 0 : 55 + b * 40;
            palette[idx] = FromRgb((byte)R, (byte)G, (byte)B);
        }

        // 232-255: Grayscale ramp
        for (int idx = 232; idx < 256; idx++)
        {
            int gray = 8 + (idx - 232) * 10;
            gray = Math.Clamp(gray, 0, 255);
            palette[idx] = FromRgb((byte)gray, (byte)gray, (byte)gray);
        }

        return palette;
    }

    public static SgrColorArgb From256(int idx)
    {
        if ((uint)idx > 255) return default;
        return _palette256[idx];
    }

    public static bool TryFrom256(int idx, out SgrColorArgb color)
    {
        if ((uint)idx > 255)
        {
            color = default;
            return false;
        }
        color = _palette256[idx];
        return true;
    }

    /// <summary>
    /// Converts to hex string for backward compatibility (renders, etc).
    /// Use sparingly - creates string allocation.
    /// </summary>
    public string ToHexString()
    {
        return $"#{R:X2}{G:X2}{B:X2}";
    }
}
