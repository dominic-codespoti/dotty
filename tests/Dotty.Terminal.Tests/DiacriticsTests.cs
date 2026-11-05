using System.Globalization;
using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// The Kitty row/column/high-byte diacritic table. The values below were derived independently of the code
/// (counting the published list by hand and cross-checking with Python's unicodedata: every entry has canonical
/// combining class 230).
/// </summary>
public sealed class DiacriticsTests
{
    [Fact]
    public void HasExactly297Entries()
    {
        Assert.Equal(297, Diacritics.Count);
    }

    [Fact]
    public void StartsWithThePublishedPrefix()
    {
        int[] expected = [0x305, 0x30D, 0x30E, 0x310, 0x312, 0x33D, 0x33E, 0x33F, 0x346, 0x34A, 0x34B, 0x34C, 0x350, 0x351, 0x352, 0x357, 0x35B, 0x363];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], Diacritics.CodepointAt(i));
    }

    [Fact]
    public void EndsWithU1D244()
    {
        Assert.Equal(0x1D244, Diacritics.CodepointAt(296));
        Assert.Equal(0x1D243, Diacritics.CodepointAt(295));
        Assert.Equal(0x1D242, Diacritics.CodepointAt(294));
    }

    [Fact]
    public void IsStrictlyAscendingAndAllNonSpacingMarks()
    {
        int previous = -1;
        for (int i = 0; i < Diacritics.Count; i++)
        {
            int cp = Diacritics.CodepointAt(i);
            Assert.True(cp > previous, $"entry {i} (U+{cp:X}) is not above U+{previous:X}");
            Assert.Equal(UnicodeCategory.NonSpacingMark, CharUnicodeInfo.GetUnicodeCategory(cp));
            previous = cp;
        }
    }

    [Fact]
    public void ChecksumsMatchTheReferenceList()
    {
        // Independent fingerprints (computed offline from the reference list): catch any dropped, added or shifted entry.
        long sum = 0, weighted = 0;
        for (int i = 0; i < Diacritics.Count; i++)
        {
            int cp = Diacritics.CodepointAt(i);
            sum += cp;
            weighted += (long)i * cp;
        }
        Assert.Equal(4545336L, sum);
        Assert.Equal(1137028771L, weighted);
        Assert.Equal(0x35B, Diacritics.CodepointAt(16));
        Assert.Equal(0x7EB, Diacritics.CodepointAt(100));
        Assert.Equal(0x20D4, Diacritics.CodepointAt(200));
    }

    [Fact]
    public void IndexOfInvertsCodepointAtForEveryEntry()
    {
        for (int i = 0; i < Diacritics.Count; i++)
            Assert.Equal(i, Diacritics.IndexOf(Diacritics.CodepointAt(i)));
    }

    [Theory]
    [InlineData(0x305, 0)]
    [InlineData(0x30D, 1)]
    [InlineData(0x30E, 2)]
    [InlineData(0x363, 17)]
    [InlineData(0x1D244, 296)]
    public void IndexOfKnownEntries(int codepoint, int index)
    {
        Assert.Equal(index, Diacritics.IndexOf(codepoint));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x41)]
    [InlineData(0x300)] // combining grave: class 230 but composes with letters, so not in the table
    [InlineData(0x301)]
    [InlineData(0x304)]
    [InlineData(0x306)]
    [InlineData(0x34D)] // between table entries
    [InlineData(0x1D245)] // just past the end
    [InlineData(0x10EEEE)]
    [InlineData(-1)]
    public void IndexOfRejectsNonMembers(int codepoint)
    {
        Assert.Equal(-1, Diacritics.IndexOf(codepoint));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(297)]
    [InlineData(int.MaxValue)]
    public void CodepointAtOutOfRangeIsMinusOne(int index)
    {
        Assert.Equal(-1, Diacritics.CodepointAt(index));
    }
}
