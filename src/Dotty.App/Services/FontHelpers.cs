namespace Dotty.App.Services
{
    /// <summary>
    /// Small helper functions around font/glyph heuristics that are safe to unit-test.
    /// Kept minimal to avoid heavy Avalonia/UI dependencies in tests.
    /// </summary>
    public static class FontHelpers
    {
        /// <summary>
        /// Returns true when the codepoint is likely to be a symbol/icon (PUA, box drawing, emoji, bullets).
        /// This mirrors the heuristics used in TerminalCanvas and is intentionally conservative.
        /// </summary>
        public static bool IsLikelySymbol(int code)
        {
            // Private Use Area (PUA) commonly used by Nerd fonts for icons
            if (code >= 0xE000 && code <= 0xF8FF) return true;
            // Box drawing, geometric shapes, bullets
            if (code >= 0x2500 && code <= 0x27BF) return true;
            // Misc symbols (e.g., bullets)
            if (code == 0x2022) return true;
            // Emoji / pictographs range
            if (code >= 0x1F300 && code <= 0x1FAFF) return true;
            return false;
        }

        /// <summary>
        /// Overload for char compatibility.
        /// </summary>
        public static bool IsLikelySymbol(char ch) => IsLikelySymbol((int)ch);
    }
}
