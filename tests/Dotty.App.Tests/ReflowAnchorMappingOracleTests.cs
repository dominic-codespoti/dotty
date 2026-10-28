using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Differential tests of the reflow anchor resolution against the frozen dense oracle
/// (<see cref="LegacyDenseReflowMapping"/> / <see cref="LegacyDenseReflowReplay"/>) and of the lazy anchor location
/// against the frozen unit table (<see cref="LegacyUnitAnchorOracle"/>), plan 08 WI-R1 / WI-R2.
///
/// The production reflow no longer keeps a per-cell position table: callers register the anchors they need with
/// <c>ReflowMapping.AddAnchor</c> before <c>Screen.ReflowWithOptions</c>, which resolves exactly those, and read them back
/// with <c>TryMap(slot)</c>. The rewrite must resolve every anchor exactly as the dense table did, including the probing
/// fallback for offsets whose output row was trimmed out of the retained window. <see cref="CurrentMapping"/> is the one
/// adaptation point to the production API; everything else compares against the legacy side.
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

    /// <summary>The production mapping under test; the single adaptation point to the anchor-only API.</summary>
    private static class CurrentMapping
    {
        /// <summary>
        /// Registers <paramref name="anchors"/> (slot i is anchors[i]) on a fresh production mapping and reflows
        /// <paramref name="layout"/> of <paramref name="source"/>; the destination is discarded.
        /// </summary>
        internal static ReflowMapping Resolve(
            Screen source,
            Screen.SourceLayout layout,
            IReadOnlyList<ReflowCursorAnchor> anchors,
            int rows,
            int columns,
            int scrollbackRows,
            bool includeScrollback,
            int destinationScrollbackCapacity)
        {
            var mapping = new ReflowMapping();
            mapping.BeginAnchors(anchors.Count);
            for (int i = 0; i < anchors.Count; i++)
                Assert.Equal(i, mapping.AddAnchor(anchors[i]));

            using Screen destination = source.ReflowWithOptions(
                rows,
                columns,
                layout,
                new Screen.ReflowWorkspace(),
                mapping,
                scrollbackRows,
                includeScrollback,
                destinationScrollbackCapacity);
            return mapping;
        }

        internal static bool TryMap(ReflowMapping mapping, int slot, out ReflowPosition position) =>
            mapping.TryMap(slot, out position);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomHandBuiltLayoutsMapIdenticallyToLegacyMapper(int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < 4; round++)
        {
            int sourceColumns = random.Next(1, 10);
            int capacity = random.Next(0, 8);
            int destinationCapacity = random.Next(0, 8); // differs from the source's under a byte cap
            int rows = random.Next(1, 6);
            int columns = random.Next(1, 9);
            int lineCount = random.Next(1, 14);
            int scrollbackRows = random.Next(-1, capacity + 1); // -1: the whole capacity
            bool includeScrollback = random.Next(4) != 0;

            // Logical lines of random length (some without cells) and random one/two column units.
            var layout = new Screen.SourceLayout();
            var lengths = new int[lineCount];
            for (int line = 0; line < lineCount; line++)
            {
                var logicalLine = layout.AddLine();
                int cells = random.Next(5) == 0 ? 0 : random.Next(1, 20);
                for (int cell = 0; cell < cells; cell++)
                {
                    int width = random.Next(4) == 0 ? 2 : 1;
                    logicalLine.Cells.Add(new ReflowCell(new CellHot { Rune = 'x' }, default, width));
                }

                lengths[line] = cells == 0 ? sourceColumns : cells;
            }

            var anchors = new List<ReflowCursorAnchor>();
            for (int line = -1; line <= lineCount; line++)
            {
                int maxOffset = line >= 0 && line < lineCount ? lengths[line] + 4 : 4;
                for (int offset = -2; offset <= maxOffset; offset++)
                {
                    anchors.Add(new ReflowCursorAnchor(line, offset, false));
                    anchors.Add(new ReflowCursorAnchor(line, offset, true));
                }
            }

            using var source = new Screen(1, sourceColumns, capacity);
            var legacy = LegacyDenseReflowReplay.Replay(
                layout, sourceColumns, capacity, destinationCapacity, rows, columns, scrollbackRows, includeScrollback);
            var current = CurrentMapping.Resolve(
                source, layout, anchors, rows, columns, scrollbackRows, includeScrollback, destinationCapacity);

            string context = $"seed {seed} round {round} -> {rows}x{columns} capacity {capacity} -> {destinationCapacity} scrollbackRows {scrollbackRows} include {includeScrollback}";
            AssertWindowMatches(legacy, current, context);
            for (int slot = 0; slot < anchors.Count; slot++)
                AssertSame(legacy, current, slot, anchors[slot], context);
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
                    int destinationCapacity = random.Next(4) switch
                    {
                        0 => 0,
                        1 => source.ScrollbackCapacity / 2,
                        2 => source.ScrollbackCapacity + 7,
                        _ => source.ScrollbackCapacity,
                    };
                    var legacy = LegacyDenseReflowReplay.Replay(
                        layout, source.Columns, source.ScrollbackCapacity, destinationCapacity, rows, columns, scrollbackRows, includeScrollback);

                    var anchors = new List<ReflowCursorAnchor>();
                    foreach (var line in layout.Lines)
                    {
                        int length = line.Cells.Count == 0 ? source.Columns : line.Cells.Count;
                        for (int offset = 0; offset <= length + 1; offset++)
                        {
                            anchors.Add(new ReflowCursorAnchor(line.Identity, offset, false));
                            anchors.Add(new ReflowCursorAnchor(line.Identity, offset, true));
                        }
                    }

                    // Anchors outside the table must agree too (unknown logical lines).
                    anchors.Add(new ReflowCursorAnchor(-1, 0));
                    anchors.Add(new ReflowCursorAnchor(layout.Lines.Count, 0));

                    var current = CurrentMapping.Resolve(
                        source, layout, anchors, rows, columns, scrollbackRows, includeScrollback, destinationCapacity);

                    string context = $"seed {seed} {kind} -> {rows}x{columns}";
                    AssertWindowMatches(legacy, current, context);
                    for (int slot = 0; slot < anchors.Count; slot++)
                        AssertSame(legacy, current, slot, anchors[slot], context);
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void LazyAnchorLocationMatchesLegacyUnitTable(int seed)
    {
        foreach (ReflowScenarioKind kind in Enum.GetValues<ReflowScenarioKind>())
        {
            var buffer = ReflowScenario.Build(seed, kind).Buffer;
            try
            {
                Screen screen = buffer.ActiveBuffer;
                int scrollbackRows = buffer.IsAlternateScreenActive ? 0 : buffer.ScrollbackCount;
                var layout = screen.BuildSourceLayout(scrollbackRows, new Screen.SourceLayout());
                var legacy = LegacyUnitAnchorOracle.Build(screen, scrollbackRows);

                // One row past each end and columns outside the screen exercise the clamps.
                for (int logicalRow = -scrollbackRows - 1; logicalRow <= screen.Rows; logicalRow++)
                {
                    for (int column = -1; column <= screen.Columns; column++)
                    {
                        foreach (bool wrapPending in new[] { false, true })
                        {
                            var expected = legacy.Anchor(logicalRow, column, wrapPending);
                            var actual = screen.GetReflowAnchor(logicalRow, column, wrapPending, scrollbackRows, layout);
                            Assert.True(
                                expected == actual,
                                $"seed {seed} {kind}: row {logicalRow} column {column} wrapPending {wrapPending}: {actual}, legacy {expected}");
                        }
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }

    private static void AssertWindowMatches(LegacyDenseReflowMapping legacy, ReflowMapping current, string context) =>
        Assert.True(
            current.RetainedStart == legacy.RetainedStart && current.NewScrollbackRows == legacy.NewScrollbackRows,
            $"{context}: window start/scrollback {current.RetainedStart}/{current.NewScrollbackRows}, " +
            $"legacy {legacy.RetainedStart}/{legacy.NewScrollbackRows}");

    private static void AssertSame(
        LegacyDenseReflowMapping legacy,
        ReflowMapping current,
        int slot,
        ReflowCursorAnchor anchor,
        string context)
    {
        bool legacyMapped = legacy.TryMap(anchor, out var expected);
        bool currentMapped = CurrentMapping.TryMap(current, slot, out var actual);
        string description = $"{context}: anchor line={anchor.LogicalLine} offset={anchor.CellOffset} wrapPending={anchor.WrapPending}";
        Assert.True(legacyMapped == currentMapped, $"{description}: mapped={currentMapped}, legacy mapped={legacyMapped}");
        if (legacyMapped)
            Assert.True(expected == actual, $"{description}: {actual}, legacy {expected}");
    }
}
