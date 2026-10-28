using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Kitty graphics APC capture (ESC _ G ... ST) in <see cref="BasicAnsiParser"/>: framing, termination
/// and abort rules, bounded control data, and invariance under input splitting.
/// </summary>
public sealed class KittyApcParserTests
{
    private const string St = "\u001b\\";

    private static byte[] Bytes(string latin1) => Encoding.Latin1.GetBytes(latin1);

    private static BoundedSequenceTests.RecordingHandler NewHandler(bool enabled = true)
        => new() { KittyEnabled = enabled };

    private static BoundedSequenceTests.RecordingHandler Run(byte[] data, bool enabled, params int[] cuts)
    {
        var handler = NewHandler(enabled);
        var parser = new BasicAnsiParser { Handler = handler };
        int start = 0;
        foreach (int cut in cuts)
        {
            parser.Feed(data.AsSpan(start, cut - start));
            start = cut;
        }

        parser.Feed(data.AsSpan(start));
        return handler;
    }

    // Payload slice boundaries are an implementation detail; consecutive P: events are merged.
    private static string[] Canon(IEnumerable<string> events)
    {
        var result = new List<string>();
        foreach (string e in events)
        {
            if (e.StartsWith("P:", StringComparison.Ordinal) && result.Count > 0 &&
                result[^1].StartsWith("P:", StringComparison.Ordinal))
            {
                result[^1] += e.Substring(2);
            }
            else
            {
                result.Add(e);
            }
        }

        return result.Where(e => e != "P:").ToArray();
    }

    private static string Printed(BoundedSequenceTests.RecordingHandler h) => string.Concat(h.PrintCalls);

    // Lone high bytes in ground state decode to U+FFFD; how many is outside this feature's scope.
    private static string PrintedNoReplacement(BoundedSequenceTests.RecordingHandler h) => Printed(h).Replace("\uFFFD", "");

    private static string Describe(BoundedSequenceTests.RecordingHandler h)
        => string.Join("|", Canon(h.KittyEvents)) + " // " + PrintedNoReplacement(h) + " // " + string.Join(",", h.ControlCalls)
           + " // up" + string.Join(",", h.CursorUpCalls) + " down" + string.Join(",", h.CursorDownCalls);

    // Feeds the whole input, byte-by-byte, and at every two-way split; all must be indistinguishable.
    private static string AssertSplitInvariant(string input, bool enabled = true)
    {
        byte[] data = Bytes(input);
        string whole = Describe(Run(data, enabled));
        Assert.Equal(whole, Describe(Run(data, enabled, Enumerable.Range(1, data.Length - 1).ToArray())));
        for (int cut = 1; cut < data.Length; cut++)
            Assert.True(whole == Describe(Run(data, enabled, cut)), $"split at {cut} of {input.Length} differs");
        return whole;
    }

    public static IEnumerable<object[]> CompleteCommands() => new[]
    {
        new object[] { "\u001b_Gi=1,a=T;AAAA" + St + "tail", new[] { "B:i=1,a=T", "P:AAAA", "E:True" }, "tail" },
        new object[] { "\u001b_Gi=1;AAAA\u009ctail", new[] { "B:i=1", "P:AAAA", "E:True" }, "tail" },
        new object[] { "\u001b_G" + St + "tail", new[] { "B:", "E:True" }, "tail" },
        new object[] { "\u001b_G;" + St + "tail", new[] { "B:", "E:True" }, "tail" },
        new object[] { "\u001b_Ga=q,i=1" + St + "tail", new[] { "B:a=q,i=1", "E:True" }, "tail" },
        new object[] { "\u001b_Ga=q\u009ctail", new[] { "B:a=q", "E:True" }, "tail" },
        new object[] { "\u001b_Gi=1;" + St + "tail", new[] { "B:i=1", "E:True" }, "tail" },
        new object[] { "\u001b_Gi=1;\u009ctail", new[] { "B:i=1", "E:True" }, "tail" },
        // DEL and non-ST high bytes are payload; the decoder rejects them later.
        new object[] { "\u001b_Gi=1;\u007fA\u0080\u00ffB" + St + "tail", new[] { "B:i=1", "P:\u007fA\u0080\u00ffB", "E:True" }, "tail" },
        // Only the first ';' splits control from payload.
        new object[] { "\u001b_Gi=1;a;b" + St, new[] { "B:i=1", "P:a;b", "E:True" }, "" },
        new object[] { "\u001b_G,i=1,;AA" + St, new[] { "B:,i=1,", "P:AA", "E:True" }, "" },
        new object[] { "\u001b_Gi=1;AB" + St + "x\u001b_Ga=p;CD\u009cy", new[] { "B:i=1", "P:AB", "E:True", "B:a=p", "P:CD", "E:True" }, "xy" },
        new object[] { "a\u001b_Gi=1;AB" + St + "b", new[] { "B:i=1", "P:AB", "E:True" }, "ab" },
    };

