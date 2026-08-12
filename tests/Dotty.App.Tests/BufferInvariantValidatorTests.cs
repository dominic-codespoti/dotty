using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

/// <summary>
/// Tests for <see cref="TerminalBuffer.ValidateInvariants"/> — the library-owned
/// buffer invariant checker that replaced the duplicated test-file scanners.
/// Also locks in the two copy-path bugs the validator exposed: region scrolls
/// and IL/DL must transfer RowColdFlags (and RowMaxCol) with the moved content.
/// </summary>
public class BufferInvariantValidatorTests
{
    private static void AssertClean(TerminalBuffer tb)
    {
        var violations = tb.ValidateInvariants();
        Assert.True(violations.Count == 0,
            "Buffer invariants violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void CleanBuffer_HasNoViolations()
    {
        var tb = new TerminalBuffer(rows: 5, columns: 20);
        AssertClean(tb);
    }

    [Fact]
    public void EmojiWriteThenAsciiOverwrite_StaysClean()
    {
        // The original ghost repro: a wide grapheme replaced by an ASCII run
        // must not leave stale cold metadata behind.
        var tb = new TerminalBuffer(rows: 5, columns: 40);
        tb.SetCursor(0, 0);
        tb.WriteText("abc❤️def".AsSpan(), CellAttributes.Default);
        tb.SetCursor(0, 0);
        tb.WriteText("abc123def".AsSpan(), CellAttributes.Default);
        AssertClean(tb);
        Assert.StartsWith("abc123def", tb.GetRowText(0));
    }

    [Fact]
    public void ContinuationCarryingRune_IsFlagged()
    {
        var tb = new TerminalBuffer(rows: 1, columns: 10);
        tb.SetCursor(0, 0);
        tb.WriteText("界".AsSpan(), CellAttributes.Default);
        var screen = tb.ActiveScreenForTests;
        screen.GetCellRef(0, 1).Rune = 65; // corrupt the continuation

        var violations = tb.ValidateInvariants();
        Assert.Contains(violations, v => v.Contains("continuation carries Rune"));
    }

    [Fact]
    public void MissingContinuation_IsFlagged()
    {
        var tb = new TerminalBuffer(rows: 1, columns: 10);
        tb.SetCursor(0, 0);
        tb.WriteText("界".AsSpan(), CellAttributes.Default);
        tb.ActiveScreenForTests.GetCellRef(0, 1).Reset(); // drop the continuation

        var violations = tb.ValidateInvariants();
        Assert.Contains(violations, v => v.Contains("missing continuation"));
    }

    [Fact]
    public void ColdMetadataWithoutRowFlag_IsFlagged()
    {
        var tb = new TerminalBuffer(rows: 1, columns: 10);
        tb.SetCursor(0, 0);
        tb.WriteText("❤️".AsSpan(), CellAttributes.Default); // multi-codepoint -> stored grapheme
        var screen = tb.ActiveScreenForTests;
        screen.RowColdFlags[screen.GetPhysicalRow(0)] = false; // corrupt the flag

        var violations = tb.ValidateInvariants();
        Assert.Contains(violations, v => v.Contains("RowColdFlags is false"));
    }

    [Fact]
    public void RowMaxColBelowContent_IsFlagged()
    {
        var tb = new TerminalBuffer(rows: 1, columns: 20);
        tb.SetCursor(0, 0);
        tb.WriteText("abcdefghij".AsSpan(), CellAttributes.Default);
        var screen = tb.ActiveScreenForTests;
        screen.RowMaxCol[screen.GetPhysicalRow(0)] = 2; // corrupt (stale-low)

        var violations = tb.ValidateInvariants();
        Assert.Contains(violations, v => v.Contains("below content max"));
    }

    // ================================================================
    // Copy-path regressions: content moved between physical rows must
    // carry its RowColdFlags (and RowMaxCol) with it.
    // ================================================================

    [Fact]
    public void RegionScroll_MovesColdFlagWithContent()
    {
        var tb = new TerminalBuffer(rows: 5, columns: 20);
        tb.SetScrollRegion(1, 4); // rows 0..3
        tb.SetCursor(2, 0);
        tb.WriteText("ab❤️".AsSpan(), CellAttributes.Default);
        tb.ScrollUpLines(1); // the grapheme row moves up to row 1

        // Row 1 now holds content with grapheme metadata; its RowColdFlags
        // must have traveled with it or the next ASCII overwrite ghosts.
        AssertClean(tb);
    }

    [Fact]
    public void RegionScrollThenAsciiOverwrite_NoGhost()
    {
        var tb = new TerminalBuffer(rows: 5, columns: 20);
        tb.SetScrollRegion(1, 4);
        tb.SetCursor(2, 0);
        tb.WriteText("ab❤️".AsSpan(), CellAttributes.Default);
        tb.ScrollUpLines(1);

        // Overwrite the moved row, covering the wide glyph's columns.
        tb.SetCursor(1, 0);
        tb.WriteText("xyzwq".AsSpan(), CellAttributes.Default);

        AssertClean(tb);
        Assert.StartsWith("xyzwq", tb.GetRowText(1));
    }

    [Fact]
    public void InsertLines_MovesMaxColAndColdFlagWithContent()
    {
        var tb = new TerminalBuffer(rows: 6, columns: 20);
        tb.SetCursor(2, 0);
        tb.WriteText("ab界cdefghij".AsSpan(), CellAttributes.Default);

        tb.SetCursor(1, 0);
        tb.InsertLines(1); // row 2's long content shifts to row 3

        AssertClean(tb);
        // GetRowText renders the width-2 glyph's continuation cell as a space.
        Assert.StartsWith("ab界 cdefghij", tb.GetRowText(3));
    }

    [Fact]
    public void DeleteLines_MovesMaxColAndColdFlagWithContent()
    {
        var tb = new TerminalBuffer(rows: 6, columns: 20);
        tb.SetCursor(3, 0);
        tb.WriteText("ab界cdefghij".AsSpan(), CellAttributes.Default);

        tb.SetCursor(1, 0);
        tb.DeleteLines(1); // row 3's long content shifts up to row 2

        AssertClean(tb);
        // GetRowText renders the width-2 glyph's continuation cell as a space.
        Assert.StartsWith("ab界 cdefghij", tb.GetRowText(2));
    }
}
