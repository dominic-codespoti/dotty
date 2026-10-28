using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Differential tests of the reflow position mapping against the frozen dense oracle
/// (<see cref="LegacyDenseReflowMapping"/> / <see cref="LegacyDenseReflowReplay"/>), plan 08 WI-R1.
///
/// Both tests pass on the current dictionary implementation because it IS the oracle's twin. Their job is to keep passing
/// when WI-R2 replaces the per-cell dictionary with anchor-only resolution: the rewrite must resolve every anchor exactly as
/// the dense table did, including the probing fallback for offsets whose output row was trimmed out of the retained window.
///
/// When WI-R2 changes <c>ReflowMapping</c>'s API (<c>AddAnchor</c> / slot lookup instead of <c>Add</c> / <c>TryMap(anchor)</c>),
/// adapt ONLY the two members of <see cref="CurrentMapping"/> below: everything else compares against the legacy side.
/// </summary>
public sealed class ReflowAnchorMappingOracleTests
{
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 1; seed <= 60; seed++)
            data.Add(seed);
        return data;
    }

    /// <summary>The production mapping under test; the single adaptation point for the anchor-only rewrite.</summary>
    private static class CurrentMapping
    {
        /// <summary>Feeds a hand-built position table into a fresh production mapping (table-level test).</summary>
        internal static ReflowMapping FromTable(
            int rows,
            int columns,
            IReadOnlyList<(int Line, int Length)> lineLengths,
            IReadOnlyList<(int Line, int Offset, ReflowPosition Position)> positions,
            int retainedStart,
            int newScrollbackRows)
        {
            var mapping = new ReflowMapping();
            mapping.Reset(rows, columns);
            foreach (var (line, length) in lineLengths)
                mapping.SetLineLength(line, length);
            foreach (var (line, offset, position) in positions)
                mapping.Add(line, offset, position);
            mapping.RetainedStart = retainedStart;
            mapping.NewScrollbackRows = newScrollbackRows;
            return mapping;
        }

        /// <summary>Production mapping produced by a real reflow of <paramref name="source"/>; the destination is discarded.</summary>
        internal static ReflowMapping FromReflow(
            Screen source,
            Screen.SourceLayout layout,
            int rows,
            int columns,
            int scrollbackRows,
            bool includeScrollback)
        {
            var mapping = new ReflowMapping();
            using Screen destination = source.ReflowWithOptions(
                rows,
                columns,
                default,
                layout,
                new Screen.ReflowWorkspace(),
                mapping,
                scrollbackRows,
                includeScrollback);
            return mapping;
        }

        internal static bool TryMap(ReflowMapping mapping, ReflowCursorAnchor anchor, out ReflowPosition position) =>
            mapping.TryMap(anchor, out position);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomPositionTablesMapIdenticallyToLegacyMapper(int seed)
    {
        var random = new Random(seed);
        int rows = random.Next(1, 6);
        int columns = random.Next(1, 9);
        int lines = random.Next(1, 12);

        var lineLengths = new List<(int Line, int Length)>();
        var positions = new List<(int Line, int Offset, ReflowPosition Position)>();
        var lengths = new int[lines];
        int outputIndex = 0;
        for (int line = 0; line < lines; line++)
        {
            int length = random.Next(0, 14);
            lengths[line] = length;
            if (random.Next(10) != 0) // now and then leave the length unregistered
                lineLengths.Add((line, length));

            int used = 0;
            for (int offset = 0; offset <= length; offset++)
            {
                if (offset > 0 && used >= columns)
                {
                    outputIndex++;
                    used = 0;
                }

                // Drop an entry now and then so the probing fallback (lower offsets) is exercised.
                bool dropped = offset < length && random.Next(8) == 0;
                if (!dropped)
                {
                    positions.Add((line, offset, new ReflowPosition(
                        -1, Math.Min(used, columns - 1), random.Next(6) == 0, false, outputIndex)));
                }

                used++;
            }

            outputIndex++;
        }

        int retainedStart = random.Next(0, Math.Max(1, outputIndex / 2));
        int newScrollbackRows = random.Next(0, Math.Max(1, outputIndex - retainedStart));

        var legacy = new LegacyDenseReflowMapping();
        legacy.Reset(rows, columns);
        foreach (var (line, length) in lineLengths)
            legacy.SetLineLength(line, length);
        foreach (var (line, offset, position) in positions)
            legacy.Add(line, offset, position);
        legacy.RetainedStart = retainedStart;
        legacy.NewScrollbackRows = newScrollbackRows;

        var current = CurrentMapping.FromTable(rows, columns, lineLengths, positions, retainedStart, newScrollbackRows);

        for (int line = -1; line <= lines; line++)
        {
            int maxOffset = line >= 0 && line < lines ? lengths[line] + 4 : 4;
            for (int offset = -2; offset <= maxOffset; offset++)
            {
                foreach (bool wrapPending in new[] { false, true })
                {
                    var anchor = new ReflowCursorAnchor(line, offset, wrapPending);
                    AssertSame(legacy, current, anchor, $"seed {seed}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RealReflowMappingMatchesLegacyReplayForEveryAnchor(int seed)
    {
        foreach (ReflowScenarioKind kind in Enum.GetValues<ReflowScenarioKind>())
        {
            var adapter = ReflowScenario.Build(seed, kind);
            var buffer = adapter.Buffer;
            try
            {
                var random = new Random(seed * 31 + 7);
                Screen source = buffer.ActiveBuffer;
                bool includeScrollback = !buffer.IsAlternateScreenActive;
                int scrollbackRows = includeScrollback ? buffer.ScrollbackCount : 0;

                for (int target = 0; target < 4; target++)
                {
                    var (rows, columns) = ReflowScenario.NextSize(random, buffer.Rows, buffer.Columns);
                    var layout = source.BuildSourceLayout(scrollbackRows, new Screen.SourceLayout());
                    var legacy = LegacyDenseReflowReplay.Replay(
                        layout, source.Columns, source.ScrollbackCapacity, rows, columns, scrollbackRows, includeScrollback);
                    var current = CurrentMapping.FromReflow(source, layout, rows, columns, scrollbackRows, includeScrollback);

                    string context = $"seed {seed} {kind} -> {rows}x{columns}";
                    Assert.True(
                        current.RetainedStart == legacy.RetainedStart && current.NewScrollbackRows == legacy.NewScrollbackRows,
                        $"{context}: window start/scrollback {current.RetainedStart}/{current.NewScrollbackRows}, " +
                        $"legacy {legacy.RetainedStart}/{legacy.NewScrollbackRows}");

                    foreach (var line in layout.Lines)
                    {
                        int length = line.Cells.Count == 0 ? source.Columns : line.Cells.Count;
                        for (int offset = 0; offset <= length + 1; offset++)
                        {
                            foreach (bool wrapPending in new[] { false, true })
                                AssertSame(legacy, current, new ReflowCursorAnchor(line.Identity, offset, wrapPending), context);
                        }
                    }

                    // Anchors outside the table must agree too (unknown logical lines).
                    AssertSame(legacy, current, new ReflowCursorAnchor(-1, 0), context);
                    AssertSame(legacy, current, new ReflowCursorAnchor(layout.Lines.Count, 0), context);
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }

    private static void AssertSame(
        LegacyDenseReflowMapping legacy,
        ReflowMapping current,
        ReflowCursorAnchor anchor,
        string context)
    {
        bool legacyMapped = legacy.TryMap(anchor, out var expected);
        bool currentMapped = CurrentMapping.TryMap(current, anchor, out var actual);
        string description = $"{context}: anchor line={anchor.LogicalLine} offset={anchor.CellOffset} wrapPending={anchor.WrapPending}";
        Assert.True(legacyMapped == currentMapped, $"{description}: mapped={currentMapped}, legacy mapped={legacyMapped}");
        if (legacyMapped)
            Assert.True(expected == actual, $"{description}: {actual}, legacy {expected}");
    }
}
