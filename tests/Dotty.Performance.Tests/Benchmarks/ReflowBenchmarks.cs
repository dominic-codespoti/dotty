using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Resize/reflow benchmarks over a populated 10,000-line scrollback (plan 08, WI-1c). The existing
/// <c>'Resize: ...'</c> benchmarks use scrollback 0 and about 500 cells, so they cannot see reflow cost.
///
/// One invoke = resize away and back (<c>OperationsPerInvoke = 2</c>, so results are per reflow). No
/// <c>[Params]</c>: the baseline key is the description. Each variant needs a different buffer state and
/// every 10k-line buffer retains tens of MB of pooled reflow memory, so each fixture is built lazily by the
/// one benchmark that uses it (BenchmarkDotNet runs every benchmark in its own process, so a process only ever
/// builds its own fixture; the first, warm-up invocation pays the build). Only public entry points
/// (<see cref="BasicAnsiParser.Feed"/> and <see cref="TerminalBuffer.Resize"/>) are used.
///
/// Set <c>DOTTY_REFLOW_BENCH_MEMORY=1</c> to print <c>GC.GetTotalMemory(true)</c> before the first resize and
/// after 20 resizes for the fixture that is built (setup-time only; not a benchmark and never part of the gate).
/// This class opts out of the no-GC region: the first resize grows pooled lists far beyond 10 MB.
/// </summary>
[BenchmarkCategory("Memory", "Reflow")]
public class ReflowBenchmarks : PerformanceTestBase
{
    private const int ScrollbackLines = 10_000;
    private const int WorkloadLines = ScrollbackLines + 24;

    private TerminalAdapter? _plain;
    private TerminalAdapter? _promptMarks;
    private TerminalAdapter? _altScreen;
    private TerminalAdapter? _wide;

    protected override bool UseNoGcRegion => false;

    #region Fixtures

    // Lines of 40..110 chars (about 40% longer than 80 columns): soft-wrapped, hard-broken and short rows mixed.
    private static TerminalAdapter BuildPlain() =>
        Populate(BenchmarkContent.WorkloadLines(WorkloadLines, minLength: 40, lengthSpan: 71), alternateScreen: false);

    // A prompt mark every 14th line: about 700 in the buffer, about 500 still inside the retained window.
    private static TerminalAdapter BuildPromptMarks() =>
        Populate(
            BenchmarkContent.WorkloadLines(WorkloadLines, minLength: 40, lengthSpan: 71, promptMarkEvery: 14),
            alternateScreen: false);

    private static TerminalAdapter BuildAltScreen() =>
        Populate(BenchmarkContent.WorkloadLines(WorkloadLines, minLength: 40, lengthSpan: 71), alternateScreen: true);

    private static TerminalAdapter BuildWide() =>
        Populate(BenchmarkContent.WideCharLines(WorkloadLines), alternateScreen: false);

    private static TerminalAdapter Populate(byte[] workload, bool alternateScreen)
    {
        var adapter = new TerminalAdapter(24, 80, scrollbackCapacity: ScrollbackLines);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(workload);
        if (adapter.Buffer.ScrollbackCount != ScrollbackLines)
            throw new InvalidOperationException(
                $"Scrollback ring not full after setup: {adapter.Buffer.ScrollbackCount}.");

        if (alternateScreen)
        {
            // Enter the alternate screen (CSI ? 1049 h) and paint a screenful of full-screen-app style text;
            // the hidden main screen with its 10k scrollback is still reflowed on every resize.
            parser.Feed("\u001b[?1049h\u001b[2J\u001b[H"u8);
            parser.Feed(BenchmarkContent.LogGrid(24, 80));
            if (!adapter.Buffer.IsAlternateScreenActive)
                throw new InvalidOperationException("Alternate screen is not active after CSI ? 1049 h.");
        }

        return adapter;
    }

    private static TerminalAdapter Fixture(ref TerminalAdapter? cache, Func<TerminalAdapter> build)
    {
        if (cache is not null)
            return cache;

        cache = build();
        if (Environment.GetEnvironmentVariable("DOTTY_REFLOW_BENCH_MEMORY") == "1")
            PrintRetainedMemory(cache);
        return cache;
    }

    // Setup-time diagnostic for the "retained pooled memory after reflow" estimate in plan 08, section 1.5.
    private static void PrintRetainedMemory(TerminalAdapter adapter)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        for (int i = 0; i < 10; i++)
        {
            adapter.Buffer.Resize(40, 120);
            adapter.Buffer.Resize(24, 80);
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine(
            $"// Reflow fixture managed memory: {before / 1_048_576.0:F1} MiB before the first resize, " +
            $"{after / 1_048_576.0:F1} MiB after 20 resizes ({(after - before) / 1_048_576.0:F1} MiB retained by pooled reflow state).");
    }

    private static void ResizeThereAndBack(TerminalAdapter adapter, int rows, int columns)
    {
        // TerminalBuffer.Resize rather than TerminalAdapter.ResizeBuffer: the latter swallows exceptions,
        // which would let a broken resize benchmark "succeed" at nothing.
        adapter.Buffer.Resize(rows, columns);
        adapter.Buffer.Resize(24, 80);
    }

    #endregion

    [Benchmark(Description = "Reflow: 10k sb 80x24 -> 120x40 -> 80x24", OperationsPerInvoke = 2)]
    public void Reflow_120x40() =>
        ResizeThereAndBack(Fixture(ref _plain, BuildPlain), 40, 120);

    [Benchmark(Description = "Reflow: 10k sb 80x24 -> 200x60 -> 80x24", OperationsPerInvoke = 2)]
    public void Reflow_200x60() =>
        ResizeThereAndBack(Fixture(ref _plain, BuildPlain), 60, 200);

    [Benchmark(Description = "Reflow: 10k sb rows-only 80x24 -> 80x40 -> 80x24", OperationsPerInvoke = 2)]
    public void Reflow_RowsOnly() =>
        ResizeThereAndBack(Fixture(ref _plain, BuildPlain), 40, 80);

    [Benchmark(Description = "Reflow: 10k sb + 500 prompt marks 80x24 -> 120x40 -> 80x24", OperationsPerInvoke = 2)]
    public void Reflow_PromptMarks() =>
        ResizeThereAndBack(Fixture(ref _promptMarks, BuildPromptMarks), 40, 120);

    [Benchmark(Description = "Reflow: 10k sb, alt screen active 80x24 -> 120x40 -> 80x24", OperationsPerInvoke = 2)]
    public void Reflow_AltScreenActive() =>
        ResizeThereAndBack(Fixture(ref _altScreen, BuildAltScreen), 40, 120);

    [Benchmark(Description = "Reflow: 10k sb wide-char lines 80x24 -> 120x40 -> 80x24", OperationsPerInvoke = 2)]
    public void Reflow_WideChars() =>
        ResizeThereAndBack(Fixture(ref _wide, BuildWide), 40, 120);
}
