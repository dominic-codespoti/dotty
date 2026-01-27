using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class StressFuzzReproTests
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
                    // continuation cells should not carry a grapheme
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
        // Use internal test accessor to avoid reflection and trimming warnings.
        return tb.ActiveScreenForTests.GetCellsForTests();
    }

    [Fact]
    public void DeterministicFuzz_DoesNotProduceOrphanedBases()
    {
        int rows = 60;
        int cols = 120;
        var tb = new TerminalBuffer(rows: rows, columns: cols);

        // Allow overriding seed and iteration count from the environment to enable
        // broader, deterministic fuzz runs from CI or the terminal.
        var seedEnv = Dotty.Env.GetEnvironmentVariable("DOTTY_FUZZ_SEED");
        int seed = 123456;
        if (!string.IsNullOrEmpty(seedEnv) && int.TryParse(seedEnv, out var parsed)) seed = parsed;

        var itersEnv = Dotty.Env.GetEnvironmentVariable("DOTTY_FUZZ_ITERATIONS");
        int iterations = 5000;
        if (!string.IsNullOrEmpty(itersEnv) && int.TryParse(itersEnv, out var parsedIters)) iterations = Math.Max(1, parsedIters);

        var rnd = new Random(seed);
        var events = new List<string>();

        var glyphs = new[] { "A", "界", "e\u0301", "🙂" };

        // Run many deterministic random ops to stress the buffer model
        for (int it = 0; it < iterations; it++)
        {
            int op = rnd.Next(0, 9);
            int r = rnd.Next(0, rows);
            int c = rnd.Next(0, cols);

            try
            {
                switch (op)
                {
                    case 0:
                        tb.SetCursor(r, c);
                        events.Add($"SetCursor {r},{c}");
                        break;
                    case 1:
                        var text = glyphs[rnd.Next(glyphs.Length)];
                        tb.SetCursor(r, c);
                        tb.WriteText(text.AsSpan(), CellAttributes.Default);
                        events.Add($"WriteText '{text}' at {r},{c}");
                        break;
                    case 2:
                        tb.SetCursor(r, 0);
                        tb.EraseLine(rnd.Next(0, 3));
                        events.Add($"EraseLine row={r} mode={rnd.Next(0,3)}");
                        break;
                    case 3:
                        tb.SetCursor(r, c);
                        tb.EraseDisplay(rnd.Next(0, 3));
                        events.Add($"EraseDisplay at {r} mode={rnd.Next(0,3)}");
                        break;
                    case 4:
                        tb.LineFeed();
                        events.Add("LineFeed");
                        break;
                    case 5:
                        // Set scroll region - include resets
                        int top = rnd.Next(0, rows);
                        int bottom = Math.Max(top + 1, rnd.Next(top + 1, Math.Min(rows, top + 10)));
                        tb.SetScrollRegion(top + 1, bottom + 1); // API expects 1-based args
                        events.Add($"SetScrollRegion {top + 1};{bottom + 1}");
                        break;
                    case 6:
                        tb.SetOriginMode(rnd.Next(0, 2) == 0 ? false : true);
                        events.Add($"SetOriginMode {rnd.Next(0,2)==0}");
                        break;
                    case 7:
                        tb.SetAlternateScreen(rnd.Next(0, 2) == 0 ? false : true);
                        events.Add($"SetAlternateScreen {rnd.Next(0,2)==0}");
                        break;
                    case 8:
                        // Manual corruption: flip continuation flags or widths in the backing cells
                        var cells = GetScreenCells(tb);
                        int rr = rnd.Next(0, rows);
                        int cc = rnd.Next(0, cols);
                        var cell = cells[rr, cc];
                        // If empty, create a base wide glyph sometimes
                        if (cell.IsEmpty && rnd.NextDouble() < 0.3)
                        {
                            cell.Grapheme = "界";
                            cell.Width = 2;
                            cell.IsContinuation = false;
                            cells[rr, cc] = cell;
                            // mark continuation cell randomly
                            if (cc + 1 < cols)
                            {
                                var cont = cells[rr, cc + 1];
                                cont.Grapheme = null;
                                cont.IsContinuation = true;
                                cont.Width = 0;
                                cells[rr, cc + 1] = cont;
                            }
                            events.Add($"ManualCorrupt insert wide base at {rr},{cc}");
                        }
                        else
                        {
                            // Flip a continuation flag (simulate lost marker)
                            cell.IsContinuation = !cell.IsContinuation;
                            // Sometimes zero-out Width to simulate a partial write
                            if (rnd.NextDouble() < 0.4) cell.Width = 0;
                            cells[rr, cc] = cell;
                            events.Add($"ManualCorrupt flip at {rr},{cc} cont={cell.IsContinuation} w={cell.Width}");
                        }
                        break;
                }

                // Periodically validate the whole buffer invariant
                if (it % 100 == 0)
                {
                    AssertNoOrphanedBases(tb);
                }
            }
            catch (Exception ex)
            {
                // Dump recent events to help debugging
                var dump = string.Join("\n", events.Count > 100 ? events.GetRange(events.Count - 100, 100) : events);
                throw new Exception($"Fuzzer detected invariant violation at iteration {it}: {ex.Message}\nRecent events:\n{dump}", ex);
            }
        }

        // Final full invariant check
        AssertNoOrphanedBases(tb);
    }
}
