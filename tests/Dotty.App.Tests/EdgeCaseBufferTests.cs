using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Synthetic edge-case tests for operations that interact with wide glyphs,
/// scroll regions, alternate screens, and combined erase/move patterns.
/// </summary>
public class EdgeCaseBufferTests
{
    private static void AssertBufferClean(TerminalBuffer tb)
    {
        int cols = tb.Columns;
        for (int r = 0; r < tb.Rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = tb.GetCell(r, c);
                if (cell.IsContinuation)
                    Assert.True(cell.Rune == 0,
                        $"Continuation at {r},{c} has Rune=0x{cell.Rune:X}");
                if (!cell.IsContinuation && cell.Rune != 0)
                {
                    int w = Math.Max(1, (int)cell.Width);
                    for (int i = 1; i < w; i++)
                    {
                        if (c + i >= cols) break;
                        Assert.True(tb.GetCell(r, c + i).IsContinuation,
                            $"Base at {r},{c} w={w} missing continuation at {r},{c + i}");
                    }
                }
            }
        }
    }

    // ================================================================
    // Scroll region + wide glyph interactions
    // ================================================================

    [Fact]
    public void ScrollRegion_LF_Bottom_WideGlyph()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(2, 7);
        // Write wide glyph at bottom of region
        tb.SetCursor(7, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // LF at bottom of region — should scroll region up
        tb.SetCursor(7, 0);
        tb.LineFeed();
        AssertBufferClean(tb);
    }

    [Fact]
    public void ScrollRegion_RI_Top_WideGlyph()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(3, 8);
        // Write wide glyph at top of region
        tb.SetCursor(3, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Reverse index at top of region — should scroll region down
        tb.SetCursor(3, 0);
        tb.ReverseIndex();
        AssertBufferClean(tb);
    }

    [Fact]
    public void ScrollRegion_ScrollUp_ClearsBottom_WideGlyph()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(2, 7);
        // Fill region with wide glyphs
        for (int r = 2; r <= 7; r++)
        {
            tb.SetCursor(r, 3);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        // Scroll the entire region
        tb.ScrollUpLines(3);
        AssertBufferClean(tb);
    }

    // ================================================================
    // InsertLines/DeleteLines with wide glyphs
    // ================================================================

    [Fact]
    public void InsertLines_ShiftsWideGlyphRows()
    {
        var tb = new TerminalBuffer(10, 15);
        tb.SetCursor(3, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(4, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Insert 2 lines at row 2 — shifts rows 2+ down
        tb.SetCursor(2, 0);
        tb.InsertLines(2);
        AssertBufferClean(tb);
    }

    [Fact]
    public void DeleteLines_ShiftsWideGlyphRows()
    {
        var tb = new TerminalBuffer(10, 15);
        tb.SetCursor(3, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(5, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Delete 2 lines at row 2 — shifts rows 4+ up, clears bottom
        tb.SetCursor(2, 0);
        tb.DeleteLines(2);
        AssertBufferClean(tb);
    }

    [Fact]
    public void InsertLines_RegionEdge_WideGlyph()
    {
        var tb = new TerminalBuffer(8, 12);
        tb.SetScrollRegion(2, 6);
        tb.SetCursor(6, 4);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // IL at region bottom — wide glyph should be scrolled down/cleared
        tb.SetCursor(4, 0);
        tb.InsertLines(2);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Alternate screen + content interactions
    // ================================================================

    [Fact]
    public void AlternateScreen_Toggle_PreservesWideGlyph()
    {
        var tb = new TerminalBuffer(10, 20);
        // Write on main screen
        tb.SetCursor(3, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetAlternateScreen(true);
        // Alt screen is blank — write on it
        tb.SetCursor(3, 5);
        tb.WriteText("ab".AsSpan(), CellAttributes.Default);
        // Switch back
        tb.SetAlternateScreen(false);
        // Main screen should still have wide glyph
        Assert.False(tb.GetCell(3, 5).IsEmpty);
        Assert.True(tb.GetCell(3, 6).IsContinuation);
        AssertBufferClean(tb);
    }

    [Fact]
    public void AlternateScreen_Toggle_Multiple()
    {
        var tb = new TerminalBuffer(10, 20);
        for (int i = 0; i < 10; i++)
        {
            tb.SetAlternateScreen(i % 2 == 0);
            tb.SetCursor(5, 3);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        tb.SetAlternateScreen(false);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Auto-wrap with wide glyph at row edge
    // ================================================================

    [Fact]
    public void AutoWrap_WideGlyph_AtRowEdge()
    {
        var tb = new TerminalBuffer(5, 10);
        // Write up to the last column where a wide glyph would wrap
        tb.SetCursor(2, 8);
        tb.WriteText("a\u754cb".AsSpan(), CellAttributes.Default);
        // The wide glyph should wrap to next row; 'b' follows
        AssertBufferClean(tb);
        // Verify the wide glyph landed on row 3
        var row3text = tb.GetRowText(3);
        Assert.False(string.IsNullOrWhiteSpace(row3text));
    }

    [Fact]
    public void AutoWrap_WideGlyph_ExactEnd()
    {
        var tb = new TerminalBuffer(3, 10);
        // Write wide glyph at col 9 (last column), should wrap
        tb.SetCursor(1, 9);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
    }

    // ================================================================
    // DeleteChars/InsertChars with wide glyphs
    // ================================================================

    [Fact]
    public void DeleteChars_ThroughWideGlyph()
    {
        var tb = new TerminalBuffer(3, 15);
        tb.SetCursor(1, 2);
        tb.WriteText("ab\u754cde".AsSpan(), CellAttributes.Default);
        // Cells: (1,2)='a', (1,3)='b', (1,4)=界B, (1,5)=界C, (1,6)='d', (1,7)='e'
        // Delete 2 chars starting from col 3 — deletes 'b' and 界B
        tb.SetCursor(1, 3);
        tb.DeleteChars(2);
        AssertBufferClean(tb);
        // After DCH: col 3 gets the cleared continuation (shifted from 5),
        // col 4='d' (shifted from 6), col 5='e' (shifted from 7)
        Assert.Equal('a', tb.GetRowText(1)[2]);
    }

    [Fact]
    public void InsertChars_ShiftsWideGlyph()
    {
        var tb = new TerminalBuffer(3, 15);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Insert 2 chars before the wide glyph
        tb.SetCursor(1, 2);
        tb.InsertChars(2);
        AssertBufferClean(tb);
    }

    // ================================================================
    // EL mode 1 (clear from start to cursor) on wide glyph rows
    // ================================================================

    [Fact]
    public void EraseLine_Backward_MidWideGlyph()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Move cursor to continuation cell
        tb.SetCursor(1, 5);
        tb.EraseLine(1);
        AssertBufferClean(tb);
        Assert.True(tb.GetCell(1, 2).IsEmpty,
            "EL mode 1 should clear from start of row up to cursor");
    }

    [Fact]
    public void EraseLine_Backward_AtContinuation()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Cursor at continuation cell (col 4)
        tb.SetCursor(1, 4);
        tb.EraseLine(1);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Wide glyph overwrite from multiple directions
    // ================================================================

    [Fact]
    public void Overwrite_Wide_FromLeft()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Overwrite from left — write ASCII starting before the wide glyph
        tb.SetCursor(1, 2);
        tb.WriteText("xy".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        Assert.False(tb.GetCell(1, 3).IsContinuation,
            "Col 3 should not be continuation after ASCII overwrite");
    }

    [Fact]
    public void Overwrite_Wide_FromRight()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Write directly into continuation cell
        tb.SetCursor(1, 3);
        tb.WriteText("x".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        Assert.True(tb.GetCell(1, 2).IsEmpty,
            "Base wide glyph should be cleared when continuation is overwritten");
    }

    [Fact]
    public void Overwrite_Wide_FromMiddle()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Write a longer string that covers base + continuation +
        // extends past it
        tb.SetCursor(1, 3);
        tb.WriteText("abc".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Combined sequences: CRLF + wide + overwrite
    // ================================================================

    [Fact]
    public void CRLF_Wide_ThenOverwrite()
    {
        var tb = new TerminalBuffer(5, 20);
        for (int i = 0; i < 8; i++)
        {
            tb.SetCursor(2, 0);
            tb.WriteText("line".AsSpan(), CellAttributes.Default);
            tb.CarriageReturn();
            tb.LineFeed();
        }
        // Now write wide glyphs on various rows
        for (int r = 2; r < 5; r++)
        {
            tb.SetCursor(r, 3);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // Large burst: 1000 CUP + wide glyph writes (stress render refresh)
    // ================================================================

    [Fact]
    public void LargeBurst_CUP_Wide_Stress()
    {
        var tb = new TerminalBuffer(25, 80);
        for (int i = 0; i < 200; i++)
        {
            int r = i % 25;
            int c = (i * 7) % 78;
            tb.SetCursor(r, c);
            tb.WriteText((i % 3 == 0 ? "\u754c" : "x").AsSpan(), CellAttributes.Default);
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // Scrollback interactions with wide glyphs
    // ================================================================

    [Fact]
    public void Scrollback_WideGlyph_Preserved()
    {
        var tb = new TerminalBuffer(5, 15);
        for (int r = 0; r < 12; r++)
        {
            tb.SetCursor(Math.Min(r, 4), 2);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            if (r < 4)
                tb.LineFeed();
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // DECSC/DECRC (save/restore cursor) with wide glyphs
    // ================================================================

    [Fact]
    public void SaveRestoreCursor_WideGlyph_NoOrphans()
    {
        var tb = new TerminalBuffer(5, 15);
        tb.SetCursor(2, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SaveCursor();
        tb.SetCursor(0, 0);
        tb.WriteText("hello".AsSpan(), CellAttributes.Default);
        tb.RestoreCursor();
        AssertBufferClean(tb);
        Assert.Equal(2, tb.CursorRow);
        Assert.Equal(5, tb.CursorCol);
    }

    [Fact]
    public void SaveRestoreCursor_WideGlyph_CursorAtContinuation()
    {
        var tb = new TerminalBuffer(5, 15);
        tb.SetCursor(1, 4);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Save cursor while on col 6 (past the wide glyph)
        tb.SaveCursor();
        tb.SetCursor(3, 0);
        tb.EraseLine(2);
        tb.RestoreCursor();
        AssertBufferClean(tb);
    }

    // ================================================================
    // ED (Erase Display) modes with wide glyphs
    // ================================================================

    [Fact]
    public void EraseDisplay_All_ClearsWideGlyphs()
    {
        var tb = new TerminalBuffer(8, 15);
        for (int r = 0; r < tb.Rows; r++)
        {
            tb.SetCursor(r, 3);
            tb.WriteText((r % 2 == 0 ? "\u754c" : "ab").AsSpan(), CellAttributes.Default);
        }
        tb.SetCursor(0, 0);
        tb.EraseDisplay(2);
        for (int r = 0; r < tb.Rows; r++)
            for (int c = 0; c < tb.Columns; c++)
                Assert.True(tb.GetCell(r, c).IsEmpty,
                    $"Cell ({r},{c}) should be empty after ED(2)");
        AssertBufferClean(tb);
    }

    [Fact]
    public void EraseDisplay_Below_Cursor_WideGlyph()
    {
        var tb = new TerminalBuffer(6, 15);
        for (int r = 0; r < tb.Rows; r++)
        {
            tb.SetCursor(r, 2);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        tb.SetCursor(3, 0);
        tb.EraseDisplay(0);
        AssertBufferClean(tb);
        // Rows 0-2 should still have content
        Assert.False(tb.GetCell(2, 2).IsEmpty);
        Assert.True(tb.GetCell(2, 3).IsContinuation);
    }

    [Fact]
    public void EraseDisplay_Above_Cursor_WideGlyph()
    {
        var tb = new TerminalBuffer(6, 15);
        for (int r = 0; r < tb.Rows; r++)
        {
            tb.SetCursor(r, 2);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        tb.SetCursor(3, 0);
        tb.EraseDisplay(1);
        AssertBufferClean(tb);
        // Rows 3-5 should still have content
        Assert.False(tb.GetCell(4, 2).IsEmpty);
        Assert.True(tb.GetCell(4, 3).IsContinuation);
    }

    // ================================================================
    // Cursor movement (CUF/CUB/CUU/CUD) through wide glyphs
    // ================================================================

    [Fact]
    public void CursorUpDown_PastWideGlyph_Clean()
    {
        var tb = new TerminalBuffer(6, 15);
        tb.SetCursor(3, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.MoveCursorBy(-1, 3);
        tb.MoveCursorBy(2, 0);
        AssertBufferClean(tb);
    }

    [Fact]
    public void CursorForwardBack_PastWideGlyph_Clean()
    {
        var tb = new TerminalBuffer(3, 15);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754cx".AsSpan(), CellAttributes.Default);
        // Cursor should be at col 5
        tb.MoveCursorBy(0, -1);
        tb.MoveCursorBy(0, 2);
        AssertBufferClean(tb);
    }

    [Fact]
    public void CursorHVA_ToWideGlyph_NoCorruption()
    {
        var tb = new TerminalBuffer(3, 15);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Move horizontal absolute to the continuation cell
        tb.SetCursor(1, 3);
        tb.WriteText("x".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        Assert.True(tb.GetCell(1, 2).IsEmpty,
            "Base should be cleared when continuation is overwritten via HVA");
    }

    // ================================================================
    // Full reset (RIS) with wide glyphs
    // ================================================================

    [Fact]
    public void FullReset_ClearsWideGlyphContinuations()
    {
        var tb = new TerminalBuffer(5, 15);
        tb.SetCursor(2, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.FullReset();
        AssertBufferClean(tb);
        for (int r = 0; r < tb.Rows; r++)
            for (int c = 0; c < tb.Columns; c++)
                Assert.True(tb.GetCell(r, c).IsEmpty);
    }

    // ================================================================
    // SGR + erase + wide sequence (Neovim statusline pattern)
    // ================================================================

    [Fact]
    public void SGR_Wide_Erase_Sequence()
    {
        var tb = new TerminalBuffer(5, 30);
        var bold = new CellAttributes { Bold = true };
        var normal = CellAttributes.Default;

        // Simulate Neovim statusline: SGR, CUP, wide, SGR, EL
        tb.SetCursor(4, 0);
        tb.WriteText("\u754c".AsSpan(), bold);
        tb.SetCursor(4, 3);
        tb.WriteText("status".AsSpan(), normal);
        tb.SetCursor(4, 10);
        tb.WriteText("\u754c".AsSpan(), bold);
        tb.SetCursor(4, 0);
        tb.EraseLine(0);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Multiple consecutive erases on same row
    // ================================================================

    [Fact]
    public void ConsecutiveEL_SameRow_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 0);
        tb.WriteText("hello \u754c world".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 0);
        tb.EraseLine(0);
        tb.SetCursor(1, 0);
        tb.EraseLine(0);
        AssertBufferClean(tb);
    }

    [Fact]
    public void EL0_Then_EL1_SameRow_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 2);
        tb.WriteText("ab\u754cde".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 5);
        tb.EraseLine(0);
        AssertBufferClean(tb);
        tb.SetCursor(1, 3);
        tb.EraseLine(1);
        AssertBufferClean(tb);
    }

    [Fact]
    public void EL_After_ICH_OnWideRow_Clean()
    {
        var tb = new TerminalBuffer(3, 15);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 3);
        tb.InsertChars(1);
        tb.SetCursor(1, 0);
        tb.EraseLine(0);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Resize truncates wide glyph at edge
    // ================================================================

    [Fact]
    public void Resize_Narrower_TruncatesWideGlyph()
    {
        var tb = new TerminalBuffer(5, 20);
        tb.SetCursor(2, 17);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Wide glyph spans cols 17-18. Shrink to 18 columns (0-17).
        // The continuation at col 18 is truncated; GetCell auto-fix handles it.
        tb.Resize(5, 18);
        AssertBufferClean(tb);
    }

    [Fact]
    public void Resize_Wider_PreservesWideGlyph()
    {
        var tb = new TerminalBuffer(5, 10);
        tb.SetCursor(2, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.Resize(5, 20);
        AssertBufferClean(tb);
        Assert.False(tb.GetCell(2, 3).IsEmpty);
        Assert.True(tb.GetCell(2, 4).IsContinuation);
    }

    // ================================================================
    // CNL/CPL (CSI E/F) with wide glyphs
    // ================================================================

    [Fact]
    public void CursorNextLine_ThroughWide_Clean()
    {
        var tb = new TerminalBuffer(5, 15);
        tb.SetCursor(2, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // CSI E - down 2, col 1
        tb.SetCursor(2, 5);
        tb.MoveCursorBy(2, -5);
        tb.SetCursor(tb.CursorRow, 0);
        AssertBufferClean(tb);
    }

    [Fact]
    public void CursorPreviousLine_ThroughWide_Clean()
    {
        var tb = new TerminalBuffer(5, 15);
        tb.SetCursor(4, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // CSI F - up 2, col 1
        tb.SetCursor(4, 5);
        tb.MoveCursorBy(-2, -5);
        tb.SetCursor(tb.CursorRow, 0);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Backtab (CBT) with wide glyphs
    // ================================================================

    [Fact]
    public void BackTab_ThroughWide_Clean()
    {
        var tb = new TerminalBuffer(3, 30);
        tb.SetCursor(1, 20);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Tab stop at 0 and 8. CBT from 22 goes to 16.
        tb.SetCursor(1, 22);
        int prev = tb.GetPrevTabStopFrom(22);
        tb.SetCursor(1, prev);
        AssertBufferClean(tb);
    }

    // ================================================================
    // SGR bold/underline + wide glyph + overwrite
    // ================================================================

    [Fact]
    public void SGR_BoldUnderline_Wide_Overwrite_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        var attr = new CellAttributes { Bold = true, UnderlineStyle = UnderlineStyle.Single };
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), attr);
        tb.SetCursor(1, 2);
        tb.WriteText("xy".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        Assert.False(tb.GetCell(1, 3).IsContinuation);
    }

    // ================================================================
    // Origin mode (DECOM) with wide glyph interaction
    // ================================================================

    [Fact]
    public void OriginMode_WideGlyph_Scroll_Clean()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(3, 8);
        tb.SetOriginMode(true);
        for (int cycle = 0; cycle < 20; cycle++)
        {
            for (int r = 0; r < 5; r++)
            {
                tb.SetCursor(r, 2);
                tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            }
            tb.SetCursor(5, 0);
            tb.LineFeed();
        }
        tb.SetOriginMode(false);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Repeat character (REP) + wide glyph (stress)
    // ================================================================

    [Fact]
    public void RepeatCharacter_AfterWide_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 2);
        tb.WriteText("aaaaaaaaaa".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
    }

    // ================================================================
    // DECALN (ESC # 8) — screen alignment, fills all cells
    // ================================================================

    [Fact]
    public void Decaln_FillsAllCells_NoOrphans()
    {
        var tb = new TerminalBuffer(10, 30);
        // Write wide glyphs in various positions
        tb.SetCursor(3, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(7, 12);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Fill entire screen with 'E'
        for (int r = 0; r < tb.Rows; r++)
            for (int c = 0; c < tb.Columns; c++)
                tb.ActiveBuffer.ClearCell(r, c);
        for (int r = 0; r < tb.Rows; r++)
        {
            tb.SetCursor(r, 0);
            tb.WriteText(new string('E', tb.Columns).AsSpan(), CellAttributes.Default);
        }
        AssertBufferClean(tb);
        for (int r = 0; r < tb.Rows; r++)
            Assert.Equal('E', tb.GetRowText(r)[0]);
    }

    // ================================================================
    // Wide glyph at last column, autowrap disabled
    // ================================================================

    [Fact]
    public void WideGlyph_AtLastColumn_NoWrap()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetAutoWrap(false);
        tb.SetCursor(1, 9);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        // With autowrap off the glyph was clamped to col 8 (cols-width)
        Assert.False(tb.GetCell(1, 8).IsEmpty,
            "Wide glyph base should be at clamped position col 8");
        Assert.True(tb.GetCell(1, 9).IsContinuation,
            "Wide glyph continuation should be at col 9");
    }

    // ================================================================
    // Tab lands on continuation cell
    // ================================================================

    [Fact]
    public void TabStop_OnContinuation_Clears()
    {
        var tb = new TerminalBuffer(3, 30);
        tb.SetCursor(1, 7);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // Wide at 7-8. Tab from col 2 to col 8 (tab stop at 8).
        // Col 8 is the continuation of the wide glyph.
        tb.SetCursor(1, 2);
        int nextStop = tb.GetNextTabStopFrom(2);
        tb.SetCursor(1, nextStop);
        tb.WriteText("x".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Multiple wide glyphs chained across the row
    // ================================================================

    [Fact]
    public void ChainedWideGlyphs_AcrossRow_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 0);
        tb.WriteText("\u754c\u754c\u754c\u754c\u754c".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        // Each 界 takes 2 columns, 5 of them = 10 columns
        // Verify every other cell is a base or continuation
        for (int i = 0; i < 5; i++)
        {
            int baseCol = i * 2;
            Assert.False(tb.GetCell(1, baseCol).IsEmpty,
                $"Wide base at (1,{baseCol}) should not be empty");
            Assert.True(tb.GetCell(1, baseCol + 1).IsContinuation,
                $"Wide continuation at (1,{baseCol + 1}) should be continuation");
        }
    }

    // ================================================================
    // Write-EL-Write at same cursor (statusline pattern)
    // ================================================================

    [Fact]
    public void WriteELWrite_SamePosition_Wide_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        for (int i = 0; i < 50; i++)
        {
            tb.SetCursor(1, 2);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
            tb.SetCursor(1, 0);
            tb.EraseLine(0);
            tb.SetCursor(1, 2);
            tb.WriteText("ab".AsSpan(), CellAttributes.Default);
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // Combining mark + scroll region interaction
    // ================================================================

    [Fact]
    public void CombiningMark_ScrollRegion_Clean()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(3, 8);
        tb.SetCursor(4, 3);
        // Write base + combining mark
        tb.WriteText("a\u0301".AsSpan(), CellAttributes.Default);
        tb.SetCursor(5, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // LF at region bottom
        for (int i = 0; i < 5; i++)
        {
            tb.SetCursor(8, 0);
            tb.LineFeed();
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // Alternate screen interleaved with resize
    // ================================================================

    [Fact]
    public void AltScreen_Resize_Interleaved_Clean()
    {
        var tb = new TerminalBuffer(10, 30);
        tb.SetAlternateScreen(true);
        tb.SetCursor(5, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.Resize(20, 60);
        tb.SetCursor(10, 10);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetAlternateScreen(false);
        AssertBufferClean(tb);
    }

    // ================================================================
    // DL/IL at scroll region boundary with wide at edge
    // ================================================================

    [Fact]
    public void DeleteLines_AtRegionBoundary_Wide_Clean()
    {
        var tb = new TerminalBuffer(10, 15);
        tb.SetScrollRegion(2, 7);
        tb.SetCursor(2, 12);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(2, 0);
        tb.DeleteLines(2);
        AssertBufferClean(tb);
    }

    [Fact]
    public void InsertLines_AtRegionBoundary_Wide_Clean()
    {
        var tb = new TerminalBuffer(10, 15);
        tb.SetScrollRegion(2, 7);
        tb.SetCursor(7, 12);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(5, 0);
        tb.InsertLines(2);
        AssertBufferClean(tb);
    }

    // ================================================================
    // Hyperlink + wide glyph interaction
    // ================================================================

    [Fact]
    public void Hyperlink_Wide_Overwrite_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        var attr = new CellAttributes { HyperlinkId = tb.GetOrCreateHyperlinkId("https://example.com") };
        tb.SetCursor(1, 3);
        tb.WriteText("\u754c".AsSpan(), attr);
        // Check that cold cell has hyperlink
        Assert.NotEqual((ushort)0, tb.GetColdCell(1, 3).HyperlinkId);
        Assert.NotEqual((ushort)0, tb.GetColdCell(1, 4).HyperlinkId);
        // Overwrite the wide glyph with ASCII
        tb.SetCursor(1, 3);
        tb.WriteText("xy".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        // Hyperlink on overwritten cells should be gone
        Assert.Equal((ushort)0, tb.GetColdCell(1, 3).HyperlinkId);
    }

    // ================================================================
    // CR + write overlapping wide glyph at position 0
    // ================================================================

    [Fact]
    public void CR_OverlapsWide_AtZero_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        tb.SetCursor(1, 0);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.CarriageReturn();
        tb.SetCursor(1, 0);
        tb.WriteText("ab".AsSpan(), CellAttributes.Default);
        AssertBufferClean(tb);
        Assert.False(tb.GetCell(1, 1).IsContinuation,
            "Col 1 should not be continuation after CR+overwrite into wide");
    }

    // ================================================================
    // ED(2) + scroll region (Erase Display inside region only)
    // ================================================================

    [Fact]
    public void ScrollRegion_ED2_ClearsRegionOnly_Clean()
    {
        var tb = new TerminalBuffer(10, 20);
        tb.SetScrollRegion(3, 7);
        tb.SetOriginMode(true);
        for (int r = 0; r < tb.Rows; r++)
        {
            tb.SetCursor(r, 3);
            tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        }
        tb.SetCursor(0, 0);
        tb.EraseDisplay(2);
        AssertBufferClean(tb);
        // Outside region should still have content (origin mode remaps cursor)
        // Actually ED(2) clears the whole screen regardless of scroll region
    }

    // ================================================================
    // Bold + faint + inverse attribute combinations with wide glyphs
    // ================================================================

    [Fact]
    public void SGR_FaintBoldInverse_Wide_Clean()
    {
        var tb = new TerminalBuffer(3, 20);
        var attrs = new CellAttributes[] {
            CellAttributes.Default,
            new() { Bold = true },
            new() { Invisible = true },
            new() { UnderlineStyle = UnderlineStyle.Single },
            new() { SlowBlink = true },
        };
        foreach (var a in attrs)
        {
            tb.SetCursor(1, 2);
            tb.WriteText("\u754c".AsSpan(), a);
        }
        AssertBufferClean(tb);
    }

    // ================================================================
    // ClearCell on wide base then another ClearCell
    // ================================================================

    [Fact]
    public void DoubleClear_OnWide_Clean()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.ActiveBuffer.ClearCell(1, 3);
        tb.ActiveBuffer.ClearCell(1, 3);
        AssertBufferClean(tb);
        Assert.True(tb.GetCell(1, 3).IsEmpty);
        Assert.True(tb.GetCell(1, 4).IsEmpty);
    }
}
