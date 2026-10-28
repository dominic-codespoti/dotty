using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Bulk OSC accumulation / bytes-native OSC 52: results must be independent of how the
/// byte stream is chunked and identical to the per-byte semantics.
/// </summary>
public sealed class OscFastPathTests
{
    // Mirrors BasicAnsiParser.MaxOscPayloadBytes (private); the boundary tests pin it.
    private const int OscCap = 1024 * 1024;

    private sealed class Rig : IDisposable
    {
        // The OSC tests never scroll; a tiny ring keeps the thousands of rigs the split sweeps build cheap.
        public readonly TerminalAdapter Adapter = new(24, 80, scrollbackCapacity: 16);
        public readonly BasicAnsiParser Parser = new();
        public readonly List<string> Events = new();

        public Rig()
        {
            Parser.Handler = Adapter;
            Adapter.TitleChanged += t => Events.Add("T:" + t);
            Adapter.ClipboardWriteRequested += w => Events.Add($"W:{(int)w.Selection}:{w.Text}");
            Adapter.ClipboardReadRequested += r => Events.Add($"R:{(int)r.Selection}");
        }

        public void Dispose() => Adapter.Dispose();
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static List<string> Run(byte[] stream, params int[] splitAt)
    {
        using var rig = new Rig();
        int prev = 0;
        foreach (int cut in splitAt)
        {
            rig.Parser.Feed(stream.AsSpan(prev, cut - prev));
            prev = cut;
        }
        rig.Parser.Feed(stream.AsSpan(prev));
        return rig.Events;
    }