    [Theory]
    [MemberData(nameof(CompleteCommands))]
    public void CompleteCommand_IsDeliveredIdentically_AtEverySplit(string input, string[] expectedEvents, string expectedText)
    {
        var whole = Run(Bytes(input), enabled: true);
        Assert.Equal(expectedEvents, Canon(whole.KittyEvents));
        Assert.Equal(expectedText, Printed(whole));
        Assert.Empty(whole.ControlCalls);
        AssertSplitInvariant(input);
    }

    public static IEnumerable<object[]> AbortedCommands() => new[]
    {
        // other C0 control: End(false), control re-processed as normal control
        new object[] { "\u001b_Gi=1;AAAA\ntail", new[] { "B:i=1", "P:AAAA", "E:False" }, "tail", new[] { "LF" } },
        new object[] { "\u001b_Gi=1;\u0007tail", new[] { "B:i=1", "E:False" }, "tail", new[] { "BEL" } },
        new object[] { "\u001b_Gi=1;\r\ntail", new[] { "B:i=1", "E:False" }, "tail", new[] { "CR", "LF" } },
        // CAN / SUB: End(false), swallowed
        new object[] { "\u001b_Gi=1;AAAA\u0018tail", new[] { "B:i=1", "P:AAAA", "E:False" }, "tail", Array.Empty<string>() },
        new object[] { "\u001b_Gi=1;AAAA\u001atail", new[] { "B:i=1", "P:AAAA", "E:False" }, "tail", Array.Empty<string>() },
        // NUL aborts too, executes as nothing, and the rest is ordinary text
        new object[] { "\u001b_Gi=1;AA\0BB" + St + "tail", new[] { "B:i=1", "P:AA", "E:False" }, "BBtail", Array.Empty<string>() },
        // ESC + other: End(false), ESC starts a fresh sequence (here CSI 2 A)
        new object[] { "\u001b_Gi=1;AA\u001b[2Atail", new[] { "B:i=1", "P:AA", "E:False" }, "tail", Array.Empty<string>() },
        // ESC ESC: abort; the second ESC owns the following byte
        new object[] { "\u001b_Gi=1;AA\u001b\u001b\\tail", new[] { "B:i=1", "P:AA", "E:False" }, "tail", Array.Empty<string>() },
        // Aborts before the control data finished: Begin is never called
        new object[] { "\u001b_Gi=1\ntail", Array.Empty<string>(), "tail", new[] { "LF" } },
        new object[] { "\u001b_Gi=1\u0018tail", Array.Empty<string>(), "tail", Array.Empty<string>() },
        new object[] { "\u001b_Gi=1\u001atail", Array.Empty<string>(), "tail", Array.Empty<string>() },
        new object[] { "\u001b_Gi=1\u001b[3Btail", Array.Empty<string>(), "tail", Array.Empty<string>() },
        new object[] { "\u001b_Gi=1\u001bctail", Array.Empty<string>(), "tail", Array.Empty<string>() },
    };

