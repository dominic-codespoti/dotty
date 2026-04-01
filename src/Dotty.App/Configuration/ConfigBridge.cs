// This file provides integration between the generated Config class
// and the Avalonia UI framework.

#nullable enable

using System;
using Avalonia.Media;
using Avalonia;
using Dotty.Abstractions.Config;
using Thickness = Avalonia.Thickness;

namespace Dotty.App.Configuration;

/// <summary>
/// Helper class to convert generated configuration values to Avalonia types.
/// </summary>
public static class ConfigBridge
{
    /// <summary>
    /// Gets the font family from the generated configuration.
    /// </summary>
    public static FontFamily GetFontFamily()
    {
        var family = Generated.Config.FontFamily;
        return new FontFamily(family);
    }

    /// <summary>
    /// Gets the font size from the generated configuration.
    /// </summary>
    public static double GetFontSize()
    {
        return Generated.Config.FontSize;
    }

    /// <summary>
    /// Gets the background color as an Avalonia Color.
    /// </summary>
    public static Color GetBackgroundColor()
    {
        return ToColor(Generated.Config.Background);
    }

    /// <summary>
    /// Gets the foreground color as an Avalonia Color.
    /// </summary>
    public static Color GetForegroundColor()
    {
        return ToColor(Generated.Config.Foreground);
    }

    /// <summary>
    /// Gets the background brush from the generated configuration.
    /// </summary>
    public static IBrush GetBackgroundBrush()
    {
        return new SolidColorBrush(GetBackgroundColor());
    }

    /// <summary>
    /// Gets the foreground brush from the generated configuration.
    /// </summary>
    public static IBrush GetForegroundBrush()
    {
        return new SolidColorBrush(GetForegroundColor());
    }

    /// <summary>
    /// Gets the selection brush from the generated configuration.
    /// </summary>
    public static IBrush GetSelectionBrush()
    {
        return new SolidColorBrush(ToColor(Generated.Config.SelectionColor));
    }

    /// <summary>
    /// Gets the tab bar background brush from the generated configuration.
    /// Falls back to theme's background color if TabBarBackgroundColor is the default.
    /// </summary>
    public static IBrush GetTabBarBackgroundBrush()
    {
        var tabBarColor = global::Dotty.Generated.Config.TabBarBackgroundColor;
        
        // If the tab bar color is the default dark gray (0xFF1A1A1A), 
        // use a darkened version of the theme's background for better integration
        if (tabBarColor == 0xFF1A1A1A)
        {
            var themeBg = global::Dotty.Generated.Config.Background;
            // Darken the theme background slightly for the tab bar
            var darkened = DarkenColor(themeBg, 0.9);
            return new SolidColorBrush(ToColor(darkened));
        }
        
        return new SolidColorBrush(ToColor(tabBarColor));
    }
    
