using Dotty.Runtime.Fonts;
using Dotty.Abstractions.Config;
using Xunit;

namespace Dotty.App.Tests
{
    public class FontHelpersTests
    {
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

        // Decision F3: OS stacks with no Nerd Font assumed; monospace faces precede the emoji/symbol faces,
        // because the list is also the fallback order.
        [Theory]
        [InlineData(DottyDefaults.FontFamilyLinux, "monospace", "Noto Color Emoji")]
        [InlineData(DottyDefaults.FontFamilyMacOS, "Menlo", "Apple Color Emoji")]
        [InlineData(DottyDefaults.FontFamilyWindows, "Cascadia Mono", "Segoe UI Emoji")]
        public void DefaultFontStacks_PutMonospaceFirstAndEmojiAfter(string stack, string first, string emoji)
        {
            var families = Dotty.Rendering.Gpu.FontOptions.ParseFamilyList(stack);
            Assert.Equal(first, families[0]);
            Assert.Contains(emoji, families);
            Assert.DoesNotContain(families, f => f.Contains("Nerd", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void DefaultFontFamily_IsTheStackOfThisOs()
        {
            string expected = System.OperatingSystem.IsMacOS() ? DottyDefaults.FontFamilyMacOS
                : System.OperatingSystem.IsWindows() ? DottyDefaults.FontFamilyWindows
                : DottyDefaults.FontFamilyLinux;
            Assert.Equal(expected, DottyDefaults.FontFamily);
            Assert.Equal(expected, new Dotty.Runtime.Config.DottyUserConfig().Font.Family);
        }
    }
}
