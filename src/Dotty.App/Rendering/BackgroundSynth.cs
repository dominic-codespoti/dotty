using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.App.Rendering
{
    public struct SynthCell
    {
        public bool IsContinuation;
        public int Width;
        public bool HasBg;
        public SKColor Bg;
        public bool IsSeparatorGlyph;
    }

    public readonly record struct RowSpan(int X0, int X1, SKColor Color);

    public readonly record struct RegionSimple(int X0, int X1, int TopRow, int BottomRow, SKColor Color);

    public readonly record struct RegionKeySimple(int X0, int X1, SKColor Color);

    public sealed class ActiveRegionSimple
    {
        public int X0, X1;
        public int TopRow, BottomRow;
        public SKColor Color;
    }

    public static class BackgroundSynth
    {
        // Build row spans from a sequence of SynthCell. This mirrors the
        // behaviour used by the compositor but is pure and testable.
        public static List<RowSpan> BuildRowSpans(SynthCell[] cells)
        {
            var spans = new List<RowSpan>();

            bool inSpan = false;
            int spanStart = 0;
            int spanEnd = 0;
            SKColor spanColor = default;

            bool pendingPrefix = false;
            int prefixStart = 0;
            int prefixEnd = 0;

            void Flush()
            {
                if (!inSpan) return;
                spans.Add(new RowSpan(spanStart, spanEnd, spanColor));
                inSpan = false;
            }

            for (int col = 0; col < cells.Length; col++)
            {
                var cell = cells[col];

                if (cell.IsContinuation)
                {
                    if (inSpan) spanEnd = Math.Max(spanEnd, col + 1);
                    continue;
                }

                int width = Math.Max(1, cell.Width);

                if (!cell.HasBg && cell.IsSeparatorGlyph)
                {
                    if (inSpan)
                    {
                        spanEnd = Math.Max(spanEnd, col + width);
                    }
                    else
                    {
                        if (!pendingPrefix)
                        {
                            pendingPrefix = true;
                            prefixStart = col;
                            prefixEnd = col + width;
                        }
                        else if (col == prefixEnd)
                        {
                            prefixEnd = col + width;
                        }
                        else
                        {
                            prefixStart = col;
                            prefixEnd = col + width;
                        }
                    }

                    col = (col + width) - 1;
                    continue;
                }

                if (!cell.HasBg)
                {
                    pendingPrefix = false;
                    Flush();
                    continue;
                }

                int x0 = col;
                int x1 = Math.Min(cells.Length, col + width);

                if (!inSpan)
                {
                    inSpan = true;
                    spanStart = (pendingPrefix && prefixEnd == x0) ? prefixStart : x0;
                    pendingPrefix = false;
                    spanEnd = x1;
                    spanColor = cell.Bg;
                }
                else if (cell.Bg.Equals(spanColor) && x0 == spanEnd)
                {
                    spanEnd = x1;
                }
                else
                {
                    Flush();
                    inSpan = true;
                    spanStart = (pendingPrefix && prefixEnd == x0) ? prefixStart : x0;
                    pendingPrefix = false;
                    spanEnd = x1;
                    spanColor = cell.Bg;
                }

                col = x1 - 1;
            }

            Flush();
            return spans;
        }

        // MergeRowSpans: logic-only equivalent of the compositor's merge.
        public static void MergeRowSpans(int row, List<RowSpan> spans, Dictionary<RegionKeySimple, ActiveRegionSimple> activeRegions, List<RegionSimple> outRegions)
        {
            var touched = new HashSet<RegionKeySimple>();

            foreach (var span in spans)
            {
                var key = new RegionKeySimple(span.X0, span.X1, span.Color);

                if (activeRegions.TryGetValue(key, out var region))
                {
                    region.BottomRow = row + 1;
                }
                else
                {
                    region = new ActiveRegionSimple
                    {
                        X0 = span.X0,
                        X1 = span.X1,
                        TopRow = row,
                        BottomRow = row + 1,
                        Color = span.Color
                    };
                    activeRegions[key] = region;
                }

                touched.Add(key);
            }

            var toRemove = new List<RegionKeySimple>();
            foreach (var kvp in activeRegions)
            {
                if (touched.Contains(kvp.Key)) continue;

                var r = kvp.Value;
                outRegions.Add(new RegionSimple(r.X0, r.X1, r.TopRow, r.BottomRow, r.Color));
                toRemove.Add(kvp.Key);
            }

            foreach (var k in toRemove)
                activeRegions.Remove(k);
        }
    }
}
