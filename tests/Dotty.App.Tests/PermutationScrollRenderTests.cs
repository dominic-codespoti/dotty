using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class PermutationScrollRenderTests
{
    private static void AssertNoOrphanedBases(TerminalBuffer tb)
    {
        int rows = tb.Rows;
        int cols = tb.Columns;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = tb.GetCell(r, c);
                if (cell.IsContinuation)
                {
                    Assert.True(string.IsNullOrEmpty(cell.Grapheme), $"Continuation cell at {r},{c} unexpectedly has Grapheme '{cell.Grapheme}'");
                }

                if (!cell.IsContinuation && !string.IsNullOrEmpty(cell.Grapheme))
                {
                    int width = Math.Max(1, (int)cell.Width);
                    for (int i = 1; i < width; i++)
                    {
                        if (c + i >= cols) break;
                        var cont = tb.GetCell(r, c + i);
                        Assert.True(cont.IsContinuation, $"Base at {r},{c} width={width} expects continuation at {r},{c + i}");
                    }
                }
            }
        }
    }

    private static Cell[,] GetScreenCells(TerminalBuffer tb)
    {
        // Use an internal test accessor on `TerminalBuffer` and `Screen` to
        // obtain the live cells array without reflection. This requires
        // `InternalsVisibleTo` to be set on the `Dotty.Terminal` assembly.
        return tb.ActiveScreenForTests.GetCellsForTests();
    }

    [Fact]
    public void ScrollBetweenRenders_PermutationTest()
    {
        const int rows = 60;
        const int cols = 120;

        var seedEnv = Dotty.Env.GetEnvironmentVariable("DOTTY_PERM_SEED");
        int seed = 654321;
        if (!string.IsNullOrEmpty(seedEnv) && int.TryParse(seedEnv, out var parsed)) seed = parsed;

        var itersEnv = Dotty.Env.GetEnvironmentVariable("DOTTY_PERM_ITERATIONS");
        int iterations = 200;
        if (!string.IsNullOrEmpty(itersEnv) && int.TryParse(itersEnv, out var parsedIters)) iterations = Math.Max(1, parsedIters);

        var rnd = new Random(seed);

        var allowCorruptEnv = Dotty.Env.GetEnvironmentVariable("DOTTY_PERM_ALLOW_MANUAL_CORRUPT");
        bool allowManualCorrupt = true;
        if (!string.IsNullOrEmpty(allowCorruptEnv) && (allowCorruptEnv == "0" || allowCorruptEnv.Equals("false", StringComparison.OrdinalIgnoreCase)))
            allowManualCorrupt = false;

        var glyphs = new[] { "A", "界", "e\u0301", "🙂", "#", "*" };

        for (int iter = 0; iter < iterations; iter++)
        {
            var tb = new TerminalBuffer(rows: rows, columns: cols);
            var events = new List<string>();

            // Build a simple ASCII-art block with mixed glyphs
            var artLines = new List<string>();
            int artHeight = 8;
            int artWidth = Math.Min(60, cols - 4);
            for (int r = 0; r < artHeight; r++)
            {
                var line = new char[artWidth];
                for (int c = 0; c < artWidth; c++)
                {
                    // pick a glyph; we'll occasionally insert wide glyphs by doubling
                    var g = glyphs[rnd.Next(glyphs.Length)];
                    // Use ASCII fallback for the char grid; wide glyphs will be used via WriteText
                    line[c] = g[0];
                }
                artLines.Add(new string(line));
            }

            // Choose permutations for this iteration
            bool chunkedWrites = rnd.NextDouble() < 0.5;
            bool splitGraphemes = rnd.NextDouble() < 0.4;
            int preScrolls = rnd.Next(0, 6);
            int midScrolls = rnd.Next(0, 6);
            bool toggleOrigin = rnd.NextDouble() < 0.3;
            bool toggleAlternate = rnd.NextDouble() < 0.3;

            try
            {
                // Initial render
                for (int r = 0; r < artLines.Count; r++)
                {
                    tb.SetCursor(r + 2, 2);
                    var text = artLines[r];
                    if (chunkedWrites)
                    {
                        for (int i = 0; i < text.Length; i++)
                        {
                            // randomly split graphemes by writing one char at a time; sometimes write combining separately
                            if (splitGraphemes && text[i] == 'e' && rnd.NextDouble() < 0.3)
                            {
                                tb.WriteText("e".AsSpan(), CellAttributes.Default);
                                tb.WriteText("\u0301".AsSpan(), CellAttributes.Default);
                                events.Add($"Split grapheme write at {r},{i}");
                            }
                            else
                            {
                                tb.WriteText(text.AsSpan(i, 1), CellAttributes.Default);
                            }
                        }
                    }
                    else
                    {
                        // replace some positions with explicitly wide glyphs
                        if (rnd.NextDouble() < 0.2)
                        {
                            var wide = "界";
                            tb.WriteText(wide.AsSpan(), CellAttributes.Default);
                            if (artWidth > 1) tb.WriteText(text.AsSpan(1, Math.Max(0, Math.Min(text.Length - 1, artWidth - 1))), CellAttributes.Default);
                        }
                        else
                        {
                            tb.WriteText(text.AsSpan(), CellAttributes.Default);
                        }
                    }
                }

                // Some pre-scroll activity
                for (int s = 0; s < preScrolls; s++)
                {
                    if (rnd.NextDouble() < 0.5)
                    {
                        tb.LineFeed();
                        events.Add("LineFeed");
                    }
                    else
                    {
                        // set a small scroll region and scroll inside it
                        int top = rnd.Next(1, rows - 3);
                        int bottom = Math.Min(rows - 1, top + rnd.Next(1, 6));
                        tb.SetScrollRegion(top, bottom);
                        // write to bottom line to cause scroll
                        tb.SetCursor(bottom, 0);
                        tb.WriteText("SCROLL".AsSpan(), CellAttributes.Default);
                        events.Add($"ScrollRegion {top};{bottom}");
                    }
                }

                // Maybe toggle modes
                if (toggleOrigin) { tb.SetOriginMode(true); events.Add("SetOriginMode true"); }
                if (toggleAlternate) { tb.SetAlternateScreen(true); events.Add("SetAlternateScreen true"); }

                // Mid render: re-write art (simulate re-render after scrolls)
                for (int r = 0; r < artLines.Count; r++)
                {
                    tb.SetCursor(r + 2, 2);
                    var text = artLines[r];
                    // Introduce occasional erases while writing to simulate concurrent ops
                    if (rnd.NextDouble() < 0.2)
                    {
                        tb.EraseLine(2);
                        events.Add($"EraseLine {r}");
                    }

                    if (chunkedWrites && rnd.NextDouble() < 0.5)
                    {
                        // write in small chunks
                        int pos = 0;
                        while (pos < text.Length)
                        {
                            int len = rnd.Next(1, Math.Min(4, text.Length - pos + 1));
                            tb.WriteText(text.AsSpan(pos, Math.Min(len, text.Length - pos)), CellAttributes.Default);
                            pos += len;
                        }
                    }
                    else if (splitGraphemes && rnd.NextDouble() < 0.3)
                    {
                        // attempt split combining sequence
                        for (int i = 0; i < text.Length; i++)
                        {
                            if (text[i] == 'e' && rnd.NextDouble() < 0.4)
                            {
                                tb.WriteText("e".AsSpan(), CellAttributes.Default);
                                tb.WriteText("\u0301".AsSpan(), CellAttributes.Default);
                            }
                            else
                            {
                                tb.WriteText(text.AsSpan(i, 1), CellAttributes.Default);
                            }
                        }
                    }
                    else
                    {
                        tb.WriteText(text.AsSpan(), CellAttributes.Default);
                    }
                }

                // Mid-scroll activity
                for (int s = 0; s < midScrolls; s++)
                {
                    if (rnd.NextDouble() < 0.6)
                    {
                        tb.LineFeed();
                        events.Add("LineFeed");
                    }
                    else
                    {
                        // directly manipulate backing cells occasionally to simulate corruption
                        var cells = GetScreenCells(tb);
                        int rr = rnd.Next(0, rows);
                        int cc = rnd.Next(0, cols);
                        var cell = cells[rr, cc];
                        if (rnd.NextDouble() < 0.2)
                        {
                            cell.Grapheme = "界";
                            cell.Width = 2;
                            cell.IsContinuation = false;
                            cells[rr, cc] = cell;
                            // Diagnostic: record this direct insertion so it's visible in the
                            // per-iteration event dump. This helps trace uninstrumented
                            // direct mutations performed by the test harness.
                            events.Add($"InsertWide mid at {rr},{cc}");
                        }
                        else if (allowManualCorrupt)
                        {
                            cell.IsContinuation = !cell.IsContinuation;
                            if (rnd.NextDouble() < 0.4) cell.Width = 0;
                            cells[rr, cc] = cell;
                            events.Add($"ManualCorrupt mid at {rr},{cc}");
                        }
                    }
                }

                // final sanity checks
                AssertNoOrphanedBases(tb);

            }
            catch (Exception ex)
            {
                var dump = string.Join("\n", events.Count > 200 ? events.GetRange(Math.Max(0, events.Count - 200), 200) : events);
                var orphanDump = DumpOrphanedBases(tb);

                try
                {
                    var path = $"/tmp/dotty_perm_seed_{seed}_iter_{iter}.log";
                    System.IO.File.WriteAllText(path, string.Join("\n", events));
                }
                catch { /* best-effort file dump */ }

                throw new Exception($"Permutation harness failed iter={iter} seed={seed}: {ex.Message}\nRecent events:\n{dump}\nOrphaned base summary:\n{orphanDump}", ex);
            }
        }
    }

    private static string DumpOrphanedBases(TerminalBuffer tb)
    {
        var sb = new System.Text.StringBuilder();
        int rows = tb.Rows;
        int cols = tb.Columns;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = tb.GetCell(r, c);
                if (!cell.IsContinuation && !string.IsNullOrEmpty(cell.Grapheme))
                {
                    int width = Math.Max(1, (int)cell.Width);
                    bool missing = false;
                    for (int i = 1; i < width; i++)
                    {
                        if (c + i >= cols) { missing = true; break; }
                        var cont = tb.GetCell(r, c + i);
                        if (!cont.IsContinuation) { missing = true; break; }
                    }

                    if (missing)
                    {
                        sb.AppendLine($"Base at {r},{c} width={width} Grapheme='{cell.Grapheme}'");
                        // dump nearby cells [c-2 .. c+4]
                        int start = Math.Max(0, c - 2);
                        int end = Math.Min(cols - 1, c + 4);
                        for (int cc = start; cc <= end; cc++)
                        {
                            var nb = tb.GetCell(r, cc);
                            sb.AppendLine($"  [{r},{cc}] G='{nb.Grapheme ?? ""}' W={nb.Width} cont={nb.IsContinuation}");
                        }
                    }
                }
            }
        }

        if (sb.Length == 0) sb.AppendLine("(no orphaned bases found)");
        return sb.ToString();
    }
}
