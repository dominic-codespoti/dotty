using System;
using Dotty.App.Configuration;
using Dotty.Abstractions.Config;

namespace Dotty.App.Services
{
    /// <summary>
    /// Default values for Dotty terminal emulator.
    /// This class now delegates to DottyDefaults from Dotty.Abstractions.Config
    /// for the single source of truth, maintaining backward compatibility.
    /// </summary>
    public static class Defaults
    {
        /// <summary>
        /// Default font stack.
        /// </summary>
        public static string DefaultFontStack => DottyDefaults.FontFamily;

        /// <summary>
        /// Default font size.
        /// </summary>
        public static double DefaultFontSize => DottyDefaults.FontSize;

        /// <summary>
        /// Default background color in hex format.
        /// </summary>
        public static string DefaultBackground => ConfigBridge.ToHex(DottyDefaults.TabBarBackgroundColor);

        /// <summary>
        /// Default foreground color in hex format.
        /// </summary>
        public static string DefaultForeground => ConfigBridge.ToHex(DottyDefaults.SelectionColor);

        /// <summary>
        /// Gets the initial font size, checking environment variable override first.
        /// </summary>
        public static double GetInitialFontSize()
        {
            return DottyDefaults.GetInitialFontSize();
        }

        /// <summary>
        /// Gets the cell padding from configuration.
        /// </summary>
        public static double GetCellPadding()
        {
            return 0.0;
        }

        /// <summary>
        /// Gets the scrollback lines from configuration.
        /// </summary>
        public static int GetScrollbackLines()
        {
            return DottyDefaults.ScrollbackLines;
        }

        /// <summary>
        /// Gets the inactive tab destroy delay from configuration.
        /// </summary>
        public static int GetInactiveTabDestroyDelayMs()
        {
            return DottyDefaults.InactiveTabDestroyDelayMs;
        }
    }

    /// <summary>
    /// Public constants representing default values used by the config generator.
    /// These delegate to DottyDefaults for the single source of truth.
    /// </summary>
    public static class DefaultConstants
    {
        public const string FontFamily = DottyDefaults.FontFamily;
        public const double FontSize = DottyDefaults.FontSize;
        public const double CellPadding = 0.0;
        public const int ScrollbackLines = DottyDefaults.ScrollbackLines;
        public const int InactiveTabDestroyDelayMs = DottyDefaults.InactiveTabDestroyDelayMs;
        public const uint SelectionColor = DottyDefaults.SelectionColor;
        public const uint TabBarBackgroundColor = DottyDefaults.TabBarBackgroundColor;
    }
}
