using System;
using System.Linq;
using System.Text;
using Dotty.Runtime.Input;
using Xunit;

namespace Dotty.App.Tests;

[Collection(nameof(SessionAllocationCollection))]
public sealed class PasteEncoderTests
{
    private const string Start = "\x1b[200~";
    private const string End = "\x1b[201~";

    private static string Text(string s, bool bracketed) => Encoding.UTF8.GetString(PasteEncoder.Encode(s, bracketed));

    private static string Body(string s) => Text(s, true)[Start.Length..^End.Length];

    [Fact]
    public void Ascii_IsFramedOnlyWhenBracketed()
    {
        Assert.Equal(Start + "ls -la" + End, Text("ls -la", true));
        Assert.Equal("ls -la", Text("ls -la", false));
    }

    public static TheoryData<int> ControlCodes()
    {
        var data = new TheoryData<int>();
        for (int c = 0; c < 0x20; c++)
            if (c != '\t' && c != '\n' && c != '\r')
                data.Add(c);
        data.Add(0x7F);
        return data;
    }

    [Theory]
    [MemberData(nameof(ControlCodes))]
    public void C0AndDel_BecomeSpaces(int code)
    {
        string input = "a" + (char)code + "b";
        Assert.Equal("a b", Body(input));
        Assert.Equal("a b", Text(input, false));
    }

    [Fact]
    public void TabLfCr_ArePreservedWhenBracketed()
    {
        Assert.Equal("a\tb\nc\rd", Body("a\tb\nc\rd"));
    }

    [Theory]
    [InlineData(0x80)]
    [InlineData(0x85)]
    [InlineData(0x9B)]
    [InlineData(0x9F)]
    public void C1_BecomeSingleSpace(int code)
    {
        byte[] bytes = PasteEncoder.Encode("a" + (char)code + "b", false);
        Assert.Equal(new byte[] { 0x61, 0x20, 0x62 }, bytes);
    }

    [Fact]
    public void EveryC1_BecomesSpace()
    {
        for (int c = 0x80; c <= 0x9F; c++)
            Assert.Equal(" ", Body(((char)c).ToString()));
    }

    [Fact]
    public void EndMarkerInjection_IsNeutralised()
    {
        string output = Text("a\x1b[201~touch /tmp/pwned\n", true);
        Assert.EndsWith(End, output);
        Assert.Equal(output.IndexOf(End, StringComparison.Ordinal), output.LastIndexOf(End, StringComparison.Ordinal));
        Assert.Equal("a [201~touch /tmp/pwned\n", output[Start.Length..^End.Length]);
    }

    [Fact]
    public void C1CsiEndMarker_IsNeutralised()
    {
        string body = Body("\u009b201~");
        Assert.Equal(" 201~", body);
        Assert.DoesNotContain((byte)0x9B, PasteEncoder.Encode("\u009b201~", true));
    }

    [Fact]
    public void InnerStartMarker_IsNeutralised()
    {
        Assert.Equal(" [200~", Body("\x1b[200~"));
    }

    [Fact]
    public void GhosttyControlList_IsSubsetOfNeutralisedSet()
    {
        foreach (byte b in new byte[] { 0x00, 0x08, 0x05, 0x04, 0x1B, 0x7F, 0x03, 0x1C, 0x15, 0x1A, 0x11, 0x13, 0x17, 0x16, 0x12, 0x0F })
            Assert.True(PasteEncoder.IsNeutralisedControl((char)b));
    }

    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\u00a0b")]
    [InlineData("a\u2028b\u2029")]
    [InlineData("你好")]
    [InlineData("x😀y")]
    [InlineData("e\u0301")]
    public void NonControls_AreKeptVerbatim(string input)
    {
        Assert.Equal(Encoding.UTF8.GetBytes(input), PasteEncoder.Encode(input, false));
        Assert.Equal(input, Body(input));
    }

    [Theory]
    [InlineData("a\nb", "a\rb")]
    [InlineData("a\r\nb", "a\rb")]
    [InlineData("a\rb", "a\rb")]
    [InlineData("\n\n", "\r\r")]
    [InlineData("a\n\rb", "a\r\rb")]
    [InlineData("x\r\n", "x\r")]
    public void UnbracketedNewlines_BecomeCr(string input, string expected)
    {
        Assert.Equal(expected, Text(input, false));
    }

    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    public void BracketedNewlines_AreUnchanged(string input)
    {
        Assert.Equal(input, Body(input));
    }

    [Fact]
    public void NullAndEmpty_ProduceNoBytes()
    {
        Assert.Empty(PasteEncoder.Encode(string.Empty, true));
        Assert.Empty(PasteEncoder.Encode(string.Empty, false));
        Assert.Empty(PasteEncoder.Encode(null!, true));
    }

    [Fact]
    public void OnlyControl_BecomesSpace()
    {
        Assert.Equal(" ", Text("\x1b", false));
        Assert.Equal(Start + " " + End, Text("\x1b", true));
    }

    [Fact]
    public void LoneSurrogate_BecomesReplacementCharacter()
    {
        Assert.Equal(new byte[] { 0xEF, 0xBF, 0xBD }, PasteEncoder.Encode("\ud800", false));
    }

    [Fact]
    public void RandomStrings_MatchNaiveReference()
    {
        var rng = new Random(12345);
        char[] alphabet = Enumerable.Range(0, 0x20).Select(i => (char)i)
            .Concat(new[] { '[', '2', '0', '1', '~', 'a', 'Z', ' ', '\u007f', '\u0085', '\u009b', '\u00a0' }).ToArray();
        int[] lengths = { 0, 1, 255, 256, 257, 4096 };
        for (int n = 0; n < 2000; n++)
        {
            int len = n < lengths.Length ? lengths[n] : rng.Next(0, 601);
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++)
            {
                if (rng.Next(40) == 0) sb.Append("😀");
                else sb.Append(alphabet[rng.Next(alphabet.Length)]);
            }

            string input = sb.ToString();
            foreach (bool bracketed in new[] { false, true })
            {
                string expected = Reference(input, bracketed);
                Assert.Equal(Encoding.UTF8.GetBytes(expected), PasteEncoder.Encode(input, bracketed));
            }
        }
    }

    private static string Reference(string input, bool bracketed)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (!bracketed && c == '\n') sb.Append('\r');
            else if (!bracketed && c == '\r')
            {
                sb.Append('\r');
                if (i + 1 < input.Length && input[i + 1] == '\n') i++;
            }
            else if ((c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0x7f || (c >= 0x80 && c <= 0x9f)) sb.Append(' ');
            else sb.Append(c);
        }

        string body = sb.ToString();
        if (input.Length == 0) return string.Empty;
        return bracketed ? Start + body + End : body;
    }

    [Fact]
    public void OneMebibytePaste_AllocatesOnlyResultArray()
    {
        string input = new string('x', 1024 * 1024 - 2) + "\r\n";
        _ = PasteEncoder.Encode(input, true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] result = PasteEncoder.Encode(input, true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Start.Length + input.Length + End.Length, result.Length);
        Assert.True(allocated <= result.Length + 16 * 1024, $"allocated {allocated}");
    }
}
