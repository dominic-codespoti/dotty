using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class PlaceholderTableTests
{
    private const ushort Absent = PlaceholderIndices.Absent;

    private static PlaceholderIndices Get(PlaceholderCodeTable table, short code)
    {
        Assert.True(table.TryGet(code, out var indices));
        return indices;
    }

    [Fact]
    public void AllAbsentTripleIsSlotZeroWithCodeMinusTwo()
    {
        var table = new PlaceholderCodeTable();

        Assert.Equal((short)-2, table.Intern(Absent, Absent, Absent));
        Assert.Equal(PlaceholderIndices.None, Get(table, -2));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void NewTriplesGetDescendingCodesAndRoundTrip()
    {
        var table = new PlaceholderCodeTable();

        short a = table.Intern(0, 0, Absent);
        short b = table.Intern(1, 2, Absent);
        short c = table.Intern(0, 1, 17);

        Assert.Equal(new short[] { -3, -4, -5 }, new[] { a, b, c });
        Assert.Equal(new PlaceholderIndices(0, 0, Absent), Get(table, a));
        Assert.Equal(new PlaceholderIndices(1, 2, Absent), Get(table, b));
        Assert.Equal(new PlaceholderIndices(0, 1, 17), Get(table, c));
    }

    [Fact]
    public void IdenticalTriplesAreDeduplicatedAndDoNotConsumeSlots()
    {
        var table = new PlaceholderCodeTable();
        short first = table.Intern(5, 6, 7);
        int count = table.Count;

        for (int i = 0; i < 100; i++) Assert.Equal(first, table.Intern(5, 6, 7));

        Assert.Equal(count, table.Count);
    }

    [Fact]
    public void PositionMattersRowColAndMsbAreNotInterchangeable()
    {
        var table = new PlaceholderCodeTable();

        short rowOnly = table.Intern(3, Absent, Absent);
        short colOnly = table.Intern(Absent, 3, Absent);
        short msbOnly = table.Intern(Absent, Absent, 3);

        Assert.Equal(3, new[] { rowOnly, colOnly, msbOnly }.Distinct().Count());
        Assert.True(Get(table, rowOnly).HasRow && !Get(table, rowOnly).HasCol);
        Assert.True(Get(table, colOnly).HasCol && !Get(table, colOnly).HasRow);
        Assert.True(Get(table, msbOnly).HasMsb && !Get(table, msbOnly).HasCol);
    }

    [Theory]
    [InlineData((ushort)297)]
    [InlineData((ushort)5000)]
    [InlineData((ushort)0xFFFE)]
    public void IndicesBeyondTheDiacriticTableCountAsAbsent(ushort bad)
    {
        var table = new PlaceholderCodeTable();

        Assert.Equal(PlaceholderCodeTable.AbsentCode, table.Intern(bad, bad, bad));
        short code = table.Intern(1, bad, Absent);
        Assert.Equal(new PlaceholderIndices(1, Absent, Absent), Get(table, code));
        Assert.Equal(table.Intern(1, Absent, Absent), code);
    }

    [Fact]
    public void LargestValidIndexIsAccepted()
    {
        var table = new PlaceholderCodeTable();
        short code = table.Intern(296, 296, 296);

        Assert.Equal(new PlaceholderIndices(296, 296, 296), Get(table, code));
    }

    [Theory]
    [InlineData((short)-1)]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((short)-3)]       // never issued by a fresh table
    [InlineData((short)-1000)]
    [InlineData(short.MinValue)]
    [InlineData(short.MaxValue)]
    public void TryGetRejectsCodesThatWereNeverIssued(short code)
    {
        var table = new PlaceholderCodeTable();

        Assert.False(table.TryGet(code, out var indices));
        Assert.Equal(PlaceholderIndices.None, indices);
    }

    [Fact]
    public void FullTableDegradesNewTriplesToAbsentButKeepsExistingOnes()
    {
        var table = new PlaceholderCodeTable(capacity: 3); // slot 0 + two more
        short a = table.Intern(0, Absent, Absent);
        short b = table.Intern(1, Absent, Absent);

        Assert.Equal(PlaceholderCodeTable.AbsentCode, table.Intern(2, Absent, Absent));
        Assert.Equal(a, table.Intern(0, Absent, Absent));
        Assert.Equal(b, table.Intern(1, Absent, Absent));
        Assert.Equal(3, table.Count);
        Assert.Equal(new PlaceholderIndices(0, Absent, Absent), Get(table, a));
        Assert.Equal(new PlaceholderIndices(1, Absent, Absent), Get(table, b));
    }

    [Fact]
    public void MaximumCapacityIsReachedWithoutCodeAliasingOrOverflow()
    {
        var table = new PlaceholderCodeTable();
        var codes = new Dictionary<(ushort, ushort), short>();

        for (ushort row = 0; row < 297 && codes.Count < PlaceholderCodeTable.MaxCapacity - 1; row++)
        {
            for (ushort col = 0; col < 297 && codes.Count < PlaceholderCodeTable.MaxCapacity - 1; col++)
                codes[(row, col)] = table.Intern(row, col, Absent);
        }

        Assert.Equal(PlaceholderCodeTable.MaxCapacity, table.Count);
        Assert.All(codes.Values, code => Assert.True(code <= -3 && code >= -32766, $"code {code}"));
        Assert.Equal(codes.Count, codes.Values.Distinct().Count());
        foreach (var ((row, col), code) in codes)
            Assert.Equal(new PlaceholderIndices(row, col, Absent), Get(table, code));

        // One more distinct triple does not fit: it degrades to the absent code and nothing else changes.
        Assert.Equal(PlaceholderCodeTable.AbsentCode, table.Intern(296, 296, 296));
        Assert.Equal(PlaceholderCodeTable.MaxCapacity, table.Count);
        Assert.Equal(codes[(0, 0)], table.Intern(0, 0, Absent));
    }

    [Fact]
    public void ConcurrentInterningAgreesOnCodesAndSlotCount()
    {
        var table = new PlaceholderCodeTable();
        const int keys = 500;
        var results = new short[8][];
        Parallel.For(0, results.Length, worker =>
        {
            var mine = new short[keys];
            for (int i = 0; i < keys; i++)
            {
                int k = (i + worker * 37) % keys;
                mine[k] = table.Intern((ushort)(k % 297), (ushort)(k / 297), Absent);
                // A returned code must be resolvable immediately, even while other threads grow the table.
                Assert.True(table.TryGet(mine[k], out var got));
                Assert.Equal(new PlaceholderIndices((ushort)(k % 297), (ushort)(k / 297), Absent), got);
            }
            results[worker] = mine;
        });

        for (int w = 1; w < results.Length; w++)
            Assert.Equal(results[0], results[w]);
        Assert.Equal(keys + 1, table.Count);
        Assert.Equal(keys, results[0].Distinct().Count());
    }

    [Fact]
    public void ConstructorRejectsCapacityOutsideOneToMax()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaceholderCodeTable(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaceholderCodeTable(PlaceholderCodeTable.MaxCapacity + 1));
        Assert.Equal(PlaceholderCodeTable.AbsentCode, new PlaceholderCodeTable(1).Intern(0, 0, 0));
    }

    // ---- static facade: Encode / Append over raw mark text ------------------------------------------------------

    private static PlaceholderIndices Decode(short code)
    {
        Assert.True(PlaceholderTable.TryGet(code, out var indices));
        return indices;
    }

    [Fact]
    public void EncodeMapsFirstThreeDiacriticsToRowColMsb()
    {
        short code = PlaceholderTable.Encode("\u0305\u030D\u030E");

        Assert.Equal(new PlaceholderIndices(0, 1, 2), Decode(code));
    }

    [Fact]
    public void EncodeWithoutDiacriticsReturnsNoColdData()
    {
        Assert.Equal((short)-1, PlaceholderTable.Encode(""));
        Assert.Equal((short)-1, PlaceholderTable.Encode("\u0301")); // a mark that is not in the table
        Assert.Equal((short)-1, PlaceholderTable.Encode("abc"));
    }

    [Fact]
    public void EncodeDropsNonDiacriticsBetweenDiacriticsAndSurplusMarks()
    {
        short code = PlaceholderTable.Encode("\u0301\u030D\u0300\u030E\u0305\u0363");

        // 030D -> row 1, 030E -> col 2, 0305 -> msb 0; the extra 0363 is surplus.
        Assert.Equal(new PlaceholderIndices(1, 2, 0), Decode(code));
    }

    [Fact]
    public void EncodeHandlesAstralDiacriticsAndLoneSurrogates()
    {
        short code = PlaceholderTable.Encode("\uD800\U0001D244\uDC00\u0305");

        Assert.Equal(new PlaceholderIndices(296, 0, Absent), Decode(code));
    }

    [Fact]
    public void AppendFillsTheFirstAbsentSlotAndStopsAtThree()
    {
        short code = PlaceholderTable.Append(-1, "\u030D");
        Assert.Equal(new PlaceholderIndices(1, Absent, Absent), Decode(code));

        code = PlaceholderTable.Append(code, "\u0305");
        Assert.Equal(new PlaceholderIndices(1, 0, Absent), Decode(code));

        code = PlaceholderTable.Append(code, "\u030E\u0363");
        Assert.Equal(new PlaceholderIndices(1, 0, 2), Decode(code));

        short full = PlaceholderTable.Append(code, "\u0305");
        Assert.Equal(code, full);
    }

    [Fact]
    public void AppendReturnsTheSameCodeWhenNothingWasAdded()
    {
        short code = PlaceholderTable.Encode("\u0305");

        Assert.Equal(code, PlaceholderTable.Append(code, "\u0301x"));
        Assert.Equal((short)-1, PlaceholderTable.Append(-1, "\u0301"));
    }

    [Fact]
    public void EncodeIsStableAcrossCallsAndSplitAppendEqualsOneShotEncode()
    {
        short oneShot = PlaceholderTable.Encode("\u0305\u030D\u030E");
        short stepwise = PlaceholderTable.Append(PlaceholderTable.Append(PlaceholderTable.Encode("\u0305"), "\u030D"), "\u030E");

        Assert.Equal(oneShot, stepwise);
        Assert.Equal(oneShot, PlaceholderTable.Encode("\u0305\u030D\u030E"));
        Assert.NotEqual(oneShot, PlaceholderTable.Encode("\u030D\u0305\u030E"));
    }

    [Theory]
    [InlineData("\U0010EEEE", true)]
    [InlineData("\U0010EEEE\u0305", true)]
    [InlineData("\U0010EEEF", false)]
    [InlineData("\U0010EEED", false)]
    [InlineData("\U0010EEEE\U0010EEEE", true)]
    [InlineData("a\U0010EEEE", false)]
    [InlineData("", false)]
    public void IsPlaceholderClusterDetectsLeadingU10EEEE(string cluster, bool expected)
    {
        Assert.Equal(expected, PlaceholderTable.IsPlaceholderCluster(cluster));
    }

    [Fact]
    public void IsPlaceholderClusterRejectsALoneHighSurrogate()
    {
        // Plain code (not an attribute argument, where a lone surrogate would be replaced by U+FFFD).
        Assert.False(PlaceholderTable.IsPlaceholderCluster("\uDBFB"));
        Assert.False(PlaceholderTable.IsPlaceholderCluster("\uDBFBa"));
    }

    [Fact]
    public void PlaceholderRuneConstantMatchesItsSurrogatePair()
    {
        Assert.Equal("\U0010EEEE", char.ConvertFromUtf32(PlaceholderTable.PlaceholderRune));
    }
}
