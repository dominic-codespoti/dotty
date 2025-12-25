using Dotty.App.Services;
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
    }
}
