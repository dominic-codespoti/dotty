using System;
using System.Text;
using Dotty.Runtime.Input;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>DECBKM (mode 67) selection of the legacy Backspace byte.</summary>
public class BackarrowKeyTests
{
    private static string Encode(TerminalKeyModifiers modifiers, bool decbkm)
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[32];
        int length = encoder.Encode(TerminalKey.Backspace, modifiers, buffer, backarrowSendsBackspace: decbkm);
        return Encoding.ASCII.GetString(buffer[..length]);
    }

    [Theory]
    [InlineData(TerminalKeyModifiers.None, false, "\x7f")]
    [InlineData(TerminalKeyModifiers.None, true, "\x08")]
    [InlineData(TerminalKeyModifiers.Alt, false, "\x1b\x7f")]
    [InlineData(TerminalKeyModifiers.Alt, true, "\x1b\x08")]
    [InlineData(TerminalKeyModifiers.Control, false, "\x17")]
    [InlineData(TerminalKeyModifiers.Control, true, "\x17")]
    [InlineData(TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt, false, "\x1b\x17")]
    [InlineData(TerminalKeyModifiers.Control | TerminalKeyModifiers.Alt, true, "\x1b\x17")]
    public void Encode_Backspace_Matrix(TerminalKeyModifiers modifiers, bool decbkm, string expected)
    {
        Assert.Equal(expected, Encode(modifiers, decbkm));
    }

    [Fact]
    public void EncodeKeyEvent_LegacyFallbackHonorsDecbkm()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[32];
        int length = encoder.EncodeKeyEvent(TerminalKey.Backspace, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, "".AsSpan(), buffer, 0, backarrowSendsBackspace: true);
        Assert.Equal("\x08", Encoding.ASCII.GetString(buffer[..length]));
        length = encoder.EncodeKeyEvent(TerminalKey.Backspace, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, "".AsSpan(), buffer, 0);
        Assert.Equal("\x7f", Encoding.ASCII.GetString(buffer[..length]));
    }

    [Fact]
    public void EncodeKeyEvent_KittyFlagsIgnoreDecbkm()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[32];
        int length = encoder.EncodeKeyEvent(TerminalKey.Backspace, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, "".AsSpan(), buffer, 8, backarrowSendsBackspace: true);
        Assert.Equal("\x1b[127;1u", Encoding.ASCII.GetString(buffer[..length]));
    }

    [Fact]
    public void AdapterFlow_DecbkmDrivesEncoder()
    {
        var adapter = new TerminalAdapter(rows: 4, columns: 10);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.ASCII.GetBytes("\u001b[?67h"));
        Assert.Equal("\x08", Encode(TerminalKeyModifiers.None, adapter.BackarrowSendsBackspace));
        adapter.ResetBackarrowKeyMode();
        Assert.Equal("\x7f", Encode(TerminalKeyModifiers.None, adapter.BackarrowSendsBackspace));
    }
}