    /// <summary>
    /// Darkens a color by the given factor (0.0-1.0, where lower is darker).
    /// </summary>
    private static uint DarkenColor(uint argb, double factor)
    {
        var a = (byte)((argb >> 24) & 0xFF);
        var r = (byte)((argb >> 16) & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var b = (byte)(argb & 0xFF);
        
        r = (byte)(r * factor);
        g = (byte)(g * factor);
        b = (byte)(b * factor);
        
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    /// <summary>
    /// Gets the window opacity as a double (0.0-1.0) for Avalonia.
    /// </summary>
    public static double GetWindowOpacity()
    {
        return Generated.Config.Opacity / 100.0;
    }

    /// <summary>
    /// Gets the content padding as Avalonia Thickness.
    /// </summary>
    public static Thickness GetContentPadding()
    {
        return new Thickness(
            Generated.Config.ContentPaddingLeft,
            Generated.Config.ContentPaddingTop,
            Generated.Config.ContentPaddingRight,
            Generated.Config.ContentPaddingBottom
        );
    }

    /// <summary>
    /// Gets the cell padding from the generated configuration.
    /// </summary>
    public static double GetCellPadding()
    {
        return Generated.Config.CellPadding;
    }

    /// <summary>
    /// Converts an ARGB uint to Avalonia Color.
    /// </summary>
    public static Color ToColor(uint argb)
    {
        return new Color(
            (byte)((argb >> 24) & 0xFF),  // A
            (byte)((argb >> 16) & 0xFF),  // R
            (byte)((argb >> 8) & 0xFF),   // G
            (byte)(argb & 0xFF)           // B
        );
    }

    /// <summary>
    /// Converts an Avalonia Color to ARGB uint.
    /// </summary>
    public static uint FromColor(Color color)
    {
        return (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
    }

    /// <summary>
    /// Gets a hex string representation of an ARGB color.
    /// </summary>
    public static string ToHex(uint argb)
    {
        var color = ToColor(argb);
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// Converts a hex string to an ARGB uint.
    /// Supports formats: #RRGGBB, #AARRGGBB, RRGGBB, AARRGGBB
    /// </summary>
    public static uint FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new ArgumentException("Hex string cannot be null or empty", nameof(hex));

        var span = hex.AsSpan().Trim();
        
        // Remove # prefix if present
        if (span.Length > 0 && span[0] == '#')
            span = span.Slice(1);

        // Parse based on length
        return span.Length switch
        {
            6 => 0xFF000000u | (uint)Convert.ToInt32(span.ToString(), 16),  // RRGGBB -> AARRGGBB with FF alpha
            8 => (uint)Convert.ToInt32(span.ToString(), 16),                 // AARRGGBB
            _ => throw new ArgumentException($"Invalid hex color format: {hex}. Expected #RRGGBB or #AARRGGBB")
        };
    }

    /// <summary>
    /// Gets a brush from an IColorScheme by index.
    /// Index 0 = Background, 1 = Foreground, 2+ = ANSI colors 0-15
    /// </summary>
    public static IBrush? GetBrushFromScheme(IColorScheme scheme, int index)
    {
        uint color = index switch
        {
            0 => scheme.Background,
            1 => scheme.Foreground,
            2 => scheme.AnsiBlack,
            3 => scheme.AnsiRed,
            4 => scheme.AnsiGreen,
            5 => scheme.AnsiYellow,
            6 => scheme.AnsiBlue,
            7 => scheme.AnsiMagenta,
            8 => scheme.AnsiCyan,
            9 => scheme.AnsiWhite,
            10 => scheme.AnsiBrightBlack,
            11 => scheme.AnsiBrightRed,
            12 => scheme.AnsiBrightGreen,
            13 => scheme.AnsiBrightYellow,
            14 => scheme.AnsiBrightBlue,
            15 => scheme.AnsiBrightMagenta,
            16 => scheme.AnsiBrightCyan,
            17 => scheme.AnsiBrightWhite,
            _ => throw new ArgumentOutOfRangeException(nameof(index), "Index must be 0-17")
        };

        return new SolidColorBrush(ToColor(color));
    }

    /// <summary>
    /// Gets an ANSI color brush from the generated color scheme.
    /// </summary>
    /// <param name="ansiIndex">ANSI color index (0-15)</param>
    public static IBrush GetAnsiColorBrush(int ansiIndex)
    {
        if (ansiIndex < 0 || ansiIndex > 15)
            throw new ArgumentOutOfRangeException(nameof(ansiIndex), "ANSI color index must be 0-15");

        var colors = Generated.Config.Colors;
        uint colorValue = ansiIndex switch
        {
            0 => colors.AnsiBlack,
            1 => colors.AnsiRed,
            2 => colors.AnsiGreen,
            3 => colors.AnsiYellow,
            4 => colors.AnsiBlue,
            5 => colors.AnsiMagenta,
            6 => colors.AnsiCyan,
            7 => colors.AnsiWhite,
            8 => colors.AnsiBrightBlack,
            9 => colors.AnsiBrightRed,
            10 => colors.AnsiBrightGreen,
            11 => colors.AnsiBrightYellow,
            12 => colors.AnsiBrightBlue,
            13 => colors.AnsiBrightMagenta,
            14 => colors.AnsiBrightCyan,
            15 => colors.AnsiBrightWhite,
            _ => colors.Foreground
        };

        return new SolidColorBrush(ToColor(colorValue));
    }
}
