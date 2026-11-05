using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public class BasicAnsiParserTests
{
    [Fact]
    public void DecGraphicsTranslateToUnicode()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b(0lqk\u001b(BABC"));

        Assert.Equal("┌─┐", handler.PrintCalls[0]);
        Assert.Equal("ABC", handler.PrintCalls[1]);
    }

    [Fact]
    public void CharsetSettingPersistsBetweenChunks()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b(0q"));
        parser.Feed(Encoding.UTF8.GetBytes("x"));
        parser.Feed(Encoding.UTF8.GetBytes("x\u001b(B"));

        Assert.Equal("─", handler.PrintCalls[0]);
        Assert.Equal("│", handler.PrintCalls[1]);
        Assert.Equal("│", handler.PrintCalls[2]);
        Assert.Equal(3, handler.PrintCalls.Count);
    }

    [Fact]
    public void NationalReplacementModeMapsOnlyDesignatedAsciiGraphicCharacters()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("#\u001b[?42h\u001b(A#\u001b[?42l#"));

        Assert.Equal(new[] { "#", "£", "#" }, handler.PrintCalls);
        Assert.False(handler.NationalReplacementEnabled);
    }

    [Theory]
    [InlineData("\u001b(A", "£@[\\]^_`{|}~")]
    [InlineData("\u001b(4", "£¾ĳ½|^_`¨ƒ¼´")]
    [InlineData("\u001b(C", "#@ÄÖÅÜ_éäöåü")]
    [InlineData("\u001b(R", "£à°ç§^_`éùè¨")]
    [InlineData("\u001b(Q", "#àâçêî_ôéùèû")]
    [InlineData("\u001b(K", "#§ÄÖÜ^_`äöüß")]
    [InlineData("\u001b(Y", "£§°çé^_ùàòèì")]
    [InlineData("\u001b(E", "#ÄÆØÅÜ_äæøåü")]
    [InlineData("\u001b(%6", "#@ÃÇÕ^_`ãçõ~")]
    [InlineData("\u001b(Z", "£§¡Ñ¿^_``°ñç")]
    [InlineData("\u001b(H", "#ÉÄÖÅÜ_éäöåü")]
    [InlineData("\u001b(=", "ùàéçêîèôäöüû")]
    public void NationalReplacementDesignationsMapAllGraphicPositions(string designation, string expected)
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes(
            "\u001b[?42h" + designation + "#@[\\]^_`{|}~"));

        Assert.Equal(expected, Assert.Single(handler.PrintCalls));
    }

    [Fact]
    public void NationalReplacementUsesLockingShiftAndResetBoundaries()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes(
            "\u001b[?42h\u001b)K\u000e[\u000f[\u001b[!p\u001b(A#\u001b[!p#"));

        Assert.Equal(new[] { "Ä", "[", "£", "#" }, handler.PrintCalls);
        Assert.True(handler.NationalReplacementEnabled);
    }

    [Fact]
    public void RisResetsNationalReplacementModeAndDesignations()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?42h\u001b(A#\u001bc#"));

        Assert.Equal(new[] { "£", "#" }, handler.PrintCalls);
        Assert.False(handler.NationalReplacementEnabled);
    }

    [Fact]
    public void NationalReplacementLeavesUtf8AndUnmappedCharactersUnchanged()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?42h\u001b(Aé€"));

        Assert.Equal("é€", Assert.Single(handler.PrintCalls));
    }

    [Theory]
    [InlineData("\u001b[J", 0)]
    [InlineData("\u001b[0J", 0)]
    [InlineData("\u001b[1J", 1)]
    [InlineData("\u001b[2J", 2)]
    public void EraseDisplayModes_ZeroOneTwo_AreForwarded(string sequence, int expectedMode)
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes(sequence));

        Assert.Single(handler.EraseDisplayCalls);
        Assert.Equal(expectedMode, handler.EraseDisplayCalls[0]);
    }

    [Fact]
    public void EraseDisplayModeThree_ClearsScrollbackOnly()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[3J"));

        Assert.Empty(handler.EraseDisplayCalls);
        Assert.Equal(1, handler.ClearScrollbackCalls);
    }


    [Fact]
    public void CsiDeviceStatusReport6_UsesOnDeviceStatusReport()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[6n"));

        Assert.Single(handler.DeviceStatusReportCodes);
        Assert.Equal(6, handler.DeviceStatusReportCodes[0]);
        Assert.Equal(0, handler.CursorPositionReportCalls);
    }

    [Fact]
    public void CsiPrivateDeviceStatusReport6_UsesOnCursorPositionReport()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?6n"));

        Assert.Empty(handler.DeviceStatusReportCodes);
        Assert.Equal(1, handler.CursorPositionReportCalls);
    }

    private sealed class RecordingHandler : ITerminalHandler
    {
        public List<string> PrintCalls { get; } = new();
        public List<bool> SetCursorVisibilityCalls { get; } = new();
        public List<(int mode, bool enabled)> SetMouseModeCalls { get; } = new();
        public List<int> EraseDisplayCalls { get; } = new();
        public List<int> ScrollUpCalls { get; } = new();
        public List<int> ScrollDownCalls { get; } = new();
        public List<int> EraseCharacterCalls { get; } = new();
        public List<int> CursorVerticalAbsoluteCalls { get; } = new();
        public List<int> BackTabCalls { get; } = new();
        public List<int> RepeatCharacterCalls { get; } = new();
        public List<bool> AutoWrapCalls { get; } = new();
        public int ClearScrollbackCalls { get; set; }
        public int SetTabStopCalls { get; set; }
        public int ClearTabStopCalls { get; set; }
        public int ClearAllTabStopsCalls { get; set; }
        public int ReverseIndexCalls { get; set; }
        public List<int> DeviceStatusReportCodes { get; } = new();
        public int CursorPositionReportCalls { get; set; }

        object? ITerminalHandler.Buffer => null;
        public bool NationalReplacementEnabled { get; set; }
        bool ITerminalHandler.NationalReplacementEnabled => NationalReplacementEnabled;
        void ITerminalHandler.OnSetNationalReplacementMode(bool enabled) => NationalReplacementEnabled = enabled;
        void ITerminalHandler.OnSetFontShiftMode(bool enabled) { }
        void ITerminalHandler.OnSetSmoothScroll(bool enabled) { }
        void ITerminalHandler.OnSetPrinterMode(int mode, bool enabled) { }
        void ITerminalHandler.OnPrintScreen() { }
        void ITerminalHandler.OnSetScreenLines(int lines) { }
        void ITerminalHandler.OnSetAttributeChangeExtent(int mode) { }
        void ITerminalHandler.OnChangeRectangleAttributes(int top, int left, int bottom, int right, ReadOnlySpan<int> parameters, bool reverse) { }
        void ITerminalHandler.OnRequestStatusString(ReadOnlySpan<byte> request) { }
        void ITerminalHandler.OnPrivateDeviceStatusReport(int code, int identifier) { }
        event Action<string>? ITerminalHandler.RenderRequested
        {
            add { }
            remove { }
        }
        event Action<Dotty.Abstractions.Clipboard.ClipboardWriteRequest>? ITerminalHandler.ClipboardWriteRequested
        {
            add { }
            remove { }
        }
        event Action<string>? ITerminalHandler.TitleChanged
        {
            add { }
            remove { }
        }
        event Action? ITerminalHandler.PaletteChanged { add { } remove { } }
        void ITerminalHandler.OnHyperlink(string uri) { }
        // add { } remove { } }

        void ITerminalHandler.RequestRenderExtern() { }
        void ITerminalHandler.ResizeBuffer(int rows, int cols) { }
        void ITerminalHandler.OnPrint(ReadOnlySpan<char> text) => PrintCalls.Add(text.ToString());
        void ITerminalHandler.OnEraseDisplay(int mode) => EraseDisplayCalls.Add(mode);
        void ITerminalHandler.OnSelectiveEraseDisplay(int mode) { }
        void ITerminalHandler.OnSelectiveEraseLine(int mode) { }
        void ITerminalHandler.OnClearScrollback() => ClearScrollbackCalls++;
        void ITerminalHandler.OnSetConformanceLevel(int level, int controls) { }
        void ITerminalHandler.OnSavePrivateMode(int mode) { }
        void ITerminalHandler.OnRestorePrivateMode(int mode) { }
        void ITerminalHandler.OnSetTitle(int code, ReadOnlySpan<byte> payload) { }
        void ITerminalHandler.OnSetTitleModes(ReadOnlySpan<int> modes, bool enabled) { }
        void ITerminalHandler.OnInsertColumns(int n) { }
        void ITerminalHandler.OnDeleteColumns(int n) { }
        void ITerminalHandler.OnBackIndex() { }
        void ITerminalHandler.OnForwardIndex() { }
        void ITerminalHandler.OnCopyRectangle(int top, int left, int bottom, int right, int sourcePage, int destinationTop, int destinationLeft, int destinationPage) { }
        void ITerminalHandler.OnFillRectangle(uint rune, int top, int left, int bottom, int right) { }
        void ITerminalHandler.OnEraseRectangle(int top, int left, int bottom, int right, bool selective) { }
        void ITerminalHandler.OnSetAnsiMode(int mode, bool enabled) { }
        void ITerminalHandler.OnSetColumnMode(int mode, bool enabled) { }
        void ITerminalHandler.OnSetMoreFix(bool enabled) { }
        void ITerminalHandler.OnSwitchAlternateScreen(int mode, bool enabled) { }
        void ITerminalHandler.OnSetGraphicsRendition(ReadOnlySpan<char> parameters) { }
        void ITerminalHandler.OnBell() { }
        void ITerminalHandler.OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload) { }
        void ITerminalHandler.OnMoveCursor(int row, int col) { }
        void ITerminalHandler.OnCursorUp(int n) { }
        void ITerminalHandler.OnCursorDown(int n) { }
        void ITerminalHandler.OnCursorForward(int n) { }
        void ITerminalHandler.OnCursorBack(int n) { }
        void ITerminalHandler.OnEraseLine(int mode) { }
        void ITerminalHandler.OnSetIsoProtection(bool active) { }
        void ITerminalHandler.OnSetDecProtection(int mode) { }
        void ITerminalHandler.OnCarriageReturn() { }
        void ITerminalHandler.OnLineFeed() { }
        void ITerminalHandler.OnSetScrollRegion(int top1Based, int bottom1Based) { }
        void ITerminalHandler.OnSetOriginMode(bool enabled) { }
        void ITerminalHandler.OnSetAlternateScreen(bool enabled) { }
        void ITerminalHandler.OnSetCursorVisibility(bool visible) => SetCursorVisibilityCalls.Add(visible);
        void ITerminalHandler.OnSaveCursor() { }
        void ITerminalHandler.OnRestoreCursor() { }
        void ITerminalHandler.OnInsertChars(int n) { }
        void ITerminalHandler.OnDeleteChars(int n) { }
        void ITerminalHandler.OnEraseCharacters(int n) => EraseCharacterCalls.Add(n);
        void ITerminalHandler.OnInsertLines(int n) { }
        void ITerminalHandler.OnDeleteLines(int n) { }
        void ITerminalHandler.OnSetAutoWrap(bool enabled) => AutoWrapCalls.Add(enabled);
        void ITerminalHandler.OnSetTabStop() => SetTabStopCalls++;
        void ITerminalHandler.OnClearTabStop() => ClearTabStopCalls++;
        void ITerminalHandler.OnClearAllTabStops() => ClearAllTabStopsCalls++;
        void ITerminalHandler.OnReverseIndex() => ReverseIndexCalls++;
        void ITerminalHandler.OnSetBracketedPasteMode(bool enabled) { }
        void ITerminalHandler.OnDeviceStatusReport(int code) => DeviceStatusReportCodes.Add(code);
        void ITerminalHandler.OnCursorPositionReport() => CursorPositionReportCalls++;
        void ITerminalHandler.OnCursorHorizontalAbsolute(int col) { }
        void ITerminalHandler.OnCursorVerticalAbsolute(int row) => CursorVerticalAbsoluteCalls.Add(row);
        void ITerminalHandler.OnCursorNextLine(int n) { }
        void ITerminalHandler.OnCursorPreviousLine(int n) { }
        void ITerminalHandler.OnHorizontalPositionRelative(int n) { }
        void ITerminalHandler.OnVerticalPositionRelative(int n) { }
        void ITerminalHandler.OnSetLeftRightMarginMode(bool enabled) { }
        void ITerminalHandler.OnSaveCursorOrSetMargins(int left, int right) { }
        void ITerminalHandler.OnSetReverseWrapMode(int mode, bool enabled) { }
        void ITerminalHandler.OnScrollUp(int n) => ScrollUpCalls.Add(n);
        void ITerminalHandler.OnScrollDown(int n) => ScrollDownCalls.Add(n);
        void ITerminalHandler.OnFullReset() => NationalReplacementEnabled = false;
        void ITerminalHandler.OnScreenAlignmentTest() { }
        void ITerminalHandler.OnRepeatCharacter(int n) => RepeatCharacterCalls.Add(n);
        void ITerminalHandler.OnTab(int n) { }
        void ITerminalHandler.OnBackTab(int n) => BackTabCalls.Add(n);
        void ITerminalHandler.OnSetKeypadApplicationMode(bool enabled) { }
        void ITerminalHandler.OnSetCursorShape(int shape) { }
        void ITerminalHandler.OnSetApplicationCursorKeys(bool enabled) { }
        void ITerminalHandler.OnSendDeviceAttributes(int daType) { }
        void ITerminalHandler.OnMouseEvent(int button, int col, int row, bool isPress) { }
        void ITerminalHandler.OnSetSynchronizedUpdate(bool enabled) { }
        void ITerminalHandler.OnSetMouseMode(int mode, bool enabled) => SetMouseModeCalls.Add((mode, enabled));
        void ITerminalHandler.OnKittyKeyboardCommand(char introducer, int flags, int argument) { }
        void ITerminalHandler.FlushRender() { }
        void ITerminalHandler.OnSetFocusReporting(bool enabled) { }
    }

    [Fact]
    public void CsiScrollUp_UsesOnScrollUp()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[3S"));

        Assert.Single(handler.ScrollUpCalls);
        Assert.Equal(3, handler.ScrollUpCalls[0]);
    }

    [Fact]
    public void CsiScrollDown_DefaultsToOne()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[T"));

        Assert.Single(handler.ScrollDownCalls);
        Assert.Equal(1, handler.ScrollDownCalls[0]);
    }

    [Fact]
    public void CsiEraseCharacters_UsesOnEraseCharacters()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[5X"));

        Assert.Single(handler.EraseCharacterCalls);
        Assert.Equal(5, handler.EraseCharacterCalls[0]);
    }

    [Fact]
    public void CsiVerticalCursorAbsolute_UsesOnCursorVerticalAbsolute()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[12d"));

        Assert.Single(handler.CursorVerticalAbsoluteCalls);
        Assert.Equal(12, handler.CursorVerticalAbsoluteCalls[0]);
    }

    [Fact]
    public void EscReverseIndex_UsesOnReverseIndex()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001bM"));

        Assert.Equal(1, handler.ReverseIndexCalls);
    }

    [Fact]
    public void EscHorizontalTabSet_UsesOnSetTabStop()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001bH"));

        Assert.Equal(1, handler.SetTabStopCalls);
    }

    [Fact]
    public void CsiBackTab_UsesOnBackTab()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[2Z"));

        Assert.Single(handler.BackTabCalls);
        Assert.Equal(2, handler.BackTabCalls[0]);
    }

    [Fact]
    public void CsiRepeatCharacter_UsesOnRepeatCharacter()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[4b"));

        Assert.Single(handler.RepeatCharacterCalls);
        Assert.Equal(4, handler.RepeatCharacterCalls[0]);
    }

    [Fact]
    public void CsiTabClear_DefaultsToCurrentTabStop()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[g"));

        Assert.Equal(1, handler.ClearTabStopCalls);
        Assert.Equal(0, handler.ClearAllTabStopsCalls);
    }

    [Fact]
    public void CsiTabClear_ModeThree_ClearsAllTabs()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[3g"));

        Assert.Equal(0, handler.ClearTabStopCalls);
        Assert.Equal(1, handler.ClearAllTabStopsCalls);
    }

    [Fact]
    public void PrivateAutoWrapMode_UsesOnSetAutoWrap()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("\u001b[?7l\u001b[?7h"));

        Assert.Equal(new[] { false, true }, handler.AutoWrapCalls);
    }


    [Fact]
    public void UnsupportedDcsPayloadsAreDiscardedAcrossChunksAndCancelCleanly()
    {
        var parser = new BasicAnsiParser();
        var handler = new RecordingHandler();
        parser.Handler = handler;

        parser.Feed(Encoding.UTF8.GetBytes("before\u001bP+q4D"));
        parser.Feed(Encoding.UTF8.GetBytes("73\u001b[31mignored\u001b"));
        parser.Feed(Encoding.UTF8.GetBytes("\\after\u001bPcancelled-by-can\u0018mid\u001bPcancelled-by-sub\u001aend"));

        // ESC [ inside the DCS abandons it (only ESC \ terminates a string),
        // so "ignored" is ordinary text; the CAN/SUB-cancelled strings drop
        // their payload up to the cancel.
        Assert.Equal("beforeignoredaftermidend", string.Concat(handler.PrintCalls));
    }
}
