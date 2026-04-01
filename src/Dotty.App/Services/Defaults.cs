using System;
using Dotty.App.Configuration;

namespace Dotty.App.Services
{
    /// <summary>
    /// Default values for Dotty terminal emulator.
    /// These values are now primarily defined in the generated Config class.
    /// This class provides backward compatibility and runtime fallbacks.
    /// </summary>
    public static class Defaults
    {
        // Use generated config values as the source of truth
        public static string DefaultFontStack => Generated.Config.FontFamily;
        public static double DefaultFontSize => Generated.Config.FontSize;
        public static string DefaultBackground => ConfigBridge.ToHex(Generated.Config.Background);
        public static string DefaultForeground => ConfigBridge.ToHex(Generated.Config.Foreground);

        /// <summary>
        /// Gets the initial font size, checking environment variable override first.
        /// </summary>
        public static double GetInitialFontSize()
        {
            var env = Environment.GetEnvironmentVariable("DOTTY_FONT_SIZE");
            if (!string.IsNullOrWhiteSpace(env) &&
                double.TryParse(env, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                return parsed;
            }

            return DefaultFontSize;
        }

        /// <summary>
        /// Gets the cell padding from configuration.
        /// </summary>
        public static double GetCellPadding()
        {
            return Generated.Config.CellPadding;
        }

        /// <summary>
        /// Gets the scrollback lines from configuration.
        /// </summary>
        public static int GetScrollbackLines()
        {
            return Generated.Config.ScrollbackLines;
        }

        /// <summary>
        /// Gets the inactive tab destroy delay from configuration.
        /// </summary>
        public static int GetInactiveTabDestroyDelayMs()
        {
            return Generated.Config.InactiveTabDestroyDelayMs;
        }
    }

    /// <summary>
    /// Public constants representing default values used by the config generator.
    /// These should match the DefaultDottyConfig values in Configuration/DefaultConfig.cs
    /// </summary>
    public static class DefaultConstants
    {
        public const string FontFamily = "JetBrainsMono Nerd Font Mono, JetBrainsMono NF, JetBrainsMono Nerd Font, JetBrains Mono, SpaceMono Nerd Font Mono, SpaceMono Nerd Font, Material Symbols Sharp, Material Symbols Rounded, Noto Sans Symbols, Cascadia Code, Liberation Mono, Noto Sans Mono, monospace";
        public const double FontSize = 15.0;
        public const double CellPadding = 1.5;
        public const int ScrollbackLines = 10000;
        public const int InactiveTabDestroyDelayMs = 5000;
        public const uint SelectionColor = 0xA03385DB;
        public const uint TabBarBackgroundColor = 0xFF1A1A1A;
    }
}
