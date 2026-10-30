using System.Globalization;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Pins the extended grapheme cluster segmentation (UAX #29) that the buffer relies on. Dotty does not segment
/// text itself: it uses .NET's <see cref="StringInfo"/>, so a runtime whose Unicode data changes a boundary the
/// terminal depends on fails here.
/// </summary>
public sealed class GraphemeSegmentationTests
{
    // Each case lists the expected clusters in order; their concatenation is the input.
    public static TheoryData<string, string[]> Cases => new()
    {
        { "ASCII", new[] { "a", "b", "c" } },
        { "GB3 CR LF", new[] { "\r\n", "a" } },
        { "GB4/5 controls break both ways", new[] { "a", "\n", "\u0301" } },
        { "GB9 combining marks", new[] { "e\u0301\u0302", "x" } },
        { "GB9 ZWJ extends", new[] { "a\u200D", "b" } },
        { "GB9a spacing mark", new[] { "\u0915\u093F", "a" } },
        { "GB9b prepend", new[] { "\u0600\u0661", "\u0662" } },
        { "GB6 L + V + T", new[] { "\u1100\u1161\u11A8", "a" } },
        { "GB6 L + L + LV", new[] { "\u1100\u1100\uAC00" } },
        { "GB7 LV + V + T", new[] { "\uAC00\u1161\u11A8" } },
        { "GB8 LVT + T", new[] { "\uAC01\u11A8", "\uAC00" } },
        { "GB11 ZWJ family", new[] { "\U0001F468\u200D\U0001F469\u200D\U0001F467", "a" } },
        { "GB11 ZWJ with VS16", new[] { "\U0001F3F3\uFE0F\u200D\U0001F308" } },
        { "GB11 needs a pictographic before ZWJ", new[] { "a\u200D", "\U0001F308" } },
        { "GB12/13 regional indicator pairs", new[] { "\U0001F1FA\U0001F1F8", "\U0001F1E9\U0001F1EA" } },
        { "GB12/13 odd regional indicator", new[] { "\U0001F1FA\U0001F1F8", "\U0001F1E9" } },
        { "keycap", new[] { "1\uFE0F\u20E3", "2\uFE0F\u20E3" } },
        { "skin tone modifier", new[] { "\U0001F44D\U0001F3FD", "\U0001F44D" } },
        { "VS16 and VS15 extend", new[] { "\u2764\uFE0F", "\u263A\uFE0E" } },
        { "tag sequence", new[] { "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F", "a" } },
        { "GB999 wide ideographs", new[] { "\u4E2D", "\u6587" } },
        { "zero width space is a control", new[] { "a", "\u200B", "b" } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void StringInfoSegmentsExtendedGraphemeClusters(string name, string[] clusters)
    {
        string text = string.Concat(clusters);
        var actual = new List<string>();
        int index = 0;
        while (index < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            Assert.True(length > 0, name);
            actual.Add(text.Substring(index, length));
            index += length;
        }
        Assert.Equal(clusters, actual.ToArray());
    }
}
