using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Advanced tests: parser fuzzing, scrollback integrity, unicode edge cases.
/// </summary>
public class AdvancedEdgeCaseTests
{
    private static void AssertBufferClean(TerminalBuffer tb)
    {
        var violations = tb.ValidateInvariants();
        Assert.True(violations.Count == 0,
            "Buffer invariants violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ================================================================
    // Parser fuzzing: malformed but valid-byte sequences
    // ================================================================

    [Fact]
    public void Parser_MalformedCsi_DoesNotThrow()
    {
        var p = new BasicAnsiParser();
        var a = new TerminalAdapter(24, 80);
        p.Handler = a;

        // Random-ish malformed CSI sequences
        byte[][] sequences = [
            "\x1b["u8.ToArray(),
            "\x1b[;"u8.ToArray(),
            "\x1b[;;"u8.ToArray(),
            "\x1b[;;;"u8.ToArray(),
            "\x1b[?;"u8.ToArray(),
            "\x1b[>;"u8.ToArray(),
            "\x1b[<"u8.ToArray(),
            "\x1b[99999999H"u8.ToArray(),
            "\x1b[0x1;0x2H"u8.ToArray(),
            "\x1bZ"u8.ToArray(),
            "\x1b["u8.ToArray(),
            "\x1b]0\x07"u8.ToArray(),
            "\x1b]\x07"u8.ToArray(),
            "\x1bP"u8.ToArray(),
            "\x1b\\"u8.ToArray(),
            "\x1b#"u8.ToArray(),
        ];
        foreach (var seq in sequences)
        {
            var ex = Record.Exception(() => p.Feed(seq));
            Assert.Null(ex);
        }

        // After fuzzing, adapter buffer should still be consistent
        Assert.NotNull(a.Buffer);
    }

    [Fact]
    public void Parser_LongOscPayload_DoesNotThrow()
    {
        var p = new BasicAnsiParser();
        var a = new TerminalAdapter(24, 80);
        p.Handler = a;

        // Very long OSC title (10KB)
        var osc = new byte[10240];
        osc[0] = 0x1b;
        osc[1] = (byte)']';
        osc[2] = (byte)'0';
        osc[3] = (byte)';';
        for (int i = 4; i < osc.Length - 1; i++)
            osc[i] = (byte)'X';
        osc[osc.Length - 1] = 0x07;

        var ex = Record.Exception(() => p.Feed(osc));
        Assert.Null(ex);
    }

    [Fact]
    public void Parser_CsiVeryLargeParam_DoesNotThrow()
    {
        var p = new BasicAnsiParser();
        var a = new TerminalAdapter(24, 80);
        p.Handler = a;

        // CSI with 8-digit parameter: \x1b[12345678H
        var seq = "\x1b[12345678H"u8.ToArray();
        var ex = Record.Exception(() => p.Feed(seq));
        Assert.Null(ex);
    }

    [Fact]
    public void Parser_InterleavedTextAndEscapes_Clean()
    {
        var p = new BasicAnsiParser();
        var a = new TerminalAdapter(5, 20);
        p.Handler = a;

        // Deterministic pattern: text, CR, LF, and CSI interleaved safely
        var segments = new List<byte[]>();
        for (int i = 0; i < 200; i++)
        {
            switch (i % 5)
            {
                case 0: segments.Add("hello "u8.ToArray()); break;
                case 1: segments.Add("\x0d\x0a"u8.ToArray()); break;
                case 2: segments.Add("\x1b[3H"u8.ToArray()); break;
                case 3: segments.Add("\x1b[2K"u8.ToArray()); break;
                case 4: segments.Add("world "u8.ToArray()); break;
            }
        }
        var combined = segments.SelectMany(s => s).ToArray();

        var ex = Record.Exception(() => p.Feed(combined));
        Assert.Null(ex);
        AssertBufferClean(a.Buffer);
    }

    // ================================================================
    // Scrollback integrity with wide glyphs
    // ================================================================

    [Fact]
    public void Scrollback_WideGlyph_ContentPreserved()
    {
        var tb = new TerminalBuffer(5, 20);
        // Fill the screen
        for (int r = 0; r < 5; r++)
        {
            tb.SetCursor(r, 2);
            tb.WriteText($"\u754c{r}".AsSpan(), CellAttributes.Default);
        }
        // Push 3 lines into scrollback
        for (int i = 0; i < 3; i++)
        {
            tb.LineFeed();
            tb.SetCursor(4, 2);
            tb.WriteText($"new{i}".AsSpan(), CellAttributes.Default);
        }

        AssertBufferClean(tb);
        Assert.True(tb.ScrollbackCount > 0, "Expected scrollback content");
        var sbLine = tb.GetScrollbackLine(0);
        Assert.False(string.IsNullOrEmpty(sbLine.Text), "Expected non-empty scrollback line");
    }

    [Fact]
    public void Scrollback_ThenWrite_NoContinuationLeak()
    {
        var tb = new TerminalBuffer(3, 15);
        // Fill all rows with wide glyphs
        tb.SetCursor(0, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(2, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // LF at bottom scrolls the region
        tb.LineFeed();
        AssertBufferClean(tb);
        // Now write on the scrolled-in fresh row
        tb.SetCursor(2, 0);
        tb.WriteText("hello".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
    }

    [Fact]
    public void Scrollback_FullScreenScroll_WidePreserved()
    {
        var tb = new TerminalBuffer(4, 20);
        for (int r = 0; r < 20; r++)
        {
            // Fill a line with mixed content
            tb.SetCursor(3, 0);
            tb.WriteText($"{r}:".AsSpan(), CellAttributes.Default);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            tb.LineFeed();
        }
        AssertBufferClean(tb);
        Assert.True(tb.ScrollbackCount > 0);
    }

    // ================================================================
    // Wide glyph + soft scroll (full-screen LF region)
    // ================================================================

    [Fact]
    public void ScrollRegion_WideAtEdge_ScrollPreservesRegion()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(2, 8);
        // Write wide glyphs at region bottom edge
        for (int r = 2; r <= 8; r++)
        {
            tb.SetCursor(r, 16);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        // LF at bottom scrolls region up
        for (int i = 0; i < 5; i++)
        {
            tb.SetCursor(8, 0);
            tb.LineFeed();
        }
        AssertBufferClean(tb);
        // Rows outside region should be unaffected
        Assert.True(tb.GetCell(9, 0).IsEmpty || tb.GetCell(9, 0).Rune == 0);
    }

    // ================================================================
    // Combining marks (Unicode) + buffer operations
    // ================================================================

    [Fact]
    public void CombiningMark_Multiple_OnWideBase()
    {
        var tb = new TerminalBuffer(3, 15);
        // Write emoji with skin-tone modifier and ZWJ sequences
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c\u0301".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
    }

    [Fact]
    public void CombiningMark_ThenErase_Clean()
    {
        var tb = new TerminalBuffer(3, 15);
        tb.SetCursor(1, 2);
        tb.WriteText("e\u0301\u0302".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 0);
        tb.EraseLine(0);
        AssertBufferClean(tb);
        Assert.True(tb.GetCell(1, 2).IsEmpty);
    }

    [Fact]
    public void CombiningMark_ScrollRegion_Clean()
    {
        var tb = new TerminalBuffer(6, 20);
        tb.SetScrollRegion(2, 5);
        for (int i = 0; i < 10; i++)
        {
            tb.SetCursor(2, 3);
            tb.WriteText("a\u0301".AsSpan(), CellAttributes.Default);
            tb.SetCursor(5, 0);
            tb.LineFeed();
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // Backtab + tab stops corner cases
    // ================================================================

    [Fact]
    public void BackTab_FromColumn0_StaysAt0()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 0);
        int prev = tb.GetPrevTabStopFrom(0);
        Assert.Equal(0, prev);
    }

    [Fact]
    public void TabStop_Cleared_WideGlyph_NoEffect()
    {
        var tb = new TerminalBuffer(3, 30);
        tb.ClearAllTabStops();
        tb.SetTabStopAt(5);
        tb.SetCursor(1, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Clear tab stop at 5 (where wide glyph base sits)
        tb.ClearTabStopAt(5);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Empty/zero operations (should be no-ops)
    // ================================================================

    [Fact]
    public void ScrollUp_Zero_Clean()
    {
        var tb = new TerminalBuffer(5, 20);
        tb.ScrollUpLines(0);
        AssertBufferClean(tb);
    }

    [Fact]
    public void ScrollDown_Zero_Clean()
    {
        var tb = new TerminalBuffer(5, 20);
        tb.ScrollDownLines(0);
        AssertBufferClean(tb);
    }

    [Fact]
    public void EraseCharacters_Zero_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.EraseCharacters(0);
        AssertBufferClean(tb);
    }

    [Fact]
    public void InsertLines_Zero_Clean()
    {
        var tb = new TerminalBuffer(5, 20);
        tb.SetCursor(2, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.InsertLines(0);
        AssertBufferClean(tb);
    }

    [Fact]
    public void DeleteLines_Zero_Clean()
    {
        var tb = new TerminalBuffer(5, 20);
        tb.SetCursor(2, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.DeleteLines(0);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Multiple back-to-back CR sequences
    // ================================================================

    [Fact]
    public void MultipleCR_Sequence_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        for (int i = 0; i < 10; i++)
            tb.CarriageReturn();
        AssertBufferClean(tb);
        Assert.Equal(0, tb.CursorCol);
    }

    // ================================================================
    // SGR + erase + write repeating pattern (statusline stress)
    // ================================================================

    [Fact]
    public void RepeatedStatusLine_Updates_Clean()
    {
        var tb = new TerminalBuffer(3, 40);
        var bold = new CellAttributes { Bold = true };
        var normal = CellAttributes.Default;

        for (int cycle = 0; cycle < 500; cycle++)
        {
            // Statusline at row 2
            tb.SetCursor(2, 0);
            tb.WriteText("\u754c".AsSpan(), bold);
            tb.SetCursor(2, 3);
            tb.WriteText($" cycle {cycle:D4} ".AsSpan(), normal);
            tb.SetCursor(2, 20);
            tb.WriteText("\u754c".AsSpan(), bold);
            tb.SetCursor(2, 0);
            tb.EraseLine(0);
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // Rapid CUP to same location with wide overwrite
    // ================================================================

    [Fact]
    public void RapidCUP_SameLocation_Wide_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        for (int i = 0; i < 200; i++)
        {
            tb.SetCursor(1, 5);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // CRLF with wide glyph at different cursor positions
    // ================================================================

    [Fact]
    public void CRLF_Wide_Alternating_Clean()
    {
        var tb = new TerminalBuffer(10, 20);
        for (int i = 0; i < 30; i++)
        {
            int r = i % 8;
            tb.SetCursor(r, 0);
            tb.WriteText((i % 3 == 0 ? "\u754c" : "x").AsSpan(), CellAttributes.Default);
            tb.CarriageReturn();
            tb.LineFeed();
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // Scrolloff patterns: consecutive RI/LF + CUP interleaved
    // ================================================================

    /// <summary>
    /// Simulates Neovim scrolloff at top: consecutive reverse-index
    /// operations at the top of the scroll region scroll content down.
    /// </summary>
    [Fact]
    public void Scrolloff_Top_ConsecutiveRI_Clean()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(2, 9);

        // Fill region content
        for (int r = 2; r <= 9; r++)
        {
            tb.SetCursor(r, 1);
            tb.WriteText($"line{r}".AsSpan(), CellAttributes.Default);
        }

        // Consecutive RI at region top — scroll region down, new blank lines at top
        for (int i = 0; i < 5; i++)
        {
            tb.SetCursor(2, 0);
            tb.ReverseIndex();
            // Write on the new blank line at top of region
            tb.SetCursor(2, 1);
            tb.WriteText($"new_top_{i}".AsSpan(), CellAttributes.Default);
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// Simulates Neovim scrolloff at bottom: consecutive line feeds
    /// at the bottom of the scroll region scroll content up.
    /// </summary>
    [Fact]
    public void Scrolloff_Bottom_ConsecutiveLF_Clean()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(2, 9);

        for (int r = 2; r <= 9; r++)
        {
            tb.SetCursor(r, 1);
            tb.WriteText($"line{r}".AsSpan(), CellAttributes.Default);
        }

        // Consecutive LF at region bottom
        for (int i = 0; i < 5; i++)
        {
            tb.SetCursor(9, 0);
            tb.LineFeed();
            tb.SetCursor(9, 1);
            tb.WriteText($"new_bot_{i}".AsSpan(), CellAttributes.Default);
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// RI + CUP + write pattern — after reverse scroll, Neovim
    /// repositions the cursor and writes updated content.
    /// </summary>
    [Fact]
    public void Scrolloff_RI_CUP_write_Clean()
    {
        var tb = new TerminalBuffer(8, 30);
        tb.SetScrollRegion(2, 7);

        for (int cycle = 0; cycle < 30; cycle++)
        {
            // Reverse scroll
            tb.SetCursor(2, 0);
            tb.ReverseIndex();

            // CUP to various positions and rewrite
            for (int r = 2; r <= 7; r++)
            {
                tb.SetCursor(r, 0);
                tb.WriteText($"C{cycle:03}r{r}".AsSpan(), CellAttributes.Default);
                tb.SetCursor(r, 10);
                tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            }
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// LF + CUP + write pattern — after line feed scroll, Neovim
    /// repositions and rewrites.
    /// </summary>
    [Fact]
    public void Scrolloff_LF_CUP_write_Clean()
    {
        var tb = new TerminalBuffer(8, 30);
        tb.SetScrollRegion(2, 7);

        for (int cycle = 0; cycle < 30; cycle++)
        {
            tb.SetCursor(7, 0);
            tb.LineFeed();

            for (int r = 2; r <= 7; r++)
            {
                tb.SetCursor(r, 0);
                tb.WriteText($"C{cycle:03}r{r}".AsSpan(), CellAttributes.Default);
                tb.SetCursor(r, 10);
                tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            }
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// Rapid RI/LF alternation — simulates bouncing at scroll region
    /// boundary (scrolloff keeps cursor near region edge).
    /// </summary>
    [Fact]
    public void Scrolloff_Alternating_RI_LF_Clean()
    {
        var tb = new TerminalBuffer(6, 20);
        tb.SetScrollRegion(1, 4);

        for (int i = 0; i < 20; i++)
        {
            if (i % 2 == 0)
            {
                tb.SetCursor(1, 0);
                tb.ReverseIndex();
            }
            else
            {
                tb.SetCursor(4, 0);
                tb.LineFeed();
            }
            tb.SetCursor(2, 0);
            tb.WriteText($"x{i}".AsSpan(), CellAttributes.Default);
            tb.SetCursor(2, 5);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// RI with wide glyph at region top boundary — the wide glyph is
    /// at the top of the scroll region and RI pushes it down.
    /// </summary>
    [Fact]
    public void Scrolloff_RI_WideGlyphAtTop_Clean()
    {
        var tb = new TerminalBuffer(8, 20);
        tb.SetScrollRegion(2, 7);

        for (int i = 0; i < 10; i++)
        {
            tb.SetCursor(2, 3);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            tb.SetCursor(2, 0);
            tb.ReverseIndex();
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// LF with wide glyph at region bottom boundary.
    /// </summary>
    [Fact]
    public void Scrolloff_LF_WideGlyphAtBottom_Clean()
    {
        var tb = new TerminalBuffer(8, 20);
        tb.SetScrollRegion(2, 7);

        for (int i = 0; i < 10; i++)
        {
            tb.SetCursor(7, 3);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            tb.SetCursor(7, 0);
            tb.LineFeed();
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// Full matrix: scrolloff-like behavior across multiple region sizes
    /// </summary>
    [Fact]
    public void Scrolloff_VariousRegionSizes_Clean()
    {
        foreach (var regionHeight in new[] { 3, 5, 8 })
        {
            foreach (var scrollDirection in new[] { "up", "down" })
            {
                var tb = new TerminalBuffer(12, 20);
                int topMargin = 2;
                int bottomMargin = Math.Min(11, topMargin + regionHeight - 1);
                tb.SetScrollRegion(topMargin, bottomMargin);

                for (int cycle = 0; cycle < 15; cycle++)
                {
                    if (scrollDirection == "up")
                    {
                        tb.SetCursor(topMargin, 0);
                        tb.ReverseIndex();
                        tb.SetCursor(topMargin, 1);
                        tb.WriteText($"u{cycle}\u754c".AsSpan(), CellAttributes.Default);
                    }
                    else
                    {
                        tb.SetCursor(bottomMargin, 0);
                        tb.LineFeed();
                        tb.SetCursor(bottomMargin, 1);
                        tb.WriteText($"d{cycle}\u754c".AsSpan(), CellAttributes.Default);
                    }
                }

                AssertBufferClean(tb);
            }
        }
    }

    // ================================================================
    // G→gg→scroll cycle: the exact bug sequence
    // ================================================================

    /// <summary>
    /// Simulates the user's exact failure sequence:
    /// 1. G (scroll down to bottom) — pushes content into scrollback
    /// 2. gg (jump to top) — pulls content back from scrollback via RI
    /// 3. Scroll again — scrollback count must be consistent
    /// </summary>
    [Fact]
    public void JumpDown_JumpUp_Scroll_Down_Scrollback_Consistent()
    {
        var tb = new TerminalBuffer(10, 30);

        // Phase 1: scroll down (G) — write content while pushing to scrollback
        int totalLines = 50;
        for (int i = 0; i < totalLines; i++)
        {
            for (int r = 0; r < tb.Rows; r++)
            {
                tb.SetCursor(r, 0);
                tb.WriteText($"line{i}.{r}\u754c".AsSpan(), CellAttributes.Default);
            }
            // Simulate LF at bottom
            tb.SetCursor(tb.Rows - 1, 0);
            tb.LineFeed();
        }

        AssertBufferClean(tb);
        int scrollbackAfterG = tb.ScrollbackCount;
        Assert.True(scrollbackAfterG > 0, "Should have scrollback after G");

        // Phase 2: scroll up (gg) — reverse-index to top
        for (int i = 0; i < totalLines; i++)
        {
            tb.SetCursor(0, 0);
            tb.ReverseIndex();
        }

        AssertBufferClean(tb);
        int scrollbackAfterGG = tb.ScrollbackCount;
        // After RI scrolling all lines back, scrollback should have decreased
        Assert.True(scrollbackAfterGG < scrollbackAfterG,
            "Scrollback count should decrease after RI scrolls content back into view");

        // Phase 3: scroll down again
        for (int i = 0; i < totalLines; i++)
        {
            for (int r = 0; r < tb.Rows; r++)
            {
                tb.SetCursor(r, 0);
                tb.WriteText($"cycle2.{i}.{r}\u754c".AsSpan(), CellAttributes.Default);
            }
            tb.SetCursor(tb.Rows - 1, 0);
            tb.LineFeed();
        }

        AssertBufferClean(tb);
        // Scrollback should be growing again
        Assert.True(tb.ScrollbackCount > scrollbackAfterGG,
            "Scrollback should grow after second scroll phase");

        // All scrollback lines should have valid content (not garbage)
        for (int i = 0; i < tb.ScrollbackCount && i < 20; i++)
        {
            var line = tb.GetScrollbackLine(i);
            Assert.False(string.IsNullOrEmpty(line.Text),
                $"Scrollback line {i} should have content");
        }
    }

    /// <summary>
    /// RI/LF alternating stress — verifies _totalScrolled stays bounded
    /// after rapid up/down scrolling.
    /// </summary>
    [Fact]
    public void Rapid_UpDown_Scroll_Scrollback_Stable()
    {
        var tb = new TerminalBuffer(5, 20);

        for (int cycle = 0; cycle < 30; cycle++)
        {
            // Write content
            for (int r = 0; r < tb.Rows; r++)
            {
                tb.SetCursor(r, 2);
                tb.WriteText($"cycle{cycle}r{r}\u754c".AsSpan(), CellAttributes.Default);
            }

            // Scroll down (LF)
            tb.SetCursor(tb.Rows - 1, 0);
            tb.LineFeed();

            // Scroll up (RI)
            tb.SetCursor(0, 0);
            tb.ReverseIndex();
        }

        AssertBufferClean(tb);
        // After equal number of LF and RI, net scrollback should be near 0
        Assert.True(tb.ScrollbackCount >= 0, "Scrollback count should never be negative");
    }

    [Fact]
    public void ScrollRegion_ReverseIndex_AtActualTopMargin_ShiftsRowsDown()
    {
        var tb = new TerminalBuffer(8, 20);
        tb.SetScrollRegion(2, 7);

        for (int row = 0; row < tb.Rows; row++)
        {
            tb.SetCursor(row, 0);
            tb.WriteText($"R{row}".AsSpan(), CellAttributes.Default);
        }

        // Actual top margin for SetScrollRegion(2, 7) is logical row 1.
        tb.SetCursor(1, 0);
        tb.ReverseIndex();

        Assert.Equal("", tb.GetRowText(1).Trim());
        Assert.Equal("R1", tb.GetRowText(2).Trim());
        Assert.Equal("R2", tb.GetRowText(3).Trim());
        Assert.Equal("R3", tb.GetRowText(4).Trim());
        Assert.Equal("R4", tb.GetRowText(5).Trim());
        Assert.Equal("R5", tb.GetRowText(6).Trim());
        Assert.Equal("R7", tb.GetRowText(7).Trim());
        Assert.Equal("R0", tb.GetRowText(0).Trim());
        AssertBufferClean(tb);
    }

    [Fact]
    public void ScrollRegion_LineFeed_AtActualBottomMargin_ShiftsRowsUp()
    {
        var tb = new TerminalBuffer(8, 20);
        tb.SetScrollRegion(2, 7);

        for (int row = 0; row < tb.Rows; row++)
        {
            tb.SetCursor(row, 0);
            tb.WriteText($"R{row}".AsSpan(), CellAttributes.Default);
        }

        // Actual bottom margin for SetScrollRegion(2, 7) is logical row 6.
        tb.SetCursor(6, 0);
        tb.LineFeed();

        Assert.Equal("R0", tb.GetRowText(0).Trim());
        Assert.Equal("R2", tb.GetRowText(1).Trim());
        Assert.Equal("R3", tb.GetRowText(2).Trim());
        Assert.Equal("R4", tb.GetRowText(3).Trim());
        Assert.Equal("R5", tb.GetRowText(4).Trim());
        Assert.Equal("R6", tb.GetRowText(5).Trim());
        Assert.Equal("", tb.GetRowText(6).Trim());
        Assert.Equal("R7", tb.GetRowText(7).Trim());
        AssertBufferClean(tb);
    }

    [Fact]
    public void ReverseIndex_FullScreen_RevealsScrollbackBeforeBlanking()
    {
        var tb = new TerminalBuffer(3, 16);

        void WriteLine(string text)
        {
            tb.CarriageReturn();
            tb.WriteText(text.AsSpan(), CellAttributes.Default);
            tb.CarriageReturn();
            tb.LineFeed();
        }

        WriteLine("L0");
        WriteLine("L1");
        WriteLine("L2");
        WriteLine("L3");
        WriteLine("L4");

        Assert.Equal(3, tb.ScrollbackCount);
        Assert.Contains("L0", tb.GetScrollbackLine(0).ToString());
        Assert.Contains("L2", tb.GetScrollbackLine(2).ToString());

        tb.SetCursor(0, 0);
        tb.ReverseIndex();

        Assert.Equal(2, tb.ScrollbackCount);
        Assert.Equal("L2", tb.GetRowText(0).Trim());
        Assert.Equal("L3", tb.GetRowText(1).Trim());
        Assert.Equal("L4", tb.GetRowText(2).Trim());

        tb.SetCursor(0, 0);
        tb.ReverseIndex();

        Assert.Equal(1, tb.ScrollbackCount);
        Assert.Equal("L1", tb.GetRowText(0).Trim());
        Assert.Equal("L2", tb.GetRowText(1).Trim());
        Assert.Equal("L3", tb.GetRowText(2).Trim());

        tb.SetCursor(0, 0);
        tb.ReverseIndex();

        Assert.Equal(0, tb.ScrollbackCount);
        Assert.Equal("L0", tb.GetRowText(0).Trim());
        Assert.Equal("L1", tb.GetRowText(1).Trim());
        Assert.Equal("L2", tb.GetRowText(2).Trim());

        tb.SetCursor(0, 0);
        tb.ReverseIndex();

        Assert.Equal(0, tb.ScrollbackCount);
        Assert.Equal(string.Empty, tb.GetRowText(0).Trim());
        Assert.Equal("L0", tb.GetRowText(1).Trim());
        Assert.Equal("L1", tb.GetRowText(2).Trim());
        AssertBufferClean(tb);
    }

    [Fact]
    public void ScrollRegion_CumulativeLfRiCyclesWithRingWrap_PreservesRowContent()
    {
        int rows = 10;
        int cols = 30;
        int scrollback = 20;
        var tb = new TerminalBuffer(rows, cols, scrollback);

        // Fill all rows with unique markers
        void WriteRow(int r, string prefix)
        {
            tb.SetCursor(r, 0);
            tb.WriteText($"{prefix}-R{r:D2}".AsSpan(), CellAttributes.Default);
        }
        for (int r = 0; r < rows; r++) WriteRow(r, "INIT");

        // Advance ring-buffer head near wrap boundary
        for (int i = 0; i < scrollback - 2; i++)
        {
            tb.SetCursor(rows - 1, 0);
            tb.LineFeed();
        }

        // Rewrite rows after head has advanced
        for (int r = 0; r < rows; r++) WriteRow(r, "FILL");

        // Set scroll region (1-based: 2 to 8, so logical rows 1-7)
        tb.SetScrollRegion(2, 8);

        // Rewrite region rows with unique cycle-0 markers
        for (int r = 1; r <= 7; r++)
        {
            tb.SetCursor(r, 0);
            tb.WriteText($"CYC0-R{r:D2}".AsSpan(), CellAttributes.Default);
        }

        string RowText(int r) => tb.GetRowText(r).Trim();

        // ---- Alternating LF (bottom of region) and RI (top of region) ----
        for (int cycle = 1; cycle <= 50; cycle++)
        {
            // LF at region bottom (logical row 7)
            tb.SetCursor(7, 0);
            tb.LineFeed();

            // Scroll top row of region down using RI at region top (logical row 1)
            tb.SetCursor(1, 0);
            tb.ReverseIndex();
        }

        // After 50 balanced cycles:
        // - Rows 0 and 8,9 (outside region) must be untouched
        Assert.Equal("FILL-R00", RowText(0));
        Assert.Equal("FILL-R08", RowText(8));
        Assert.Equal("FILL-R09", RowText(9));

        // - Region top (row 1) may be blank (each RI scrolls blank into top)
        // - Region rows 2-7 should still show their cycle-0 content
        //   because each LF shift up was reversed by the corresponding RI shift down
        Assert.Equal("", RowText(1));
        Assert.Equal("CYC0-R02", RowText(2));
        Assert.Equal("CYC0-R03", RowText(3));
        Assert.Equal("CYC0-R04", RowText(4));
        Assert.Equal("CYC0-R05", RowText(5));
        Assert.Equal("CYC0-R06", RowText(6));
        Assert.Equal("CYC0-R07", RowText(7));

        AssertBufferClean(tb);
    }

    [Fact]
    public void ScrollRegion_RepeatedScrollDownUp_OutsideRowsUntouched()
    {
        var tb = new TerminalBuffer(12, 30);

        for (int r = 0; r < tb.Rows; r++)
        {
            tb.SetCursor(r, 0);
            tb.WriteText($"R{r:D2}".AsSpan(), CellAttributes.Default);
        }

        tb.SetScrollRegion(3, 10);

        // Many scroll-down operations (LF at region bottom = logical row 9)
        for (int i = 0; i < 200; i++)
        {
            tb.SetCursor(9, 0);
            tb.LineFeed();
        }

        // Rows outside region must be untouched
        Assert.Equal("R00", tb.GetRowText(0).Trim());
        Assert.Equal("R01", tb.GetRowText(1).Trim());
        // Row 10 is below region (_scrollBottom == 9)
        Assert.Equal("R10", tb.GetRowText(10).Trim());
        Assert.Equal("R11", tb.GetRowText(11).Trim());
        AssertBufferClean(tb);

        // Many scroll-up operations (RI at region top = logical row 2)
        for (int i = 0; i < 200; i++)
        {
            tb.SetCursor(2, 0);
            tb.ReverseIndex();
        }

        // Rows outside region still untouched
        Assert.Equal("R00", tb.GetRowText(0).Trim());
        Assert.Equal("R01", tb.GetRowText(1).Trim());
        Assert.Equal("R10", tb.GetRowText(10).Trim());
        Assert.Equal("R11", tb.GetRowText(11).Trim());
        AssertBufferClean(tb);
    }

    [Fact]
    public void ScrollThenWrite_NoStaleCharLeakFromShiftedRow()
    {
        var tb = new TerminalBuffer(10, 40);
        tb.SetScrollRegion(2, 9);

        // Fill rows 1-8 (inside region) with known content
        for (int r = 1; r <= 8; r++)
        {
            tb.SetCursor(r, 0);
            tb.WriteText($"ROW{r} abc".AsSpan(), CellAttributes.Default);
        }

        // Write a distinct marker at the end of row 8 (bottom of region)
        tb.SetCursor(8, 35);
        tb.WriteText("MARKER".AsSpan(), CellAttributes.Default);

        // Scroll (LF at bottom of region = row 8)
        tb.SetCursor(8, 0);
        tb.LineFeed();

        // After scroll, row 7 has old row 8 content including MARKER.
        // Write new content starting at the same column on row 7.
        tb.SetCursor(7, 0);
        tb.WriteText("REPLACEMENT".AsSpan(), CellAttributes.Default);

        // The MARKER from the old row 8 must NOT appear on row 7 after the write.
        var row7 = tb.GetRowText(7);
        Assert.StartsWith("REPLACEMENT", row7.Trim());
        Assert.DoesNotContain("MARKER", row7);

        AssertBufferClean(tb);
    }

    [Fact]
    public void NeovimStyle_ScrollThenWrite_ContentAppearsAtCorrectRow()
    {
        int rows = 73;
        int cols = 120;
        var tb = new TerminalBuffer(rows, cols);

        tb.SetScrollRegion(2, 72);

        for (int cycle = 0; cycle < 50; cycle++)
        {
            // CUP to bottom of region (0-based row 71)
            tb.MoveCursorTo(71, 0);
            // LF scrolls the region
            tb.LineFeed();
            // Write new content at the (now blank) bottom
            tb.MoveCursorTo(71, 0);
            tb.WriteText($"CYCLE-{cycle:D4}-bottom".AsSpan(), CellAttributes.Default);

            // Verify: bottom row has the new content
            var text = tb.GetRowText(71).Trim();
            Assert.StartsWith($"CYCLE-{cycle:D4}", text);
        }

        // Verify rows above region (0, 72) are untouched
        Assert.Equal("", tb.GetRowText(0).Trim());
        Assert.Equal("", tb.GetRowText(72).Trim());

        AssertBufferClean(tb);
    }

    [Fact]
    public void NeovimStyle_ScrollingUpAndDown_RowsStayInSync()
    {
        int rows = 73;
        int cols = 120;
        var tb = new TerminalBuffer(rows, cols);

        tb.SetScrollRegion(2, 72);

        // Fill region with unique markers
        for (int r = 1; r <= 71; r++)
        {
            tb.SetCursor(r, 0);
            tb.WriteText($"ROW-{r:D3}".AsSpan(), CellAttributes.Default);
        }

        // Scroll down (LF + write) 30 times
        for (int i = 0; i < 30; i++)
        {
            tb.SetCursor(71, 0);
            tb.LineFeed();
            tb.SetCursor(71, 0);
            tb.WriteText($"NEW-{i:D3}".AsSpan(), CellAttributes.Default);
        }

        // Row 1 should now have ROW-031 (shifted up 30 times from original row 31)
        Assert.Equal("ROW-031", tb.GetRowText(1).Trim());

        // NEW lines occupy rows 42-71 (30 lines after 30 cycles)
        Assert.Equal("NEW-000", tb.GetRowText(42).Trim());
        Assert.Equal("NEW-029", tb.GetRowText(71).Trim());
        // Original content that was at row 71 is now at row 41
        Assert.Equal("ROW-071", tb.GetRowText(41).Trim());

        // Now scroll back up (RI + write) 15 times

        // Now scroll back up (RI + write) 15 times
        for (int i = 0; i < 15; i++)
        {
            tb.SetCursor(1, 0);
            tb.ReverseIndex();
            tb.SetCursor(1, 0);
            tb.WriteText($"UP-{i:D3}".AsSpan(), CellAttributes.Default);
        }

        // After 15 RIs + writes, rows outside region must still be untouched
        Assert.Equal("", tb.GetRowText(0).Trim());
        Assert.Equal("", tb.GetRowText(72).Trim());
        AssertBufferClean(tb);
    }
}
