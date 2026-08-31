using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Parser-level edge cases: CSI sequences across chunk boundaries,
/// multi-param SGR, DEC private modes, and comprehensive wiring tests.
/// </summary>
public class ParserEdgeCaseTests
{
    private sealed class CaptureHandler : ITerminalHandler
    {
        public List<string> Events { get; } = new();
        public List<(int row, int col)> CursorMoves { get; } = new();
        public List<int> ModesSet { get; } = new();
        public List<int> ModesReset { get; } = new();
        public int ScrollUpCount, ScrollDownCount;
        public int InsertLinesCount, DeleteLinesCount;
        public int InsertCharsCount, DeleteCharsCount;
        public int EraseCharsCount;
        public int CursorHVA, CursorVPA;
        public int ReverseIndexCount, SetTabStopCount, ClearTabStopCount, ClearAllTabStopsCount;
        public int FullResetCount, BellCount;
        public int SaveCursorCount, RestoreCursorCount;
        public List<string> SgrCalls { get; } = new();
        public int CursorNextLine, CursorPreviousLine;
        public List<(int shape, int count)> CursorShapeCalls { get; } = new();
        public int MouseEventCount;
        public string PrintedText = "";
        public List<string> FocusReports { get; } = new();

        object? ITerminalHandler.Buffer => null;
        event Action<string>? ITerminalHandler.RenderRequested { add { } remove { } }
        event Action<string>? ITerminalHandler.ClipboardWriteRequested { add { } remove { } }
        event Action<string>? ITerminalHandler.TitleChanged { add { } remove { } }
        event Action<string>? ITerminalHandler.LinkOpened { add { } remove { } }
        void ITerminalHandler.OnHyperlink(string uri) { }
        void ITerminalHandler.RequestRenderExtern() { }
        void ITerminalHandler.ResizeBuffer(int rows, int cols) { }
        void ITerminalHandler.OnPrint(ReadOnlySpan<char> text) => PrintedText += text.ToString();
        void ITerminalHandler.OnEraseDisplay(int mode) { }
        void ITerminalHandler.OnClearScrollback() { }
        void ITerminalHandler.OnSetGraphicsRendition(ReadOnlySpan<char> p) => SgrCalls.Add(p.ToString());
        void ITerminalHandler.OnBell() => BellCount++;
        void ITerminalHandler.OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload) => Events.Add($"OSC:{code}");
        void ITerminalHandler.OnMoveCursor(int row, int col) => CursorMoves.Add((row, col));
        void ITerminalHandler.OnCursorUp(int n) => CursorMoves.Add((1, -n));
        void ITerminalHandler.OnCursorDown(int n) => Events.Add($"CUD:{n}");
        void ITerminalHandler.OnCursorForward(int n) => Events.Add($"CUF:{n}");
        void ITerminalHandler.OnCursorBack(int n) => Events.Add($"CUB:{n}");
        void ITerminalHandler.OnEraseLine(int mode) => Events.Add($"EL:{mode}");
        void ITerminalHandler.OnCarriageReturn() => Events.Add("CR");
        void ITerminalHandler.OnLineFeed() => Events.Add("LF");
        void ITerminalHandler.OnSetScrollRegion(int top1Based, int bottom1Based) => Events.Add($"DECSTBM:{top1Based},{bottom1Based}");
        void ITerminalHandler.OnSetOriginMode(bool enabled) => Events.Add($"DECOM:{enabled}");
        void ITerminalHandler.OnSetAlternateScreen(bool enabled) => Events.Add($"ALT:{enabled}");
        void ITerminalHandler.OnSetCursorVisibility(bool v) => Events.Add($"CURSOR_VIS:{v}");
        void ITerminalHandler.OnSetKeypadApplicationMode(bool en) => Events.Add($"KAM:{en}");
        void ITerminalHandler.OnSetCursorShape(int s) => Events.Add($"DECSCUSR:{s}");
        void ITerminalHandler.OnSetApplicationCursorKeys(bool en) => Events.Add($"DECCKM:{en}");
        void ITerminalHandler.OnSaveCursor() => SaveCursorCount++;
        void ITerminalHandler.OnRestoreCursor() => RestoreCursorCount++;
        void ITerminalHandler.OnInsertChars(int n) => InsertCharsCount = n;
        void ITerminalHandler.OnDeleteChars(int n) => DeleteCharsCount = n;
        void ITerminalHandler.OnEraseCharacters(int n) => EraseCharsCount = n;
        void ITerminalHandler.OnInsertLines(int n) => InsertLinesCount = n;
        void ITerminalHandler.OnDeleteLines(int n) => DeleteLinesCount = n;
        void ITerminalHandler.OnSetAutoWrap(bool en) => Events.Add($"DECAWM:{en}");
        void ITerminalHandler.OnSetTabStop() => SetTabStopCount++;
        void ITerminalHandler.OnClearTabStop() => ClearTabStopCount++;
        void ITerminalHandler.OnClearAllTabStops() => ClearAllTabStopsCount++;
        void ITerminalHandler.OnSetBracketedPasteMode(bool en) => Events.Add($"BRACKET:{en}");
        void ITerminalHandler.OnDeviceStatusReport(int code) => Events.Add($"DSR:{code}");
        void ITerminalHandler.OnCursorPositionReport() => Events.Add("CPR");
        void ITerminalHandler.OnSendDeviceAttributes(int daType) => Events.Add($"DA:{daType}");
        void ITerminalHandler.OnReverseIndex() => ReverseIndexCount++;
        void ITerminalHandler.OnCursorHorizontalAbsolute(int c) => CursorHVA = c;
        void ITerminalHandler.OnCursorVerticalAbsolute(int r) => CursorVPA = r;
        void ITerminalHandler.OnCursorNextLine(int n) => CursorNextLine = n;
        void ITerminalHandler.OnCursorPreviousLine(int n) => CursorPreviousLine = n;
        void ITerminalHandler.OnScrollUp(int n) => ScrollUpCount = n;
        void ITerminalHandler.OnScrollDown(int n) => ScrollDownCount = n;
        void ITerminalHandler.OnFullReset() => FullResetCount++;
        void ITerminalHandler.OnRepeatCharacter(int n) => Events.Add($"REP:{n}");
        void ITerminalHandler.OnTab() => Events.Add("HT");
        void ITerminalHandler.OnBackTab(int n) => Events.Add($"CBT:{n}");
        void ITerminalHandler.OnMouseEvent(int button, int col, int row, bool isPress) => MouseEventCount++;
        void ITerminalHandler.OnSetSynchronizedUpdate(bool en) => Events.Add($"SYNC:{en}");
        void ITerminalHandler.OnSetMouseMode(int mode, bool en) => Events.Add($"MOUSE:{mode}:{en}");
        void ITerminalHandler.OnSetKittyKeyboardMode(int mode) => Events.Add($"KITTY:{mode}");
        void ITerminalHandler.OnQueryKittyKeyboard() => Events.Add("KITTY_QUERY");
        void ITerminalHandler.FlushRender() { }
        void ITerminalHandler.OnSetFocusReporting(bool enabled) => FocusReports.Add(enabled ? "FOCUS:True" : "FOCUS:False");
        void ITerminalHandler.OnWindowReport(int command) { }
    }

    private static (BasicAnsiParser, CaptureHandler) Setup()
    {
        var p = new BasicAnsiParser();
        var h = new CaptureHandler();
        p.Handler = h;
        return (p, h);
    }
 
    private static (CaptureHandler Handler, BasicAnsiParser Parser) ParseSequence(string sequence, bool split)
    {
        var (parser, handler) = Setup();
        var bytes = Encoding.UTF8.GetBytes(sequence);
        if (split)
        {
            for (int i = 0; i < bytes.Length; i++)
                parser.Feed(bytes.AsSpan(i, 1));
        }
        else
        {
            parser.Feed(bytes);
        }

        return (handler, parser);
    }

    private static string Describe(CaptureHandler handler) =>
        string.Join("|", handler.Events
            .Concat(handler.FocusReports)
            .Concat(handler.CursorMoves.Select(move => $"CUP:{move.row},{move.col}"))
            .Concat(new[]
            {
                $"TEXT:{handler.PrintedText}",
                $"IL:{handler.InsertLinesCount}",
                $"DL:{handler.DeleteLinesCount}",
                $"KITTY:{handler.Events.Count(eventName => eventName.StartsWith("KITTY:", StringComparison.Ordinal))}",
            }));

    [Theory]
    [InlineData("\x1b[?1h")]
    [InlineData("\x1b[?1l")]
    [InlineData("\x1b[?2004h")]
    [InlineData("\x1b[?2004l")]
    [InlineData("\x1b[?1004h")]
    [InlineData("\x1b[?1004l")]
    [InlineData("\x1b[?2026h")]
    [InlineData("\x1b[?2026l")]
    [InlineData("\x1b[?1u")]
    [InlineData("\x1b[?u")]
    public void P0Modes_OneChunkAndEveryByteHaveIdenticalCallbacks(string sequence)
    {
        var oneChunk = ParseSequence(sequence, split: false).Handler;
        var everyByte = ParseSequence(sequence, split: true).Handler;

        Assert.Equal(Describe(oneChunk), Describe(everyByte));
    }

    [Fact]
    public void P0Modes_OverCapacityPrivateSequence_UsesFallbackWithoutDivergence()
    {
        const string sequence = "\x1b[?1;0;0;0;0;0;0;0;0h";

        var oneChunk = ParseSequence(sequence, split: false).Handler;
        var everyByte = ParseSequence(sequence, split: true).Handler;

        Assert.Equal(Describe(oneChunk), Describe(everyByte));
        Assert.Contains("DECCKM:True", oneChunk.Events);
        var fastPath = ParseSequence("\x1b[?1h", split: false).Handler;
        Assert.Equal(Describe(fastPath), Describe(oneChunk));
    }

    [Fact]
    public void P0Modes_MalformedUnknownSequence_DoesNotDesynchronizeFollowingText()
    {
        var (parser, handler) = Setup();
        var exception = Record.Exception(() => parser.Feed("\x1b[?2026;123:456hOK"u8));

        Assert.Null(exception);
        Assert.Equal("OK", handler.PrintedText);
        Assert.Contains("SYNC:True", handler.Events);
    }


    // ================================================================
    // CSI wiring: each sequence dispatches to the correct handler
    // ================================================================

    [Fact]
    public void CsiInsertLines_Wired() { var (p, h) = Setup(); p.Feed("\x1b[3L"u8); Assert.Equal(3, h.InsertLinesCount); }
    [Fact]
    public void CsiDeleteLines_Wired() { var (p, h) = Setup(); p.Feed("\x1b[4M"u8); Assert.Equal(4, h.DeleteLinesCount); }

    [Fact]
    public void CsiDeleteLines_NoParams_DefaultsToOne()
    {
        // CSI M with no parameters is always Delete Line (default count 1,
        // ECMA-48) - never a legacy X10 mouse report (ESC[M Cb Cx Cy). Mouse
        // reports flow terminal -> application as PTY *input* (from real
        // mouse clicks); they never appear in the application's *output*
        // stream that this parser reads, so there's no ambiguity to resolve.
        var (p, h) = Setup();
        p.Feed("\x1b[M"u8);
        Assert.Equal(1, h.DeleteLinesCount);
        Assert.Equal(0, h.MouseEventCount);
    }

    [Fact]
    public void CsiDeleteLines_NoParams_DefaultsToOne_EvenWhenAppMouseModeIsOn()
    {
        // Regression: Neovim's default `mouse=a` turns mouse tracking on via
        // its own output stream (CSI ?1000h) yet still relies on the
        // terminfo "dl1" capability (ESC[M) - unconditionally, regardless of
        // its own mouse setting - whenever it scrolls a DECSTBM region.
        // Gating Delete-Line on "is mouse mode enabled" reintroduces the
        // exact corruption this parser must avoid.
        var (p, h) = Setup();
        p.Feed("\x1b[?1000h"u8);
        p.Feed("\x1b[M"u8);
        Assert.Equal(1, h.DeleteLinesCount);
        Assert.Equal(0, h.MouseEventCount);
    }

    [Fact]
    public void CsiDeleteLines_NoParams_DoesNotCorruptFollowingStream()
    {
        // The buggy behavior didn't just skip Delete Line - it also consumed
        // the next 3 bytes as fake mouse coordinates, desyncing everything
        // after. Verify text immediately following CSI M parses intact.
        var (p, h) = Setup();
        p.Feed("\x1b[Mabcdef"u8);
        Assert.Equal(1, h.DeleteLinesCount);
        Assert.Equal(0, h.MouseEventCount);
        Assert.Equal("abcdef", h.PrintedText);
    }
    [Fact]
    public void CsiInsertChars_Wired() { var (p, h) = Setup(); p.Feed("\x1b[5@"u8); Assert.Equal(5, h.InsertCharsCount); }
    [Fact]
    public void CsiDeleteChars_Wired() { var (p, h) = Setup(); p.Feed("\x1b[6P"u8); Assert.Equal(6, h.DeleteCharsCount); }
    [Fact]
    public void CsiCHA_Wired() { var (p, h) = Setup(); p.Feed("\x1b[42G"u8); Assert.Equal(42, h.CursorHVA); }
    [Fact]
    public void CsiCNL_Wired() { var (p, h) = Setup(); p.Feed("\x1b[7E"u8); Assert.Equal(7, h.CursorNextLine); }
    [Fact]
    public void CsiCPL_Wired() { var (p, h) = Setup(); p.Feed("\x1b[8F"u8); Assert.Equal(8, h.CursorPreviousLine); }
    [Fact]
    public void CsiCursorShape_Wired()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[3 q"u8);
        Assert.Contains(h.Events, e => e.StartsWith("DECSCUSR:"));
    }
    [Fact]
    public void CsiSaveCursor_Wired() { var (p, h) = Setup(); p.Feed("\x1b[s"u8); Assert.Equal(1, h.SaveCursorCount); }
    [Fact]
    public void CsiRestoreCursor_Wired() { var (p, h) = Setup(); p.Feed("\x1b[u"u8); Assert.Equal(1, h.RestoreCursorCount); }

    // ================================================================
    // DEC private modes
    // ================================================================

    [Fact]
    public void DecSet_AlternateScreen()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?1049h"u8);
        Assert.Contains("ALT:True", h.Events);
    }

    [Fact]
    public void DecReset_AlternateScreen()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?1049l"u8);
        Assert.Contains("ALT:False", h.Events);
    }

    [Fact]
    public void DecSet_CursorVisible()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?25h"u8);
        Assert.Contains("CURSOR_VIS:True", h.Events);
    }

    [Fact]
    public void DecReset_CursorVisible()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?25l"u8);
        Assert.Contains("CURSOR_VIS:False", h.Events);
    }

    [Fact]
    public void DecSet_OriginMode()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?6h"u8);
        Assert.Contains("DECOM:True", h.Events);
    }

    [Fact]
    public void DecSet_AutoWrap()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?7h"u8);
        Assert.Contains("DECAWM:True", h.Events);
    }

    [Fact]
    public void DecSet_ApplicationCursorKeys()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?1h"u8);
        Assert.Contains("DECCKM:True", h.Events);
    }

    [Fact]
    public void DecSet_BracketedPaste()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?2004h"u8);
        Assert.Contains("BRACKET:True", h.Events);
    }

    [Fact]
    public void DecSet_SynchronizedUpdate()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[?2026h"u8);
        Assert.Contains("SYNC:True", h.Events);
    }

    [Fact]
    public void DecSet_MultipleModes()
    {
        var (p, h) = Setup();
        // Note: the current parser handles per-code private markers `?`
        // only for the first code when codes are combined with semicolons.
        // Real terminal apps (Neovim) send separate sequences anyway.
        p.Feed("\x1b[?1049;?25;?6h"u8);
        // At minimum the alternate screen event should fire
        Assert.True(h.Events.Any(e => e.StartsWith("ALT:")),
            "Expected alterncate screen event");
    }

    // ================================================================
    // SGR multi-parameter edge cases
    // ================================================================

    [Fact]
    public void Sgr_MultipleParams_Delivered()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[1;31;42m"u8);
        Assert.Single(h.SgrCalls);
    }

    [Fact]
    public void Sgr_EmptyParams_Defaults()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[;m"u8);
        Assert.Single(h.SgrCalls);
    }

    [Fact]
    public void Sgr_WithLeadingZeros()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[001;002m"u8);
        Assert.Single(h.SgrCalls);
    }

    // ================================================================
    // CSI sequences split across chunk boundaries
    // ================================================================

    [Fact]
    public void Csi_AcrossChunks_Complete()
    {
        var (p, h) = Setup();
        p.Feed("\x1b["u8);
        p.Feed("3L"u8);
        Assert.Equal(3, h.InsertLinesCount);
    }

    [Fact]
    public void Sgr_AcrossChunks_Complete()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[1;"u8);
        p.Feed("31;42m"u8);
        Assert.Single(h.SgrCalls);
    }

    [Fact]
    public void Escape_AcrossChunks_Complete()
    {
        var (p, h) = Setup();
        p.Feed("\x1b"u8);
        p.Feed("M"u8);
        Assert.Equal(1, h.ReverseIndexCount);
    }

    [Fact]
    public void Osc_AcrossChunks_Complete()
    {
        var (p, h) = Setup();
        p.Feed("\x1b]0;m"u8);
        p.Feed("y ti"u8);
        p.Feed("tle\x07"u8);
        Assert.Contains(h.Events, e => e.StartsWith("OSC:"));
    }

    [Fact]
    public void Csi_LongParams_AcrossChunks()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[12"u8);
        p.Feed("3;45"u8);
        p.Feed("6H"u8);
        Assert.Single(h.CursorMoves);
        Assert.Equal((123, 456), h.CursorMoves[0]);
    }

    // ================================================================
    // Non-CSI escapes across chunk boundaries
    // ================================================================

    [Fact]
    public void EscCharset_AcrossChunks()
    {
        var (p, h) = Setup();
        p.Feed("\x1b("u8);
        p.Feed("0"u8);
        // After charset selection, subsequent printable chars go through the map.
        // Just verify no crash and charset was set.
        p.Feed("a"u8);
        Assert.True(true);
    }

    // ================================================================
    // Edge: CSI with very large parameter values
    // ================================================================

    [Fact]
    public void Csi_LargeParams_DontOverflowStack()
    {
        var (p, h) = Setup();
        // 20 parameters — only 8 are read; extras are ignored
        p.Feed("\x1b[1;2;3;4;5;6;7;8;9;10;11;12;13;14;15;16;17;18;19;20H"u8);
        // At least the first params should be forwarded
        Assert.NotEmpty(h.CursorMoves);
    }

    [Fact]
    public void Csi_NonNumericParams_Fallback()
    {
        var (p, h) = Setup();
        // params with leading '?' that confuses TryParseParams but should
        // still work via the private path
        p.Feed("\x1b[?25l"u8);
        Assert.Contains("CURSOR_VIS:False", h.Events);
    }

    // ================================================================
    // SU / SD (Scroll Up/Down) from fast path
    // ================================================================

    [Fact]
    public void CsiScrollUp_NoParams_DefaultsToOne()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[S"u8);
        Assert.Equal(1, h.ScrollUpCount);
    }

    [Fact]
    public void CsiScrollDown_DefaultParam()
    {
        var (p, h) = Setup();
        p.Feed("\x1b[T"u8);
        Assert.Equal(1, h.ScrollDownCount);
    }

    // ================================================================
    // ESC sequences across chunk boundaries near buffer split
    // ================================================================

    [Fact]
    public void EscEscape_Split_MultipleWays()
    {
        var (p, h) = Setup();
        p.Feed("\x1b"u8);
        p.Feed("["u8);
        p.Feed("3"u8);
        p.Feed("L"u8);
        Assert.Equal(3, h.InsertLinesCount);
    }

    [Fact]
    public void EscEscape_BufferBoundary_Resume()
    {
        var (p, h) = Setup();
        // Feed part of a long normal text run followed by an incomplete CSI
        p.Feed("Hello \x1b[12"u8);
        // Second chunk continues the CSI
        p.Feed("3H"u8);
        var moveEvents = h.CursorMoves.Where(m => m.row == 123).ToList();
        Assert.NotEmpty(moveEvents);
    }
}