    [Theory]
    [MemberData(nameof(AbortedCommands))]
    public void AbortedCommand_PairsBeginWithEnd_AndRecoversAtEverySplit(
        string input, string[] expectedEvents, string expectedText, string[] expectedControls)
    {
        var whole = Run(Bytes(input), enabled: true);
        Assert.Equal(expectedEvents, Canon(whole.KittyEvents));
        Assert.Equal(expectedText, Printed(whole));
        Assert.Equal(expectedControls, whole.ControlCalls);
        Assert.Equal(whole.KittyBeginCount, whole.KittyEndCount);
        AssertSplitInvariant(input);
    }

    [Fact]
    public void AbortingEscape_IsInterpretedAsFreshSequence()
    {
        var h = Run(Bytes("\u001b_Gi=1;AA\u001b[2Atail\u001b_Gi=2;BB\u001b[3Bx"), enabled: true);
        Assert.Equal(new[] { 2 }, h.CursorUpCalls);
        Assert.Equal(new[] { 3 }, h.CursorDownCalls);
        Assert.Equal("tailx", Printed(h));
        Assert.Equal(new[] { "B:i=1", "P:AA", "E:False", "B:i=2", "P:BB", "E:False" }, Canon(h.KittyEvents));
    }

    [Fact]
    public void ResetViaEsc_C_DuringPayload_EndsCommandAndAppliesReset()
    {
        // ESC c is "ESC + other": the command is cancelled and the full reset reaches the handler path.
        var h = Run(Bytes("\u001b_Gi=1;AA\u001bc"), enabled: true);
        Assert.Equal(new[] { "B:i=1", "P:AA", "E:False" }, Canon(h.KittyEvents));
    }

    [Theory]
    [InlineData("\u001b_Gi=1;AAAA" + St + "tail")]
    [InlineData("\u001b_Gi=1;AAAA\u009ctail")]
    [InlineData("\u001b_Ga=q;AAAA" + St + "tail")]
    public void EnabledOrNot_PayloadIsNeverPrinted(string input)
    {
        foreach (bool enabled in new[] { true, false })
        {
            var h = Run(Bytes(input), enabled);
            Assert.DoesNotContain("AAAA", Printed(h));
            Assert.DoesNotContain("i=1", Printed(h));
        }
    }

    [Fact]
    public void Disabled_Discards_WithoutCallbacksOrText_AndRecovers()
    {
        var h = Run(Bytes("\u001b_Gi=1,a=T;AAAA" + St + "one\u001b_Ga=q" + St + "two"), enabled: false);
        Assert.Empty(h.KittyEvents);
        Assert.Equal("onetwo", Printed(h));
        AssertSplitInvariant("\u001b_Gi=1,a=T;AAAA" + St + "one\u001b_Ga=q" + St + "two", enabled: false);
    }

    [Fact]
    public void Disabled_StillHonoursAbortRules()
    {
        var h = Run(Bytes("\u001b_Gi=1;AA\ntail"), enabled: false);
        Assert.Empty(h.KittyEvents);
        Assert.Equal(new[] { "LF" }, h.ControlCalls);
        Assert.Equal("tail", Printed(h));
    }

    [Theory]
    [InlineData("\u001b_Xfoo" + St + "tail")]
    [InlineData("\u001b_xfoo;AA" + St + "tail")]
    [InlineData("\u001b_g;AA" + St + "tail")]
    [InlineData("\u001b_ ;AA" + St + "tail")]
    [InlineData("\u001b_" + St + "tail")]
    [InlineData("\u001b^Gi=1;AA" + St + "tail")]
    [InlineData("\u001bXGi=1;AA" + St + "tail")]
    [InlineData("\u001b_\u0018tail")]
    public void NonGraphicsStrings_AreSwallowed_EvenWhenEnabled(string input)
    {
        var h = Run(Bytes(input), enabled: true);
        Assert.Empty(h.KittyEvents);
        Assert.Equal("tail", Printed(h));
        AssertSplitInvariant(input);
    }

