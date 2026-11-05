using System;
using Dotty.Silk.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests
{
    public class FontMetricsServiceTests
    {
        private static CellMetricsOptions Options(float sizePx, double lineHeight = 1.2, float scale = 1f) =>
            new(sizePx, lineHeight, scale);

        [Fact]
        public void MeasureCell_ThrowsArgumentNullException_WhenTypefaceIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => FontMetricsService.MeasureCell(null!, Options(12f)));
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
        [InlineData(10f, 1.0)]
        [InlineData(14f, 1.2)]
        [InlineData(28f, 1.2)]
        [InlineData(8f, 1.5)]
        public void MeasureCell_ReturnsConsistentDevicePixelMetrics(float fontSizePx, double lineHeight)
        {
            using var typeface = SKTypeface.Default;
            var metrics = FontMetricsService.MeasureCell(typeface, Options(fontSizePx, lineHeight));

            Assert.True(metrics.CellWidth >= 1);
            Assert.True(metrics.CellHeight >= fontSizePx * lineHeight - 0.5f);
            Assert.InRange(metrics.Baseline, 0, metrics.CellHeight);
            Assert.InRange(metrics.UnderlineThickness, 1, metrics.CellHeight);
            Assert.InRange(metrics.UnderlinePosition, 0, metrics.CellHeight - metrics.UnderlineThickness);
            Assert.InRange(metrics.StrikethroughPosition, 0, metrics.Baseline);
        }

        [Fact]
        public void MeasureCell_UsesA8AtlasFontMetricsPolicy()
        {
            using var typeface = SKTypeface.Default;
            const float fontSizePx = 25.5f;
            using var atlasFont = new SKFont(typeface, fontSizePx)
            {
                Subpixel = false,
                Hinting = SKFontHinting.Full,
                Edging = SKFontEdging.Antialias,
            };
            var fm = atlasFont.Metrics;
            float advance = MathF.Max(MathF.Max(0.5f, fm.AverageCharacterWidth), atlasFont.MeasureText("W"));

            var metrics = FontMetricsService.MeasureCell(typeface, Options(fontSizePx, 1.0));

            Assert.Equal((int)MathF.Round(advance, MidpointRounding.AwayFromZero), metrics.CellWidth);
            Assert.Equal((int)MathF.Round(MathF.Abs(fm.Ascent), MidpointRounding.AwayFromZero), metrics.Ascent);
        }

        [Fact]
        public void MeasureCell_ScalesLineHeight_WhenLineHeightIncreases()
        {
            using var typeface = SKTypeface.Default;
            var normal = FontMetricsService.MeasureCell(typeface, Options(24f, 1.0));
            var tall = FontMetricsService.MeasureCell(typeface, Options(24f, 2.5));

            Assert.True(tall.CellHeight > normal.CellHeight);
            Assert.True(tall.BaselineShift > normal.BaselineShift, "extra line height is split above the text");
        }

        [Fact]
        public void MeasureCell_NormalizesNonFiniteInputs()
        {
            using var typeface = SKTypeface.Default;
            var metrics = FontMetricsService.MeasureCell(typeface, new CellMetricsOptions(float.NaN, double.PositiveInfinity, float.NaN));

            Assert.True(metrics.CellWidth >= 1);
            Assert.True(metrics.CellHeight >= 1);
        }

        [Fact]
        public void MeasureCell_EqualInputsNeverGrowTheCache()
        {
            var cache = new CellMetricsCache(capacity: 4);
            var typeface = SKTypeface.Default;

            var first = FontMetricsService.MeasureCell(cache, typeface, Options(21f, 1.2, 1.5f));
            for (int i = 0; i < 10; i++)
                Assert.Equal(first, FontMetricsService.MeasureCell(cache, typeface, Options(21f, 1.2, 1.5f)));

            Assert.Equal(1, cache.Count);
        }

        [Theory]
        // Options that normalize to the same value share one entry.
        [InlineData(14f, 1.2, 0f, 14f, 1.2, -1f, 1)]
        [InlineData(5000f, 1.2, 1f, 4096f, 1.2, 1f, 1)]
        [InlineData(14f, 20.0, 1f, 14f, 8.0, 1f, 1)]
        [InlineData(14f, 1.2, 100f, 14f, 1.2, DisplayScale.MaxScale, 1)]
        // Any differing measurement input is a different entry.
        [InlineData(14f, 1.2, 1f, 15f, 1.2, 1f, 2)]
        [InlineData(14f, 1.2, 1f, 14f, 1.3, 1f, 2)]
        [InlineData(14f, 1.2, 1f, 14f, 1.2, 2f, 2)]
        public void MeasureCell_KeysByNormalizedValue(
            float sizeA, double lineHeightA, float scaleA,
            float sizeB, double lineHeightB, float scaleB,
            int expectedEntries)
        {
            var cache = new CellMetricsCache(capacity: 8);
            var typeface = SKTypeface.Default;

            FontMetricsService.MeasureCell(cache, typeface, Options(sizeA, lineHeightA, scaleA));
            FontMetricsService.MeasureCell(cache, typeface, Options(sizeB, lineHeightB, scaleB));

            Assert.Equal(expectedEntries, cache.Count);
        }

        [Fact]
        public void MeasureCell_AdjustmentsArePartOfTheKey()
        {
            var cache = new CellMetricsCache(capacity: 8);
            var typeface = SKTypeface.Default;

            var plain = FontMetricsService.MeasureCell(cache, typeface, Options(20f));
            var wider = FontMetricsService.MeasureCell(cache, typeface, Options(20f) with { AdjustWidth = new CellAdjustment(2, false) });

            Assert.Equal(plain.CellWidth + 2, wider.CellWidth);
            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public void MeasureCell_DistinctWrappersOfTheSameFaceShareOneEntry()
        {
            var original = SKTypeface.Default;
            var stream = original.OpenStream(out int ttcIndex);
            Assert.SkipUnless(stream != null, "The default typeface exposes no font data stream");
            using var copy = SKTypeface.FromStream(stream, ttcIndex);
            Assert.SkipUnless(
                copy != null && !ReferenceEquals(copy, original)
                    && FontMetricsService.GetIdentity(copy) == FontMetricsService.GetIdentity(original),
                "Reloading the default font data did not yield a separate wrapper with the same family and style");
            var cache = new CellMetricsCache(capacity: 4);

            var fromOriginal = FontMetricsService.MeasureCell(cache, original, Options(14f));
            var fromCopy = FontMetricsService.MeasureCell(cache, copy!, Options(14f));

            Assert.Equal(fromOriginal, fromCopy);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void MeasureCell_CacheStaysWithinCapacity()
        {
            var cache = new CellMetricsCache(capacity: 3);
            var typeface = SKTypeface.Default;

            for (int size = 10; size < 20; size++)
                FontMetricsService.MeasureCell(cache, typeface, Options(size));

            Assert.Equal(3, cache.Count);
        }
    }
}
