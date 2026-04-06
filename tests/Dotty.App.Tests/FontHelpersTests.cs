using Dotty.App.Services;
using Dotty.Abstractions.Config;
using Xunit;

namespace Dotty.App.Tests
{
    public class FontHelpersTests
    {
        [Theory]
        [InlineData(0xE0B6, true)] // PUA powerline-ish
        [InlineData(0xE0B4, true)] // PUA
        [InlineData(0x2500, true)] // box-drawing
        [InlineData(0x2022, true)] // bullet
        [InlineData(0x41, false)]  // 'A'
        [InlineData(0x20, false)]  // space
        [InlineData(0x1F600, true)] // emoji
        public void IsLikelySymbol_ReturnsExpected(int codepoint, bool expected)
        {
            var result = FontHelpers.IsLikelySymbol(codepoint);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Material Symbols Sharp", true)]
        [InlineData("Segoe MDL2 Assets", true)]
        [InlineData("JetBrains Mono", false)]
        [InlineData("Cascadia Code", false)]
        [InlineData(null, false)]
        public void IsLikelySymbolFontName_ReturnsExpected(string? fontFamilyName, bool expected)
        {
            var result = FontHelpers.IsLikelySymbolFontName(fontFamilyName);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void DefaultFontStack_PrioritizesMonospaceBeforeSymbolFonts()
        {
            var stack = DottyDefaults.FontFamily;
            var monospaceIndex = stack.IndexOf("Cascadia Code", System.StringComparison.OrdinalIgnoreCase);
            var symbolIndex = stack.IndexOf("Material Symbols Sharp", System.StringComparison.OrdinalIgnoreCase);

            Assert.True(monospaceIndex >= 0, "Default stack should include a common monospace fallback");
            Assert.True(symbolIndex >= 0, "Default stack should include Material Symbols for icon fallback");
            Assert.True(monospaceIndex < symbolIndex, "Monospace fonts must appear before symbol fonts");
        }
    }
}
