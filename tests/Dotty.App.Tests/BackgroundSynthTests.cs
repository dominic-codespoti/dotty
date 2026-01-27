using System.Collections.Generic;
using Dotty.App.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests
{
    public class BackgroundSynthTests
    {
        private static SKColor C(int r, int g, int b) => new SKColor((byte)r, (byte)g, (byte)b);

        [Fact]
        public void PrefixSeparatorIsAbsorbed()
        {
            // [sep][bg][bg][bg] -> span starts at 0
            var cells = new SynthCell[4];
            cells[0] = new SynthCell { IsSeparatorGlyph = true, HasBg = false, Width = 1 };
            cells[1] = new SynthCell { HasBg = true, Bg = C(0, 128, 0), Width = 1 };
            cells[2] = new SynthCell { HasBg = true, Bg = C(0, 128, 0), Width = 1 };
            cells[3] = new SynthCell { HasBg = true, Bg = C(0, 128, 0), Width = 1 };

            var spans = BackgroundSynth.BuildRowSpans(cells);
            Assert.Single(spans);
            Assert.Equal(0, spans[0].X0);
            Assert.Equal(4, spans[0].X1);
        }

        [Fact]
        public void SuffixSeparatorIsAbsorbed()
        {
            // [bg][bg][bg][sep] -> span ends at last+1
            var cells = new SynthCell[4];
            cells[0] = new SynthCell { HasBg = true, Bg = C(10, 10, 10), Width = 1 };
            cells[1] = new SynthCell { HasBg = true, Bg = C(10, 10, 10), Width = 1 };
            cells[2] = new SynthCell { HasBg = true, Bg = C(10, 10, 10), Width = 1 };
            cells[3] = new SynthCell { IsSeparatorGlyph = true, HasBg = false, Width = 1 };

            var spans = BackgroundSynth.BuildRowSpans(cells);
            Assert.Single(spans);
            Assert.Equal(0, spans[0].X0);
            Assert.Equal(4, spans[0].X1);
        }

        [Fact]
        public void PrefixRunOfSeparatorsIsAbsorbed()
        {
            // [sep][sep][bg] -> span starts at 0
            var cells = new SynthCell[3];
            cells[0] = new SynthCell { IsSeparatorGlyph = true, HasBg = false, Width = 1 };
            cells[1] = new SynthCell { IsSeparatorGlyph = true, HasBg = false, Width = 1 };
            cells[2] = new SynthCell { HasBg = true, Bg = C(50, 60, 70), Width = 1 };

            var spans = BackgroundSynth.BuildRowSpans(cells);
            Assert.Single(spans);
            Assert.Equal(0, spans[0].X0);
            Assert.Equal(3, spans[0].X1);
        }

        [Fact]
        public void WideGraphemeProducesContinuationCoverage()
        {
            // cell 0 width=2, cell1 is continuation
            var cells = new SynthCell[3];
            cells[0] = new SynthCell { HasBg = true, Bg = C(1, 2, 3), Width = 2 };
            cells[1] = new SynthCell { IsContinuation = true, HasBg = false, Width = 1 };
            cells[2] = new SynthCell { HasBg = true, Bg = C(1, 2, 3), Width = 1 };

            var spans = BackgroundSynth.BuildRowSpans(cells);
            // Expect the wide grapheme contributes to a span covering columns 0..3
            Assert.Single(spans);
            Assert.Equal(0, spans[0].X0);
            Assert.Equal(3, spans[0].X1);
        }
    }
}
