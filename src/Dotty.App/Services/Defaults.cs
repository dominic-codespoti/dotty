using System;

namespace Dotty.App.Services
{
    public static class Defaults
    {
        public const string DefaultFontStack = "JetBrainsMono Nerd Font Mono, JetBrainsMono NF, JetBrainsMono Nerd Font, JetBrains Mono, SpaceMono Nerd Font Mono, SpaceMono Nerd Font, Material Symbols Sharp, Material Symbols Rounded, Noto Sans Symbols, Cascadia Code, Liberation Mono, Noto Sans Mono, monospace";
        public const double DefaultFontSize = 15.0;
        public const string DefaultBackground = "#F2000000";
        public const string DefaultForeground = "#D4D4D4";

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
    }
}
