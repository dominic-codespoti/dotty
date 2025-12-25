namespace Dotty.App.Services
{
    public static class Defaults
    {
        // Prefer explicit family names discovered on typical Linux installs (fc-list):
        // - JetBrainsMono NF / JetBrainsMono Nerd Font (patched with nerd glyphs)
        // - JetBrainsMono Nerd Font Mono (monospace variant)
        // - SpaceMono Nerd Font / SpaceMono Nerd Font Mono
        // - Material Symbols (Sharp/Rounded) and Noto Sans Symbols for icon coverage
        // Fallback to common monospaced families last.
        // Prefer a patched monospace (Nerd) font first to avoid per-glyph fallbacks for PUA icons.
        // Prioritize SpaceMono patched Nerd monospace to improve coverage for box-drawing/block/PUA glyphs
        public const string DefaultFontStack = "SpaceMono Nerd Font Mono, SpaceMono Nerd Font, JetBrainsMono Nerd Font Mono, JetBrainsMono NF, JetBrainsMono Nerd Font, Material Symbols Sharp, Material Symbols Rounded, Noto Sans Symbols, JetBrains Mono, Cascadia Code, Liberation Mono, Noto Sans Mono, monospace";
        public const string DefaultFontFamily = "SpaceMono Nerd Font Mono";
        public const double DefaultFontSize = 13.0;
        public const string DefaultBackground = "#1E1E1E";
        public const string DefaultForeground = "#D4D4D4";
        public const double DefaultWindowOpacity = 0.92;
        public const string DefaultBackgroundAlpha = "#CC1E1E1E";
    }
}
