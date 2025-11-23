namespace Dotty.App.Services
{
    /// <summary>
    /// Hard-coded defaults used while settings are temporarily removed.
    /// We'll reintroduce a settings system later; for now these values are authoritative.
    /// </summary>
    public static class Defaults
    {
        // A comma-separated preference list; ResolveFontFamily will pick the first installed.
        public const string DefaultFontStack = "JetBrainsMono Nerd Font, JetBrains Mono, Cascadia Code, Liberation Mono, Noto Sans Mono, monospace";
        public const string DefaultFontFamily = "JetBrainsMono Nerd Font";
        public const double DefaultFontSize = 13.0;
        public const string DefaultBackground = "#1E1E1E";
        public const string DefaultForeground = "#D4D4D4";
        public const double DefaultWindowOpacity = 0.92;
        public const string DefaultBackgroundAlpha = "#CC1E1E1E";
    }
}
