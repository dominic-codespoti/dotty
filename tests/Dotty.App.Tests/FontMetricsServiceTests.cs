using System;
using Dotty.Silk.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests
{
    public class FontMetricsServiceTests
    {
        [Fact]
        public void MeasureCell_ThrowsArgumentNullException_WhenTypefaceIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => FontMetricsService.MeasureCell(null!, 12f, 1.2, 1.0f));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolveTypeface_ReturnsNonNullTypeface_ForNullOrWhitespace(string? familyList)
        {
            var typeface = FontMetricsService.ResolveTypeface(familyList);
            Assert.NotNull(typeface);
        }

        [Fact]
        public void ResolveTypeface_FallsBackToDefault_WhenNonExistentFamilyGiven()
        {
            var typeface = FontMetricsService.ResolveTypeface("NonExistentFontFamilyName_12345");
            Assert.NotNull(typeface);
        }

        [Theory]
        [InlineData(10f, 1.0, 1.0f)]
        [InlineData(14f, 1.2, 1.0f)]
        [InlineData(14f, 1.2, 2.0f)]
        [InlineData(16f, 1.5, 0.5f)]
        public void MeasureCell_ReturnsPositiveDimensions(float fontSize, double lineHeight, float scale)
        {
            using var typeface = SKTypeface.Default;
            var (cellWidth, cellHeight) = FontMetricsService.MeasureCell(typeface, fontSize, lineHeight, scale);

            Assert.True(cellWidth >= 4f, $"Cell width ({cellWidth}) should be >= 4px");
            Assert.True(cellHeight > 0f, $"Cell height ({cellHeight}) should be positive");
        }

        [Fact]
        public void MeasureCell_ScalesLineHeight_WhenLineHeightIncreases()
        {
            using var typeface = SKTypeface.Default;
            const float fontSize = 24f;
            const float scale = 1.0f;

            var (_, normalHeight) = FontMetricsService.MeasureCell(typeface, fontSize, 1.0, scale);
            var (_, doubleHeight) = FontMetricsService.MeasureCell(typeface, fontSize, 2.5, scale);

            Assert.True(doubleHeight > normalHeight, "Cell height should increase when line height multiplier increases significantly");
        }

        [Fact]
        public void MeasureCell_ClampsScale_WhenZeroOrNegativeScaleProvided()
        {
            using var typeface = SKTypeface.Default;
            var (widthZero, heightZero) = FontMetricsService.MeasureCell(typeface, 12f, 1.2, 0f);
            var (widthNeg, heightNeg) = FontMetricsService.MeasureCell(typeface, 12f, 1.2, -1.0f);

            Assert.True(widthZero >= 4f);
            Assert.True(heightZero > 0f);
            Assert.True(widthNeg >= 4f);
            Assert.True(heightNeg > 0f);
        }
        [Fact]
        public void MeasureCell_NormalizesNonFiniteInputs()
        {
            using var typeface = SKTypeface.Default;

            var (cellWidth, cellHeight) = FontMetricsService.MeasureCell(
                typeface,
                float.NaN,
                double.PositiveInfinity,
                float.PositiveInfinity);

            Assert.True(float.IsFinite(cellWidth) && cellWidth >= 4f);
            Assert.True(float.IsFinite(cellHeight) && cellHeight > 0f);
        }
    }
}
