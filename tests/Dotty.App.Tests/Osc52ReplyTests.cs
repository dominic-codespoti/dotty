using System;
using System.Text;
using Dotty.Abstractions.Clipboard;
using Dotty.Runtime.Clipboard;
using Xunit;

namespace Dotty.App.Tests;

public sealed class Osc52ReplyTests
{
    private const string Esc = "\u001b";
    private const string St = "\u001b\\";

    private static string AsText(byte[] reply) => Encoding.ASCII.GetString(reply);

    private static byte[] DecodePayload(byte[] reply)
    {
        string text = AsText(reply);
        Assert.StartsWith(Esc + "]52;", text, StringComparison.Ordinal);
        Assert.EndsWith(St, text, StringComparison.Ordinal);
        // ESC ] 5 2 ; <letter> ; <base64> ESC \
        return Convert.FromBase64String(text.Substring(7, text.Length - 9));
    }

    [Fact]
    public void Clipboard_EncodesCLetterBase64AndStTerminator()
    {
        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, "hi"u8, out byte[] reply));

        Assert.Equal(Esc + "]52;c;aGk=" + St, AsText(reply));
    }

    [Fact]
    public void Primary_EncodesPLetter()
    {
        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Primary, "hi"u8, out byte[] reply));

        Assert.Equal(Esc + "]52;p;aGk=" + St, AsText(reply));
    }

    [Fact]
    public void SelectCutBufferReply_PreservesS0Selector()
    {
        Assert.True(Osc52Reply.TryEncode("s0", "hi"u8, out byte[] reply));

        Assert.Equal(Esc + "]52;s0;aGk=" + St, AsText(reply));
    }

    [Fact]
    public void BothSelections_PreferClipboardLetter()
    {
        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard | ClipboardSelection.Primary, "hi"u8, out byte[] reply));

        Assert.Equal(Esc + "]52;c;aGk=" + St, AsText(reply));
    }

    [Fact]
    public void NoSelection_ReturnsFalseAndEmptyReply()
    {
        Assert.False(Osc52Reply.TryEncode(ClipboardSelection.None, "hi"u8, out byte[] reply));

        Assert.Empty(reply);
    }

    [Fact]
    public void EmptyPayload_IsAValidReply()
    {
        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, ReadOnlySpan<byte>.Empty, out byte[] reply));

        Assert.Equal(Esc + "]52;c;" + St, AsText(reply));
    }

    [Fact]
    public void Base64PaddingIsEmittedForEveryRemainder()
    {
        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, "a"u8, out byte[] one));
        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, "ab"u8, out byte[] two));
        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, "abc"u8, out byte[] three));

        Assert.Equal(Esc + "]52;c;YQ==" + St, AsText(one));
        Assert.Equal(Esc + "]52;c;YWI=" + St, AsText(two));
        Assert.Equal(Esc + "]52;c;YWJj" + St, AsText(three));
    }

    [Fact]
    public void UnicodePayload_RoundTripsThroughBase64()
    {
        const string text = "h\u00e9llo \u2713 \U0001F600\r\nline two";
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, utf8, out byte[] reply));

        Assert.Equal(text, Encoding.UTF8.GetString(DecodePayload(reply)));
    }

    [Fact]
    public void ControlBytesInPayloadStayInsideBase64()
    {
        byte[] payload = { 0x1B, (byte)']', (byte)'0', (byte)';', 0x07, 0x00, 0xFF };

        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, payload, out byte[] reply));

        // Only the framing may contain control bytes; the encoded payload is plain ASCII.
        for (int i = 7; i < reply.Length - 2; i++)
            Assert.InRange(reply[i], (byte)'+', (byte)'z');
        Assert.Equal(payload, DecodePayload(reply));
    }

    [Fact]
    public void OneMebibytePayload_HasExactEncodedLength()
    {
        byte[] payload = new byte[1024 * 1024];
        Array.Fill(payload, (byte)'a');

        Assert.True(Osc52Reply.TryEncode(ClipboardSelection.Clipboard, payload, out byte[] reply));

        int encoded = (payload.Length + 2) / 3 * 4;
        Assert.Equal(7 + encoded + 2, reply.Length);
        Assert.Equal(0x1B, reply[0]);
        Assert.Equal((byte)'\\', reply[^1]);
        Assert.Equal(payload.Length, DecodePayload(reply).Length);
    }
}