    public static IEnumerable<object[]> Streams()
    {
        const string end = "\u001b]2;END\u0007";
        yield return new object[] { "\u001b]2;title\u0007" + end };
        yield return new object[] { "\u001b]2;title\u001b\\" + end };
        yield return new object[] { "\u001b]0;日本語 🙂 ünï\u0007" + end };
        yield return new object[] { "\u001b]2;cancel\u0018" + end };
        yield return new object[] { "\u001b]2;cancel\u001a" + end };
        yield return new object[] { "\u001b]2;abort\nx" + end };
        yield return new object[] { "\u001b]2;abort\u0001" + end };
        yield return new object[] { "\u001b]2;esc-not-st\u001bxrest" + end };
        yield return new object[] { "\u001b]2;dbl\u001b\u001b\\" + end };
        yield return new object[] { "\u001b]2;a\u007fb\u0007" + end };
        yield return new object[] { $"\u001b]52;c;{B64("hello")}\u0007" + end };
        yield return new object[] { $"\u001b]52;cp;{B64("héllo 日本語")}\u001b\\" + end };
        yield return new object[] { "\u001b]52;c;?\u0007" + end };
        yield return new object[] { "\u001b]52;c;\u0007" + end };
        yield return new object[] { "\u001b]52;c\u0007" + end };
        yield return new object[] { "\u001b]52\u0007" + end };
        yield return new object[] { "\u001b]52;q;" + B64("x") + "\u0007" + end };
        yield return new object[] { "\u001b]52;c;!!!bad\u0007" + end };
        yield return new object[] { "\u001b]52;c;" + B64("hello")[..4] + " " + B64("hello")[4..] + "\u0007" + end };
        yield return new object[] { "\u001b]52;c;" + B64("x") + "\u0018" + end };
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void EverySplitAndNearbyDoubleSplit_MatchesWholeFeed(string text)
    {
        byte[] stream = Bytes(text);
        List<string> whole = Run(stream);
        Assert.Contains("T:END", whole);

        for (int a = 1; a < stream.Length; a++)
        {
            Assert.Equal(whole, Run(stream, a));
            for (int b = a + 1; b < Math.Min(stream.Length, a + 9); b++)
                Assert.Equal(whole, Run(stream, a, b));
        }

        // Byte-at-a-time feed.
        using var rig = new Rig();
        foreach (byte value in stream)
            rig.Parser.Feed(new[] { value });
        Assert.Equal(whole, rig.Events);
    }

    [Fact]
    public void KnownOutcomes_MatchPerByteSemantics()
    {
        Assert.Equal(new[] { "T:title", "T:END" }, Run(Bytes("\u001b]2;title\u0007\u001b]2;END\u0007")));
        Assert.Equal(new[] { "T:END" }, Run(Bytes("\u001b]2;cancel\u0018\u001b]2;END\u0007")));
        Assert.Equal(new[] { "T:END" }, Run(Bytes("\u001b]2;abort\u0001\u001b]2;END\u0007")));
        Assert.Equal(new[] { "T:日本語", "T:END" }, Run(Bytes("\u001b]2;日本語\u001b\\\u001b]2;END\u0007")));
        Assert.Equal(new[] { "W:1:hello", "T:END" }, Run(Bytes($"\u001b]52;c;{B64("hello")}\u0007\u001b]2;END\u0007")));
        Assert.Equal(new[] { "R:1", "T:END" }, Run(Bytes("\u001b]52;c;?\u0007\u001b]2;END\u0007")));
        Assert.Equal(new[] { "W:1:", "T:END" }, Run(Bytes("\u001b]52;c;\u0007\u001b]2;END\u0007")));
        Assert.Equal(new[] { "T:END" }, Run(Bytes("\u001b]52;c;!!!bad\u0007\u001b]2;END\u0007")));
    }

    [Fact]
    public void Osc52_Base64WithWhitespace_StillDecodes()
    {
        string b = B64("hello world");
        Assert.Equal(new[] { "W:1:hello world" }, Run(Bytes($"\u001b]52;c;{b[..4]} {b[4..]}\u0007")));
    }

    [Fact]
    public void Osc52_InvalidUtf8InDecodedText_UsesReplacement()
    {
        string payload = Convert.ToBase64String(new byte[] { 0xFF, 0xFE });
        List<string> events = Run(Bytes($"\u001b]52;c;{payload}\u0007"));
        Assert.Contains('\uFFFD', Assert.Single(events));
    }

    [Fact]
    public void Osc52_InvalidUtf8InPayloadBytes_IsDropped()
    {
        byte[] stream = Bytes("\u001b]52;c;QUJD");
        byte[] tail = { 0xFF, 0x07 };
        var all = new byte[stream.Length + tail.Length];
        stream.CopyTo(all, 0);
        tail.CopyTo(all, stream.Length);
        Assert.Empty(Run(all));
    }

    [Fact]
    public void Osc52_64KiBPayload_DeliveredWhole_AtEverySampledSplit()
    {
        string text = new string('a', 48_000) + "日本語";
        byte[] stream = Bytes($"\u001b]52;c;{B64(text)}\u0007\u001b]2;END\u0007");
        var whole = Run(stream);
        Assert.Equal("W:1:" + text, whole[0]);
        foreach (int cut in new[] { 1, 5, 6, 7, 1000, 32768, stream.Length / 2, stream.Length - 12, stream.Length - 11, stream.Length - 1 })
            Assert.Equal(whole, Run(stream, cut));
    }

    [Fact]
    public void ExactCapPayload_Dispatches_InOneAndManyChunks()
    {
        // Payload = "2;" + (cap - 2) bytes; the title itself is truncated by the adapter.
        byte[] stream = Bytes("\u001b]2;" + new string('a', OscCap - 2) + "\u0007\u001b]2;END\u0007");
        string expected = "T:" + new string('a', TerminalAdapter.MaxTitleChars);
        Assert.Equal(new[] { expected, "T:END" }, Run(stream));
        Assert.Equal(new[] { expected, "T:END" }, Run(stream, 3, 70_000, OscCap));
    }

    [Fact]
    public void CapPlusOneByte_IsDiscardedThroughTerminator_InOneAndManyChunks()
    {
        foreach (string terminator in new[] { "\u0007", "\u001b\\" })
        {
            byte[] stream = Bytes("\u001b]2;" + new string('a', OscCap - 1) + terminator + "\u001b]2;END\u0007");
            Assert.Equal(new[] { "T:END" }, Run(stream));
            Assert.Equal(new[] { "T:END" }, Run(stream, 3, 70_000, OscCap, OscCap + 2));
        }
    }

    [Fact]
    public void Overflow_ThenControlAbort_RecoversAndExecutesControl()
    {
        byte[] stream = Bytes("\u001b]2;" + new string('a', OscCap + 100) + "\u0001\u001b]2;END\u0007");
        Assert.Equal(new[] { "T:END" }, Run(stream));
        Assert.Equal(new[] { "T:END" }, Run(stream, 10, OscCap + 50));
    }

    [Fact]
    public void Overflow_ThenCancel_RecoversWithoutSwallowingFollowingText()
    {
        byte[] stream = Bytes("\u001b]2;" + new string('a', OscCap + 100) + "\u0018\u001b]2;END\u0007");
        Assert.Equal(new[] { "T:END" }, Run(stream));
    }
}