    [Fact]
    public void NonGraphicsApc_DoesNotTerminateOn0x9C()
    {
        // Matches the existing discard behaviour: only ESC \ ends a discarded string.
        var h = Run(Bytes("\u001b_Xab\u009ccd" + St + "tail"), enabled: true);
        Assert.Equal("tail", Printed(h));
    }

    [Fact]
    public void NoHandler_DoesNotThrow()
    {
        var parser = new BasicAnsiParser();
        parser.Feed(Bytes("\u001b_Gi=1;AA" + St + "\u001b_Gi=2;BB\nx"));
    }

    [Fact]
    public void ControlOf512Bytes_IsAccepted()
    {
        string control = "i=1," + new string('k', 512 - 4);
        Assert.Equal(512, control.Length);
        var h = Run(Bytes("\u001b_G" + control + ";AA" + St + "tail"), enabled: true);
        Assert.Equal(new[] { "B:" + control, "P:AA", "E:True" }, Canon(h.KittyEvents));
        Assert.Equal("tail", Printed(h));
    }

    [Theory]
    [InlineData(";AA")]
    [InlineData("")]
    [InlineData(";AA\n")]
    public void ControlOf513Bytes_NeverBegins_AndParserRecovers(string suffix)
    {
        string control = new string('k', 513);
        string input = "\u001b_G" + control + suffix + St + "tail\u001b_Gi=2;ZZ" + St + "end";
        var h = Run(Bytes(input), enabled: true);
        // The oversized command is dropped entirely; a later command still works.
        Assert.Equal(new[] { "B:i=2", "P:ZZ", "E:True" }, Canon(h.KittyEvents));
        Assert.Equal(1, h.KittyBeginCount);
        Assert.Equal(1, h.KittyEndCount);
        Assert.Equal(suffix.EndsWith('\n') ? new[] { "LF" } : Array.Empty<string>(), h.ControlCalls);
        Assert.Equal("tailend", Printed(h));
        AssertSplitInvariant(input);
    }

    [Fact]
    public void LargePayload_ArrivesComplete_InArbitraryChunks()
    {
        var rng = new Random(1234);
        var payload = new StringBuilder();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";
        for (int i = 0; i < 200_000; i++)
            payload.Append(alphabet[rng.Next(alphabet.Length)]);

        byte[] data = Bytes("\u001b_Ga=t,f=100;" + payload + St + "tail");
        var handler = NewHandler();
        var parser = new BasicAnsiParser { Handler = handler };
        for (int pos = 0; pos < data.Length;)
        {
            int n = Math.Min(data.Length - pos, 1 + rng.Next(9000));
            parser.Feed(data.AsSpan(pos, n));
            pos += n;
        }

        Assert.Equal(new[] { "B:a=t,f=100", "P:" + payload, "E:True" }, Canon(handler.KittyEvents));
        Assert.Equal(payload.Length, handler.KittyPayloadBytes);
        Assert.Equal("tail", Printed(handler));
    }

    [Fact]
    public void PendingUtf8InGround_IsFlushedBeforeAnApc_AndDoesNotLeakIntoIt()
    {
        var h = Run(new byte[] { 0xE2, 0x82 }.Concat(Bytes("\u001b_Gi=1;\u0080A" + St + "x")).ToArray(), enabled: true);
        Assert.Equal(new[] { "B:i=1", "P:\u0080A", "E:True" }, Canon(h.KittyEvents));
        Assert.EndsWith("x", Printed(h));
    }

