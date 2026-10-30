using System;
using Dotty.Runtime.Input;
using Xunit;
using Encoding = Dotty.Terminal.Adapter.TerminalAdapter.MouseEncoding;
using Mode = Dotty.Terminal.Adapter.TerminalAdapter.MouseMode;

namespace Dotty.App.Tests;

/// <summary>Golden bytes for <see cref="MouseEncoder"/>: tracking modes, encodings, coordinate limits, motion de-duplication.</summary>
public sealed class MouseEncoderTests
{
    private static string? EncodeHex(in MouseReport report)
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        int length = MouseEncoder.Encode(report, buffer);
        return length == 0 ? null : Convert.ToHexString(buffer[..length]);
    }

    private static string? EncodeText(in MouseReport report)
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        int length = MouseEncoder.Encode(report, buffer);
        return length == 0 ? null : System.Text.Encoding.Latin1.GetString(buffer[..length]);
    }

    private static MouseReport LeftPressAt(Encoding encoding, int position) =>
        new(Mode.Normal, encoding, 0, MouseEventKind.Press, position - 1, 0, position - 1, 0, TerminalKeyModifiers.None);

    // Byte encodings: ESC [ M, Cb+32, Cx+32, Cy+32 with y = 1 (0x21). 1005 writes values >= 128 as 2-byte UTF-8.
    [Theory]
    [InlineData(Encoding.Default, 1, "1B5B4D 20 21 21")]
    [InlineData(Encoding.Default, 95, "1B5B4D 20 7F 21")]
    [InlineData(Encoding.Default, 96, "1B5B4D 20 80 21")]
    [InlineData(Encoding.Default, 127, "1B5B4D 20 9F 21")]
    [InlineData(Encoding.Default, 128, "1B5B4D 20 A0 21")]
    [InlineData(Encoding.Default, 222, "1B5B4D 20 FE 21")]
    [InlineData(Encoding.Default, 223, "1B5B4D 20 FF 21")]
    [InlineData(Encoding.Default, 224, null)]
    [InlineData(Encoding.Default, 2015, null)]
    [InlineData(Encoding.Default, 2016, null)]
    [InlineData(Encoding.UTF8, 1, "1B5B4D 20 21 21")]
    [InlineData(Encoding.UTF8, 95, "1B5B4D 20 7F 21")]
    [InlineData(Encoding.UTF8, 96, "1B5B4D 20 C280 21")]
    [InlineData(Encoding.UTF8, 127, "1B5B4D 20 C29F 21")]
    [InlineData(Encoding.UTF8, 128, "1B5B4D 20 C2A0 21")]
    [InlineData(Encoding.UTF8, 222, "1B5B4D 20 C3BE 21")]
    [InlineData(Encoding.UTF8, 223, "1B5B4D 20 C3BF 21")]
    [InlineData(Encoding.UTF8, 224, "1B5B4D 20 C480 21")]
    [InlineData(Encoding.UTF8, 2015, "1B5B4D 20 DFBF 21")]
    [InlineData(Encoding.UTF8, 2016, null)]
    public void ByteEncodings_ColumnBoundaries(Encoding encoding, int column, string? expectedHex)
    {
        Assert.Equal(expectedHex?.Replace(" ", ""), EncodeHex(LeftPressAt(encoding, column)));
    }

    [Theory]
    [InlineData(Encoding.Default, 223, "1B5B4D 20 21 FF")]
    [InlineData(Encoding.Default, 224, null)]
    [InlineData(Encoding.UTF8, 2015, "1B5B4D 20 21 DFBF")]
    [InlineData(Encoding.UTF8, 2016, null)]
    public void ByteEncodings_RowBoundaries(Encoding encoding, int row, string? expectedHex)
    {
        var report = new MouseReport(Mode.Normal, encoding, 0, MouseEventKind.Press, 0, row - 1, 0, 0, TerminalKeyModifiers.None);
        Assert.Equal(expectedHex?.Replace(" ", ""), EncodeHex(report));
    }

    // Decimal encodings have no coordinate limit; SGR-pixels reports 1-based pixels like xterm.
    [Theory]
    [InlineData(Encoding.URXVT, 1, "\x1b[32;1;1M")]
    [InlineData(Encoding.URXVT, 95, "\x1b[32;95;1M")]
    [InlineData(Encoding.URXVT, 223, "\x1b[32;223;1M")]
    [InlineData(Encoding.URXVT, 224, "\x1b[32;224;1M")]
    [InlineData(Encoding.URXVT, 2015, "\x1b[32;2015;1M")]
    [InlineData(Encoding.URXVT, 2016, "\x1b[32;2016;1M")]
    [InlineData(Encoding.SGR, 1, "\x1b[<0;1;1M")]
    [InlineData(Encoding.SGR, 95, "\x1b[<0;95;1M")]
    [InlineData(Encoding.SGR, 223, "\x1b[<0;223;1M")]
    [InlineData(Encoding.SGR, 224, "\x1b[<0;224;1M")]
    [InlineData(Encoding.SGR, 2015, "\x1b[<0;2015;1M")]
    [InlineData(Encoding.SGR, 2016, "\x1b[<0;2016;1M")]
    [InlineData(Encoding.SGRPixels, 1, "\x1b[<0;1;1M")]
    [InlineData(Encoding.SGRPixels, 224, "\x1b[<0;224;1M")]
    [InlineData(Encoding.SGRPixels, 2016, "\x1b[<0;2016;1M")]
    public void DecimalEncodings_ColumnBoundaries(Encoding encoding, int column, string expected)
    {
        Assert.Equal(expected, EncodeText(LeftPressAt(encoding, column)));
    }

    [Fact]
    public void SgrPixels_ReportsPixelPositionNotCell()
    {
        var report = new MouseReport(Mode.Normal, Encoding.SGRPixels, 0, MouseEventKind.Press,
            Column: 4, Row: 2, PixelX: 41, PixelY: 37, TerminalKeyModifiers.None);
        Assert.Equal("\x1b[<0;42;38M", EncodeText(report));
        Assert.Equal("\x1b[<0;42;38m", EncodeText(report with { Kind = MouseEventKind.Release }));
    }

    // Mode x event x button x modifiers at cell (column 4, row 2) => x = 5, y = 3.
    [Theory]
    [InlineData(Mode.None, Encoding.SGR, 0, MouseEventKind.Press, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.X10, Encoding.SGR, 0, MouseEventKind.Press, TerminalKeyModifiers.None, "\x1b[<0;5;3M")]
    [InlineData(Mode.X10, Encoding.SGR, 2, MouseEventKind.Press, TerminalKeyModifiers.Control | TerminalKeyModifiers.Shift, "\x1b[<2;5;3M")]
    [InlineData(Mode.X10, Encoding.Default, 1, MouseEventKind.Press, TerminalKeyModifiers.Alt, "\x1b[M!%#")]
    [InlineData(Mode.X10, Encoding.SGR, 0, MouseEventKind.Release, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.X10, Encoding.SGR, 0, MouseEventKind.Motion, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.X10, Encoding.SGR, MouseEncoder.WheelUp, MouseEventKind.Press, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.Normal, Encoding.SGR, 0, MouseEventKind.Press, TerminalKeyModifiers.None, "\x1b[<0;5;3M")]
    [InlineData(Mode.Normal, Encoding.SGR, 2, MouseEventKind.Release, TerminalKeyModifiers.None, "\x1b[<2;5;3m")]
    [InlineData(Mode.Normal, Encoding.Default, 2, MouseEventKind.Release, TerminalKeyModifiers.None, "\x1b[M#%#")]
    [InlineData(Mode.Normal, Encoding.UTF8, 2, MouseEventKind.Release, TerminalKeyModifiers.None, "\x1b[M#%#")]
    [InlineData(Mode.Normal, Encoding.URXVT, 2, MouseEventKind.Release, TerminalKeyModifiers.None, "\x1b[35;5;3M")]
    [InlineData(Mode.Normal, Encoding.SGR, 0, MouseEventKind.Motion, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.Normal, Encoding.SGR, MouseEncoder.ButtonNone, MouseEventKind.Press, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.Normal, Encoding.SGR, MouseEncoder.WheelUp, MouseEventKind.Press, TerminalKeyModifiers.None, "\x1b[<64;5;3M")]
    [InlineData(Mode.Normal, Encoding.SGR, MouseEncoder.WheelDown, MouseEventKind.Press, TerminalKeyModifiers.Control, "\x1b[<81;5;3M")]
    [InlineData(Mode.Normal, Encoding.SGR, MouseEncoder.WheelLeft, MouseEventKind.Press, TerminalKeyModifiers.None, "\x1b[<66;5;3M")]
    [InlineData(Mode.Normal, Encoding.SGR, MouseEncoder.WheelRight, MouseEventKind.Press, TerminalKeyModifiers.None, "\x1b[<67;5;3M")]
    [InlineData(Mode.Normal, Encoding.Default, MouseEncoder.WheelUp, MouseEventKind.Press, TerminalKeyModifiers.None, "\x1b[M`%#")]
    [InlineData(Mode.Normal, Encoding.SGR, MouseEncoder.WheelUp, MouseEventKind.Release, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.Normal, Encoding.SGR, 0, MouseEventKind.Press, TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control, "\x1b[<28;5;3M")]
    [InlineData(Mode.Normal, Encoding.SGR, 0, MouseEventKind.Press, TerminalKeyModifiers.Meta, "\x1b[<0;5;3M")]
    [InlineData(Mode.ButtonEvent, Encoding.SGR, 0, MouseEventKind.Motion, TerminalKeyModifiers.None, "\x1b[<32;5;3M")]
    [InlineData(Mode.ButtonEvent, Encoding.SGR, MouseEncoder.ButtonNone, MouseEventKind.Motion, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.AnyEvent, Encoding.SGR, MouseEncoder.ButtonNone, MouseEventKind.Motion, TerminalKeyModifiers.None, "\x1b[<35;5;3M")]
    [InlineData(Mode.AnyEvent, Encoding.SGR, 2, MouseEventKind.Motion, TerminalKeyModifiers.Shift, "\x1b[<38;5;3M")]
    [InlineData(Mode.AnyEvent, Encoding.UTF8, MouseEncoder.ButtonNone, MouseEventKind.Motion, TerminalKeyModifiers.None, "\x1b[MC%#")]
    [InlineData(Mode.AnyEvent, Encoding.URXVT, MouseEncoder.ButtonNone, MouseEventKind.Motion, TerminalKeyModifiers.None, "\x1b[67;5;3M")]
    [InlineData(Mode.AnyEvent, Encoding.SGR, MouseEncoder.WheelUp, MouseEventKind.Motion, TerminalKeyModifiers.None, null)]
    [InlineData(Mode.Normal, Encoding.SGR, 4, MouseEventKind.Press, TerminalKeyModifiers.None, null)]
    public void ModesEventsAndModifiers(Mode mode, Encoding encoding, int button, MouseEventKind kind,
        TerminalKeyModifiers modifiers, string? expected)
    {
        var report = new MouseReport(mode, encoding, button, kind, Column: 4, Row: 2, PixelX: 0, PixelY: 0, modifiers);
        Assert.Equal(expected, EncodeText(report));
    }

    [Fact]
    public void MotionFilter_DropsRepeatedCellMotionButNeverPressOrRelease()
    {
        var filter = new MouseMotionFilter();
        MouseReport At(MouseEventKind kind, int column, int row) =>
            new(Mode.AnyEvent, Encoding.SGR, 0, kind, column, row, column * 9 + 3, row * 17 + 5, TerminalKeyModifiers.None);

        Assert.True(filter.Admit(At(MouseEventKind.Press, 1, 1)));
        Assert.False(filter.Admit(At(MouseEventKind.Motion, 1, 1)));
        Assert.True(filter.Admit(At(MouseEventKind.Motion, 2, 1)));
        Assert.False(filter.Admit(At(MouseEventKind.Motion, 2, 1)));
        Assert.False(filter.Admit(At(MouseEventKind.Motion, 2, 1) with { PixelX = 1 }));
        Assert.True(filter.Admit(At(MouseEventKind.Motion, 2, 2)));
        Assert.True(filter.Admit(At(MouseEventKind.Release, 2, 2)));
        Assert.True(filter.Admit(At(MouseEventKind.Press, 2, 2)));
        Assert.True(filter.Admit(At(MouseEventKind.Motion, 1, 1)));

        filter.Reset();
        Assert.True(filter.Admit(At(MouseEventKind.Motion, 1, 1)));
    }

    [Fact]
    public void MotionFilter_SgrPixelsComparesPixelsNotCells()
    {
        var filter = new MouseMotionFilter();
        var motion = new MouseReport(Mode.AnyEvent, Encoding.SGRPixels, MouseEncoder.ButtonNone, MouseEventKind.Motion,
            Column: 3, Row: 3, PixelX: 30, PixelY: 50, TerminalKeyModifiers.None);

        Assert.True(filter.Admit(motion));
        Assert.False(filter.Admit(motion));
        Assert.True(filter.Admit(motion with { PixelX = 31 }));
        Assert.False(filter.Admit(motion with { PixelX = 31, Column = 9 }));
        Assert.True(filter.Admit(motion with { PixelX = 31, Encoding = Encoding.SGR }));
    }

    [Theory]
    [InlineData(true, false, 3, 64, "\x1b[A\x1b[A\x1b[A")]
    [InlineData(false, false, 1, 64, "\x1b[B")]
    [InlineData(true, true, 2, 64, "\x1bOA\x1bOA")]
    [InlineData(false, true, 2, 64, "\x1bOB\x1bOB")]
    [InlineData(true, false, 5, 7, "\x1b[A\x1b[A")]
    [InlineData(true, false, 0, 64, "")]
    [InlineData(true, false, -2, 64, "")]
    public void AlternateScroll_CursorKeysHonourDeckmAndCapacity(bool up, bool applicationCursorKeys, int count,
        int capacity, string expected)
    {
        var buffer = new byte[capacity];
        int length = MouseEncoder.EncodeAlternateScroll(up, applicationCursorKeys, count, buffer);
        Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(buffer, 0, length));
    }
}
