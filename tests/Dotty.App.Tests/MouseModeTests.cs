using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Comprehensive tests for mouse mode support including parsing, state tracking, and event generation.
/// Tests all six mouse modes: X11 (1000, 1002, 1003), UTF-8 (1005), SGR (1006), and URXVT (1015).
/// </summary>
public class MouseModeTests
{
    [Fact]
    public void MouseMode_1000_Enable()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1000h"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1000, true), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1000_Disable()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1000l"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1000, false), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1002_Enable()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1002h"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1002, true), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1003_Enable()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1003h"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1003, true), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1005_Enable_UTF8()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1005h"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1005, true), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1005_Disable_UTF8()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1005l"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1005, false), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1006_Enable_SGR()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1006h"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1006, true), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1006_Disable_SGR()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1006l"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1006, false), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1015_Enable_URXVT()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1015h"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1015, true), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseMode_1015_Disable_URXVT()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1015l"));

        Assert.Single(handler.SetMouseModeCalls);
        Assert.Equal((1015, false), handler.SetMouseModeCalls[0]);
    }

    [Fact]
    public void MouseEvent_X11_LeftClick_Press()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // X11 format: ESC [ M [button+32] [col+32] [row+32]
        // Button 0 (left press) at column 10, row 5
        byte[] mouseSeq = new byte[] { 0x1b, (byte)'[', (byte)'M', 32, 42, 37 };
        parser.Feed(mouseSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(0, evt.button);      // left button
        Assert.Equal(10, evt.col);         // col = 42 - 32 = 10
        Assert.Equal(5, evt.row);          // row = 37 - 32 = 5
        Assert.True(evt.isPress);
    }

    [Fact]
    public void MouseEvent_X11_LeftClick_Release()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // X11 format: release is button with both bits 0-1 set (i.e., 0x03)
        // Button 3 (release) at column 10, row 5
        byte[] mouseSeq = new byte[] { 0x1b, (byte)'[', (byte)'M', 35, 42, 37 }; // 35 = 32 + 3
        parser.Feed(mouseSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(3, evt.button);      // release button
        Assert.Equal(10, evt.col);
        Assert.Equal(5, evt.row);
        Assert.False(evt.isPress);
    }

    [Fact]
    public void MouseEvent_X11_RightClick_Press()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // Right button press = button 2
        byte[] mouseSeq = new byte[] { 0x1b, (byte)'[', (byte)'M', 34, 50, 45 }; // 34 = 32 + 2
        parser.Feed(mouseSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(2, evt.button);      // right button
        Assert.Equal(18, evt.col);        // col = 50 - 32 = 18
        Assert.Equal(13, evt.row);        // row = 45 - 32 = 13
        Assert.True(evt.isPress);
    }

    [Fact]
    public void MouseEvent_X11_MiddleClick_Press()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // Middle button press = button 1
        byte[] mouseSeq = new byte[] { 0x1b, (byte)'[', (byte)'M', 33, 32, 32 }; // 33 = 32 + 1
        parser.Feed(mouseSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(1, evt.button);      // middle button
        Assert.Equal(0, evt.col);         // col = 32 - 32 = 0
        Assert.Equal(0, evt.row);         // row = 32 - 32 = 0
        Assert.True(evt.isPress);
    }

    [Fact]
    public void MouseEvent_SGR_LeftClick_Press()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // SGR format: CSI < button ; col ; row M
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<0;10;5M");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(0, evt.button);      // left button
        Assert.Equal(10, evt.col);
        Assert.Equal(5, evt.row);
        Assert.True(evt.isPress);
    }

    [Fact]
    public void MouseEvent_SGR_LeftClick_Release()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // SGR format: CSI < button ; col ; row m (lowercase m for release)
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<0;10;5m");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(0, evt.button);
        Assert.Equal(10, evt.col);
        Assert.Equal(5, evt.row);
        Assert.False(evt.isPress);
    }

    [Fact]
    public void MouseEvent_SGR_RightClick_Press()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // SGR format: right button = 2
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<2;20;15M");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(2, evt.button);      // right button
        Assert.Equal(20, evt.col);
        Assert.Equal(15, evt.row);
        Assert.True(evt.isPress);
    }

    [Fact]
    public void MouseEvent_SGR_MiddleClick_Press()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // SGR format: middle button = 1
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<1;15;10M");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(1, evt.button);      // middle button
        Assert.Equal(15, evt.col);
        Assert.Equal(10, evt.row);
        Assert.True(evt.isPress);
    }

    [Fact]
    public void MouseEvent_URXVT_LeftClick_Press()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // URXVT format: CSI button ; col ; row M
        var urxvtSeq = Encoding.UTF8.GetBytes("\u001b[0;10;5M");
        parser.Feed(urxvtSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(0, evt.button);
        Assert.Equal(10, evt.col);
        Assert.Equal(5, evt.row);
        Assert.True(evt.isPress);
    }

    [Fact]
    public void MouseEvent_URXVT_LeftClick_Release()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // URXVT format: release has bit 1 set (button | 0x02)
        var urxvtSeq = Encoding.UTF8.GetBytes("\u001b[3;10;5M"); // 2 = 0 | 0x02
        parser.Feed(urxvtSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(3, evt.button);      // release button
        Assert.Equal(10, evt.col);
        Assert.Equal(5, evt.row);
        Assert.False(evt.isPress);
    }

    [Fact]
    public void MouseEvent_CoordinateRange_LargeTerminal()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // SGR format allows unlimited coordinate range
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<0;300;200M");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(300, evt.col);
        Assert.Equal(200, evt.row);
    }

    [Fact]
    public void MouseEvent_CoordinateEdgeCases()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // Test corner coordinates
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<0;1;1M");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(1, evt.col);
        Assert.Equal(1, evt.row);
    }

    [Fact]
    public void MouseMode_MultipleEnableDisable()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // Enable mode 1000
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1000h"));
        Assert.Equal((1000, true), handler.SetMouseModeCalls[0]);

        // Disable mode 1000
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1000l"));
        Assert.Equal((1000, false), handler.SetMouseModeCalls[1]);

        // Enable mode 1006 (SGR)
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1006h"));
        Assert.Equal((1006, true), handler.SetMouseModeCalls[2]);

        // Disable mode 1006
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?1006l"));
        Assert.Equal((1006, false), handler.SetMouseModeCalls[3]);

        Assert.Equal(4, handler.SetMouseModeCalls.Count);
    }

    [Fact]
    public void MouseEvent_MixedFormats()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // Send X11 event
        parser.Feed(new byte[] { 0x1b, (byte)'[', (byte)'M', 32, 42, 37 });

        // Send SGR event
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[<2;20;15M"));

        // Send URXVT event
        parser.Feed(Encoding.UTF8.GetBytes("\u001b[1;15;10M"));

        Assert.Equal(3, handler.MouseEventCalls.Count);
        
        // X11
        Assert.Equal(0, handler.MouseEventCalls[0].button);
        Assert.Equal(10, handler.MouseEventCalls[0].col);
        Assert.Equal(5, handler.MouseEventCalls[0].row);

        // SGR
        Assert.Equal(2, handler.MouseEventCalls[1].button);
        Assert.Equal(20, handler.MouseEventCalls[1].col);
        Assert.Equal(15, handler.MouseEventCalls[1].row);

        // URXVT
        Assert.Equal(1, handler.MouseEventCalls[2].button);
        Assert.Equal(15, handler.MouseEventCalls[2].col);
        Assert.Equal(10, handler.MouseEventCalls[2].row);
    }

    [Fact]
    public void MouseEvent_SGR_WithModifiers()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // SGR format: button value includes modifiers
        // Button 4 = left click + shift (bit 2 set for shift)
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<4;10;5M");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(4, evt.button);      // button with shift modifier
        Assert.Equal(10, evt.col);
        Assert.Equal(5, evt.row);
    }

    [Fact]
    public void MouseEvent_Scrollwheel_SGR()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        // SGR format: buttons 64/65 are scroll up/down
        // Button 64 = scroll up
        var sgrSeq = Encoding.UTF8.GetBytes("\u001b[<64;10;5M");
        parser.Feed(sgrSeq);

        Assert.Single(handler.MouseEventCalls);
        var evt = handler.MouseEventCalls[0];
        Assert.Equal(64, evt.button);     // scroll up button code
        Assert.Equal(10, evt.col);
        Assert.Equal(5, evt.row);
    }

    private sealed class RecordingHandler : ITerminalHandler
    {
        public List<string> PrintCalls { get; } = new();
        public List<int> DeviceStatusReportCalls { get; } = new();
        public int CursorPositionReportCalls { get; set; }
        public List<int> SendDeviceAttributesCalls { get; } = new();
        public List<(int mode, bool enabled)> SetMouseModeCalls { get; } = new();
        public List<(int button, int col, int row, bool isPress)> MouseEventCalls { get; } = new();

        object? ITerminalHandler.Buffer => null;
        event Action<string>? ITerminalHandler.RenderRequested {
            add {}
            remove {}
        }
        event Action<string>? ITerminalHandler.ClipboardWriteRequested {
            add {}
            remove {}
        }
        event Action<string>? ITerminalHandler.TitleChanged {
            add {}
            remove {}
        }
        event Action<string>? ITerminalHandler.LinkOpened {
            add {}
            remove {}
        }
        void ITerminalHandler.OnHyperlink(string uri) {} 
        // add { } remove { } }

        void ITerminalHandler.RequestRenderExtern() { }
        void ITerminalHandler.ResizeBuffer(int rows, int cols) { }
        void ITerminalHandler.OnPrint(ReadOnlySpan<char> text) => PrintCalls.Add(text.ToString());
        void ITerminalHandler.OnEraseDisplay(int mode) { }
        void ITerminalHandler.OnClearScrollback() { }
        void ITerminalHandler.OnSetGraphicsRendition(ReadOnlySpan<char> parameters) { }
        void ITerminalHandler.OnBell() { }
        void ITerminalHandler.OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload) { }
        void ITerminalHandler.OnMoveCursor(int row, int col) { }
        void ITerminalHandler.OnCursorUp(int n) { }
        void ITerminalHandler.OnCursorDown(int n) { }
        void ITerminalHandler.OnCursorForward(int n) { }
        void ITerminalHandler.OnCursorBack(int n) { }
        void ITerminalHandler.OnEraseLine(int mode) { }
        void ITerminalHandler.OnCarriageReturn() { }
        void ITerminalHandler.OnLineFeed() { }
        void ITerminalHandler.OnSetScrollRegion(int top1Based, int bottom1Based) { }
        void ITerminalHandler.OnSetOriginMode(bool enabled) { }
        void ITerminalHandler.OnSetAlternateScreen(bool enabled) { }
        void ITerminalHandler.OnSetCursorVisibility(bool visible) { }
        void ITerminalHandler.OnSetKeypadApplicationMode(bool enabled) { }
        void ITerminalHandler.OnSetCursorShape(int shape) { }
        void ITerminalHandler.OnSaveCursor() { }
        void ITerminalHandler.OnRestoreCursor() { }
        void ITerminalHandler.OnInsertChars(int n) { }
        void ITerminalHandler.OnDeleteChars(int n) { }
        void ITerminalHandler.OnInsertLines(int n) { }
        void ITerminalHandler.OnDeleteLines(int n) { }
        void ITerminalHandler.OnSetAutoWrap(bool enabled) { }
        void ITerminalHandler.OnSetTabStop() { }
        void ITerminalHandler.OnClearTabStop() { }
        void ITerminalHandler.OnClearAllTabStops() { }
        void ITerminalHandler.OnSetBracketedPasteMode(bool enabled) { }
        void ITerminalHandler.OnDeviceStatusReport(int code) => DeviceStatusReportCalls.Add(code);
        void ITerminalHandler.OnCursorPositionReport() => CursorPositionReportCalls++;
        void ITerminalHandler.OnSendDeviceAttributes(int daType) => SendDeviceAttributesCalls.Add(daType);
        void ITerminalHandler.OnReverseIndex() { }
        void ITerminalHandler.OnCursorHorizontalAbsolute(int col) { }
        void ITerminalHandler.OnCursorVerticalAbsolute(int row) { }
        void ITerminalHandler.OnCursorNextLine(int n) { }
        void ITerminalHandler.OnCursorPreviousLine(int n) { }
        void ITerminalHandler.OnScrollUp(int n) { }
        void ITerminalHandler.OnScrollDown(int n) { }
        void ITerminalHandler.OnFullReset() { }
        void ITerminalHandler.OnRepeatCharacter(int n) { }
        void ITerminalHandler.OnTab() { }
        void ITerminalHandler.OnBackTab(int n) { }
        void ITerminalHandler.OnMouseEvent(int button, int col, int row, bool isPress) => MouseEventCalls.Add((button, col, row, isPress));
        void ITerminalHandler.OnSetSynchronizedUpdate(bool enabled) { }
        void ITerminalHandler.OnSetMouseMode(int mode, bool enabled) => SetMouseModeCalls.Add((mode, enabled));
        void ITerminalHandler.OnSetApplicationCursorKeys(bool enabled) { }
        void ITerminalHandler.FlushRender() { }
    }
}