    [Fact]
    public void BeginIsCalledOncePerCommand_AcrossManyPayloadFeeds()
    {
        var h = NewHandler();
        var parser = new BasicAnsiParser { Handler = h };
        parser.Feed(Bytes("\u001b_Gm=1,i=5;"));
        for (int i = 0; i < 50; i++)
            parser.Feed(Bytes("QUJD"));
        Assert.Equal(1, h.KittyBeginCount);
        Assert.Equal(0, h.KittyEndCount);
        parser.Feed(Bytes(St));
        Assert.Equal(1, h.KittyEndCount);
        Assert.Equal(200, h.KittyPayloadBytes);
    }

    [Fact]
    public void RandomisedStreams_KeepBeginEndPairing_AndAreSplitInvariant()
    {
        string[] atoms =
        {
            "\u001b", "\\", "_", "G", ";", "A", "Q", "=", ",", "i=1", "\n", "\r", "\u0018", "\u001a", "\u009c",
            "\u007f", "[", "c", "x", "\u001b_G", "\u001b\\", "\0", "\u0007",
        };
        var rng = new Random(77);
        for (int round = 0; round < 400; round++)
        {
            var sb = new StringBuilder();
            int len = 1 + rng.Next(40);
            for (int i = 0; i < len; i++)
                sb.Append(atoms[rng.Next(atoms.Length)]);
            string input = sb.ToString();
            byte[] data = Bytes(input);

            var whole = Run(data, enabled: true);
            // Pairing: strictly B (P)* E sequences, with at most one command left open at the end.
            bool open = false;
            foreach (string e in whole.KittyEvents)
            {
                switch (e[0])
                {
                    case 'B': Assert.False(open, input); open = true; break;
                    case 'P': Assert.True(open, input); break;
                    case 'E': Assert.True(open, input); open = false; break;
                }
            }

            Assert.Equal(whole.KittyBeginCount, whole.KittyEndCount + (open ? 1 : 0));

            string expected = Describe(whole);
            Assert.Equal(expected, Describe(Run(data, true, Enumerable.Range(1, Math.Max(0, data.Length - 1)).ToArray())));
            int cut = 1 + rng.Next(Math.Max(1, data.Length - 1));
            if (cut < data.Length)
                Assert.True(expected == Describe(Run(data, true, cut)), $"round {round} cut {cut}");
        }
    }

    [Fact]
    public void ExistingBehaviour_TextAroundOtherSequencesIsUnchanged()
    {
        var h = Run(Bytes("a\u001b[2Ab\u001b]0;t\u0007c\u001b_Gi=1;AA" + St + "d"), enabled: true);
        Assert.Equal("abcd", Printed(h));
        Assert.Equal(new[] { 2 }, h.CursorUpCalls);
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class KittyApcParserAllocationTests
{
    [Fact]
    public void CapturedAbortedAndOverflowingCommands_DoNotAllocateInSteadyState()
    {
        var handler = new BoundedSequenceTests.RecordingHandler { KittyEnabled = true, KittyCountOnly = true };
        var parser = new BasicAnsiParser { Handler = handler };

        var sb = new StringBuilder();
        sb.Append("\u001b_Ga=T,i=1;").Append('A', 70_000).Append("\u001b\\");
        sb.Append("\u001b_Ga=T;ABCD\u009c");
        sb.Append("\u001b_Gi=1;AA\u0018");
        sb.Append("\u001b_Gi=1;AA\u001bc");
        sb.Append("\u001b_G").Append('k', 600).Append(";AA\u001b\\");
        sb.Append("\u001b_Xabc\u001b\\");
        byte[] stream = Encoding.Latin1.GetBytes(sb.ToString());

        AllocationAssert.NoAllocations(
            () => parser.Feed(stream),
            warmupIterations: 8,
            measuredIterationsPerWindow: 32,
            windows: 5);

        Assert.True(handler.KittyBeginCount > 0);
        Assert.Equal(handler.KittyBeginCount, handler.KittyEndCount);
    }
}
