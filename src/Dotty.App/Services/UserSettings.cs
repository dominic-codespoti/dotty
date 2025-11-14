using System;

namespace Dotty.App.Services
{
    public class UserSettings
    {
    // Default to JetBrains Mono Nerd Font (common Nerd Font name); fallback families are included.
    // If your installed font is named slightly differently, update the settings file at ~/.config/dotty/settings.json
    public string? FontFamily { get; set; } = "JetBrains Mono Nerd Font, Consolas, Menlo, monospace";
        public double FontSize { get; set; } = 13.0;
        public string? Background { get; set; } = "#1E1E1E";
        public string? Foreground { get; set; } = "#D4D4D4";
    }
}
