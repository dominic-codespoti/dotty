using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public class NeovimCaptureReplay
{
    // ============================================================
    // Replay harness
    // ============================================================

    private static byte[] LoadCaptureBytes()
    {
        var all = File.ReadAllBytes("nvim_capture.typescript");
        int headerEnd = -1;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == 0x0A) { headerEnd = i + 1; break; }
        }
        if (headerEnd <= 0 || headerEnd >= all.Length)
            return Array.Empty<byte>();
        var result = new byte[all.Length - headerEnd];
        Buffer.BlockCopy(all, headerEnd, result, 0, result.Length);
        return result;
    }

    private static (BasicAnsiParser, TerminalAdapter, TerminalBuffer) Pipeline()
    {
        var p = new BasicAnsiParser();
        var a = new TerminalAdapter(40, 120);
        p.Handler = a;
        return (p, a, a.Buffer);
    }

    private static void Feed(BasicAnsiParser p, byte[] data, int? limit = null)
    {
        var span = limit.HasValue && limit.Value < data.Length
            ? data.AsSpan(0, limit.Value) : data.AsSpan();
        p.Feed(span);
    }

    // ============================================================
    // Invariant: no orphaned continuations or broken wide glyphs
    // ============================================================

    private static void AssertBufferClean(TerminalBuffer tb)
    {
        var violations = tb.ValidateInvariants();
        Assert.True(violations.Count == 0,
            "Buffer invariants violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    // ============================================================
    // Capture replay: full replay
    // ============================================================

    [Fact]
    public void FullCapture_Replay_Clean()
    {
        var raw = LoadCaptureBytes();
        var (p, _, b) = Pipeline();
        Feed(p, raw);
        AssertBufferClean(b);
    }

    // ============================================================
    // Capture replay: startup burst (bytes 0–20000)
    // Contains all 116 CSI K calls and initial file rendering
    // ============================================================

    [Fact]
    public void StartupSegment_Clean()
    {
        var raw = LoadCaptureBytes();
        var (p, _, b) = Pipeline();
        Feed(p, raw, 20_000);
        AssertBufferClean(b);
    }

    // ============================================================
    // Capture replay: steady-state CUP-only phase (20K–85K)
    // Thousands of CUP sequences for statusline & cursor updates
    // ============================================================

    [Fact]
    public void CUPHeavySegment_Clean()
    {
        var raw = LoadCaptureBytes();
        var (p, _, b) = Pipeline();
        Feed(p, raw, 85_000);
        AssertBufferClean(b);
    }

    // ============================================================
    // Capture replay: bottom-of-file jump (85K–120K)
    // After "G" jumps to end of file, heavy redraw with CUP bursts
    // ============================================================

    [Fact]
    public void JumpToBottomSegment_Clean()
    {
        var raw = LoadCaptureBytes();
        var (p1, _, b) = Pipeline();
        // Fast-forward to 85K
        Feed(p1, raw, 85_000);
        // Feed the jump segment
        int jumpLen = Math.Min(120_000, raw.Length) - 85_000;
        Feed(p1, raw, 85_000 + jumpLen);
        AssertBufferClean(b);
    }

    // ============================================================
    // Capture replay: chunked streaming (every 4KB boundary)
    // Ensures parser leftover handling doesn't corrupt state
    // ============================================================

    [Fact]
    public void Streaming_4KB_Chunks_Clean()
    {
        var raw = LoadCaptureBytes();
        var (p, _, b) = Pipeline();
        int chunk = 4096;
        for (int off = 0; off < raw.Length; off += chunk)
        {
            int take = Math.Min(chunk, raw.Length - off);
            p.Feed(raw.AsSpan(off, take));
            AssertBufferClean(b);
        }
    }

    // ============================================================
    // Synthetic: patterns under-represented in this capture
    // ============================================================

    /// <summary>
    /// CUP positions cursor onto a continuation cell, then EL erases the
    /// line. The old continuation cell must be cleaned up without
    /// leaving the base wide glyph orphaned.
    /// </summary>
    [Fact]
    public void CUP_Into_Continuation_Then_EL_Clean()
    {
        var tb = new TerminalBuffer(5, 20);
        // Write a wide glyph at row 2 col 3
        tb.SetCursor(2, 3);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        // CUP into the continuation cell (col 4)
        tb.SetCursor(2, 4);
        // EL (default mode) erases from cursor to end of line
        tb.EraseLine(0);
        AssertBufferClean(tb);
        // The base at col 3 should also be gone
        var baseCell = tb.GetCell(2, 3);
        Assert.True(baseCell.IsEmpty,
            "Base wide glyph should be erased when cursor targets its continuation");
    }

    /// <summary>
    /// EL (mode 2) on a row with a wide glyph completely clears the row
    /// including base and continuation cells.
    /// </summary>
    [Fact]
    public void EraseLineFull_ClearsWideGlyph()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 2);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 2);
        tb.EraseLine(2);
        for (int c = 0; c < tb.Columns; c++)
            Assert.True(tb.GetCell(1, c).IsEmpty,
                $"Cell (1,{c}) should be empty after erase display (mode 2)");
    }

    /// <summary>
    /// ECH (erase characters) starting in the middle of a wide glyph
    /// must clear both the base and continuation.
    /// </summary>
    [Fact]
    public void EraseCharacters_IntoWide_ClearsBase()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 2);
        tb.WriteText("ab\u754cd".AsSpan(), CellAttributes.Default);
        // After write: (1,2)='a', (1,3)='b', (1,4)=界base, (1,5)=continuation, (1,6)='d'
        // Set cursor to the base cell of the wide glyph
        tb.SetCursor(1, 4);
        tb.EraseCharacters(2);
        AssertBufferClean(tb);
        // The base at (1,4) should be empty since ECH covered it
        Assert.True(tb.GetCell(1, 4).IsEmpty,
            "Base wide glyph should be erased when ECH targets its base");
    }

    /// <summary>
    /// Bare CR then write then EL — the write overwrites some cells,
    /// then EL(0) clears from cursor to end of line, which used to
    /// cause stale continuations.
    /// </summary>
    [Fact]
    public void BareCR_Write_EL_NoOrphans()
    {
        var tb = new TerminalBuffer(3, 20);
        // Write a wide glyph, then CR, write narrower text, then EL
        tb.SetCursor(1, 5);
        tb.WriteText("\u754c".AsSpan(), CellAttributes.Default);
        tb.CarriageReturn();
        tb.SetCursor(1, 2);
        tb.WriteText("ab".AsSpan(), CellAttributes.Default);
        tb.SetCursor(1, 4);
        tb.EraseLine(0);
        AssertBufferClean(tb);
    }

    /// <summary>
    /// Rapid CUP + EL bursts (simulating Neovim statusline redraw).
    /// Each burst positions at a row, erases, and writes text.
    /// </summary>
    [Fact]
    public void CUP_EL_Burst_Clean()
    {
        var tb = new TerminalBuffer(10, 40);
        for (int burst = 0; burst < 100; burst++)
        {
            for (int r = 0; r < 10; r++)
            {
                tb.SetCursor(r, 0);
                tb.EraseLine(0);
                tb.SetCursor(r, 5);
                tb.WriteText($"line {burst}.{r}".AsSpan(), CellAttributes.Default);
            }
        }
        AssertBufferClean(tb);
    }

    /// <summary>
    /// Scroll region (DECSTBM) with EL at region edges. This is the
    /// classic Neovim pattern for scrolling the editing area while
    /// keeping the statusline intact.
    /// </summary>
    [Fact]
    public void ScrollRegion_EL_AtEdges_Clean()
    {
        var tb = new TerminalBuffer(30, 80);
        tb.SetScrollRegion(2, 28);
        tb.SetOriginMode(true);

        for (int cycle = 0; cycle < 50; cycle++)
        {
            for (int r = 0; r < 26; r++)
            {
                tb.SetCursor(r, 0);
                tb.EraseLine(2);
                tb.SetCursor(r, 3);
                tb.WriteText($"content {cycle}.{r} \u754c".AsSpan(), CellAttributes.Default);
            }
            // Scroll the region (simulates LF at bottom of region)
            tb.SetCursor(25, 0);
            tb.LineFeed();
        }

        AssertBufferClean(tb);
    }

    /// <summary>
    /// Bare CR sequences without writes — the old bug was that a CR
    /// followed by text would clear the line. Verify CR is a no-op
    /// on cell content.
    /// </summary>
    [Fact]
    public void BareCR_Only_KeepsContent()
    {
        var tb = new TerminalBuffer(3, 10);
        tb.SetCursor(1, 0);
        tb.WriteText("Hello".AsSpan(), CellAttributes.Default);
        tb.CarriageReturn();
        // Content should remain
        var text = tb.GetRowText(1);
        Assert.StartsWith("Hello", text);
    }
}
