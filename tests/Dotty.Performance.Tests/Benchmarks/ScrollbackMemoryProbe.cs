using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

/// <summary>
/// Headless scrollback memory probe (<c>--mode scrollback-memory</c>, plan-03 WI-0).
/// Builds N terminals with a seeded synthetic history, walks stages (fresh, resize x2, alt enter/exit,
/// ED 2, CSI 3 J), and prints a table of BufferMemoryMetrics counters plus process memory with the
/// runtime baseline subtracted. RSS/PSS are report-only; the only hard assertions are on deterministic counters.
/// Counters are discovered by reflection over <see cref="BufferMemorySnapshot"/>, so counters added by other
/// work appear as extra columns and a missing counter simply leaves its column absent (never a crash).
/// Options: <c>--probe-tabs 1,20 --probe-history 0,500,5000 --probe-cols 80,200</c>.
/// </summary>
public static class ScrollbackMemoryProbe
{
    private const int Rows = 24;
    private const int ScrollbackCapacity = 5000;
    private const int Seed = 20260101;
    private const string CommittedBoundCounter = "LiveArenaCommittedBoundBytes";
    private const string CapacityCounter = nameof(BufferMemorySnapshot.LiveArenaCapacityBytes);

    private static readonly string[] Stages = ["fresh", "resize2", "alt", "ed2", "ed3"];

    private readonly record struct ProcessMemory(long Rss, long Pss, long PssAnon, long Private);

    private sealed record Row(
        int Tabs, int History, int Cols, string Stage,
        ProcessMemory Memory, long GcHeap, Dictionary<string, long> Counters);

    public static int Run(string[] args)
    {
        int[] tabsList = ParseList(args, "--probe-tabs", [1, 20]);
        int[] historyList = ParseList(args, "--probe-history", [0, 500, 5000]);
        int[] colsList = ParseList(args, "--probe-cols", [80, 200]);

        Console.WriteLine("=== Scrollback memory probe (headless) ===");
        ForceGc();
        ProcessMemory baseline = ReadProcessMemory();
        long baselineGc = GC.GetTotalMemory(true);
        PropertyInfo[] counterProps = typeof(BufferMemorySnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(long)).ToArray();
        var counterBaseline = ReadCounters(counterProps);
        Console.WriteLine($"Runtime baseline (before any buffer): RSS={Mib(baseline.Rss)} PSS={Mib(baseline.Pss)} " +
                          $"PssAnon={Mib(baseline.PssAnon)} MiB; managed={Mib(baselineGc)} MiB. Values below are baseline-subtracted.");
        Console.WriteLine("Memory counters are report-only; hard assertions apply to deterministic counters only.");
        Console.WriteLine();

        var rows = new List<Row>();
        foreach (int cols in colsList)
            foreach (int history in historyList)
            {
                byte[] stream = BuildStream(history, cols);
                foreach (int tabs in tabsList)
                    rows.AddRange(RunConfiguration(tabs, history, cols, stream, counterProps, counterBaseline, baseline));
            }

        PrintTable(rows, counterProps);
        var failures = CheckAssertions(rows, tabsList);
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("Scrollback memory probe: counter assertions passed.");
            return 0;
        }

        foreach (string failure in failures)
            Console.WriteLine("FAIL: " + failure);
        return 1;
    }

    private static List<Row> RunConfiguration(
        int tabs, int history, int cols, byte[] stream,
        PropertyInfo[] counterProps, Dictionary<string, long> counterBaseline, ProcessMemory baseline)
    {
        var rows = new List<Row>();
        // Counters are process-wide: take the delta against the value just before this population exists.
        ForceGc();
        var before = ReadCounters(counterProps);
        var terminals = new List<(TerminalAdapter Adapter, BasicAnsiParser Parser)>(tabs);
        try
        {
            for (int i = 0; i < tabs; i++)
            {
                var adapter = new TerminalAdapter(Rows, cols, ScrollbackCapacity);
                var parser = new BasicAnsiParser { Handler = adapter };
                parser.Feed(stream);
                terminals.Add((adapter, parser));
            }

            rows.Add(Capture("fresh"));

            foreach (var (adapter, _) in terminals) adapter.Buffer.Resize(Rows + 6, cols + 20);
            foreach (var (adapter, _) in terminals) adapter.Buffer.Resize(Rows, cols);
            rows.Add(Capture("resize2"));

            foreach (var (adapter, _) in terminals)
            {
                adapter.Buffer.SetAlternateScreen(true);
                adapter.Buffer.SetAlternateScreen(false);
            }
            rows.Add(Capture("alt"));

            byte[] ed2 = "\u001b[2J"u8.ToArray();
            foreach (var (_, parser) in terminals) parser.Feed(ed2);
            rows.Add(Capture("ed2"));

            byte[] ed3 = "\u001b[3J"u8.ToArray();
            foreach (var (_, parser) in terminals) parser.Feed(ed3);
            rows.Add(Capture("ed3"));
        }
        finally
        {
            foreach (var (adapter, _) in terminals) adapter.Dispose();
        }

        return rows;

        Row Capture(string stage)
        {
            foreach (var (adapter, _) in terminals) RunHistoryMaintenanceToCompletion(adapter.Buffer);
            ForceGc();
            var memory = ReadProcessMemory();
            var counters = ReadCounters(counterProps);
            var deltas = new Dictionary<string, long>(counters.Count);
            foreach (var (name, value) in counters)
                deltas[name] = value - (before.TryGetValue(name, out long b) ? b : 0);
            var adjusted = new ProcessMemory(
                memory.Rss - baseline.Rss, memory.Pss - baseline.Pss,
                memory.PssAnon - baseline.PssAnon, memory.Private - baseline.Private);
            return new Row(tabs, history, cols, stage, adjusted, GC.GetTotalMemory(false), deltas);
        }
    }

    /// <summary>
    /// Runs the buffer's idle history maintenance, if this build has one, until it reports no more work.
    /// Resolved by name so the probe compiles before and after that API exists.
    /// </summary>
    private static void RunHistoryMaintenanceToCompletion(TerminalBuffer buffer)
    {
        MethodInfo? method = typeof(TerminalBuffer).GetMethod(
            "RunHistoryMaintenance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
        if (method is null)
            return;
        for (int i = 0; i < 100_000; i++)
        {
            object? result = method.Invoke(buffer, null);
            if (result is not bool more || !more)
                return;
        }
    }

    private static List<string> CheckAssertions(List<Row> rows, int[] tabsList)
    {
        var failures = new List<string>();
        foreach (Row row in rows)
        {
            if (row.Counters.TryGetValue(CapacityCounter, out long capacity) && capacity < 0)
                failures.Add($"{Describe(row)}: {CapacityCounter} delta is negative ({capacity}).");
            if (row.Counters.TryGetValue(CommittedBoundCounter, out long bound) &&
                row.Counters.TryGetValue(CapacityCounter, out long reserved) && bound > reserved)
                failures.Add($"{Describe(row)}: {CommittedBoundCounter} ({bound}) exceeds {CapacityCounter} ({reserved}).");
        }

        // Reserved capacity is a pure function of (cols, rows, history cap): it must scale linearly with tabs.
        int lo = tabsList.Min();
        foreach (Row one in rows.Where(r => r.Tabs == lo && r.Stage == "fresh"))
            foreach (Row many in rows.Where(r => r.Tabs != lo && r.Stage == "fresh" && r.History == one.History && r.Cols == one.Cols))
            {
                if (!one.Counters.TryGetValue(CapacityCounter, out long a) || !many.Counters.TryGetValue(CapacityCounter, out long b))
                    continue;
                if (a * many.Tabs != b * lo)
                    failures.Add($"history={one.History} cols={one.Cols}: {CapacityCounter} not linear in tabs ({lo} tabs={a}, {many.Tabs} tabs={b}).");
            }

        // If the committed-bound counter exists, a longer history must never commit less than a shorter one
        // for the same population at the fresh stage.
        foreach (var group in rows.Where(r => r.Stage == "fresh" && r.Counters.ContainsKey(CommittedBoundCounter))
                     .GroupBy(r => (r.Tabs, r.Cols)))
        {
            var ordered = group.OrderBy(r => r.History).ToArray();
            for (int i = 1; i < ordered.Length; i++)
            {
                if (ordered[i].Counters[CommittedBoundCounter] < ordered[i - 1].Counters[CommittedBoundCounter])
                    failures.Add($"tabs={group.Key.Tabs} cols={group.Key.Cols}: {CommittedBoundCounter} decreased from history " +
                                 $"{ordered[i - 1].History} to {ordered[i].History}.");
            }
        }

        return failures;
    }

    private static string Describe(Row row) => $"tabs={row.Tabs} history={row.History} cols={row.Cols} stage={row.Stage}";

    private static void PrintTable(List<Row> rows, PropertyInfo[] counterProps)
    {
        var sb = new StringBuilder();
        sb.Append("tabs | history | cols | stage   | RSS MiB | PSS MiB | PssAnon MiB | Private MiB | managed MiB");
        foreach (var p in counterProps) sb.Append(" | ").Append(p.Name);
        Console.WriteLine(sb.ToString());
        foreach (Row r in rows.OrderBy(r => r.Cols).ThenBy(r => r.History).ThenBy(r => r.Tabs).ThenBy(r => Array.IndexOf(Stages, r.Stage)))
        {
            sb.Clear();
            sb.Append($"{r.Tabs,4} | {r.History,7} | {r.Cols,4} | {r.Stage,-7} | {Mib(r.Memory.Rss),7} | {Mib(r.Memory.Pss),7} | " +
                      $"{Mib(r.Memory.PssAnon),11} | {Mib(r.Memory.Private),11} | {Mib(r.GcHeap),11}");
            foreach (var p in counterProps)
                sb.Append(" | ").Append(r.Counters.TryGetValue(p.Name, out long v) ? v.ToString(CultureInfo.InvariantCulture) : string.Empty);
            Console.WriteLine(sb.ToString());
        }
    }

    private static Dictionary<string, long> ReadCounters(PropertyInfo[] props)
    {
        object snapshot = BufferMemoryMetrics.GetSnapshot();
        var result = new Dictionary<string, long>(props.Length);
        foreach (var p in props)
            result[p.Name] = p.GetValue(snapshot) is long v ? v : 0;
        return result;
    }

    private static void ForceGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static ProcessMemory ReadProcessMemory()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var smaps = ParseSmapsRollup(File.ReadAllText("/proc/self/smaps_rollup"));
                return new ProcessMemory(
                    smaps.GetValueOrDefault("Rss"), smaps.GetValueOrDefault("Pss"),
                    smaps.GetValueOrDefault("Pss_Anon"),
                    smaps.GetValueOrDefault("Private_Dirty") + smaps.GetValueOrDefault("Private_Clean"));
            }
            catch (IOException)
            {
            }
        }

        using var process = Process.GetCurrentProcess();
        return new ProcessMemory(process.WorkingSet64, 0, 0, process.PrivateMemorySize64);
    }

    private static Dictionary<string, long> ParseSmapsRollup(string text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.EndsWith(" kB", StringComparison.Ordinal))
                continue;
            string number = line[(colon + 1)..^3].Trim();
            if (long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long kb))
                values[line[..colon]] = kb * 1024;
        }

        return values;
    }

    /// <summary>Seeded stream: ~70% plain ASCII lines, ~30% SGR-coloured, a few OSC 8 links and wide glyphs.</summary>
    private static byte[] BuildStream(int lines, int cols)
    {
        var random = new Random(Seed + cols);
        var sb = new StringBuilder(lines * (cols + 16));
        string[] words = ["alpha", "beta", "gamma", "delta", "error:", "warning:", "src/main.rs", "0123456789", "the", "quick", "brown", "fox"];
        for (int i = 0; i < lines; i++)
        {
            int target = Math.Max(8, cols - 4 - random.Next(0, Math.Max(1, cols / 2)));
            var text = new StringBuilder(target);
            while (text.Length < target)
                text.Append(words[random.Next(words.Length)]).Append(' ');
            if (text.Length > target) text.Length = target;

            int kind = random.Next(100);
            if (kind < 70)
                sb.Append(text);
            else if (kind < 97)
                sb.Append("\u001b[3").Append(random.Next(1, 8)).Append("m").Append(text).Append("\u001b[0m");
            else if (kind < 99)
                sb.Append("\u001b]8;;https://example.invalid/").Append(i).Append("\u001b\\").Append(text, 0, Math.Min(text.Length, 16))
                  .Append("\u001b]8;;\u001b\\");
            else
                sb.Append("\u65E5\u672C\u8A9E ").Append(text, 0, Math.Min(text.Length, Math.Max(0, cols / 2 - 4)));
            sb.Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int[] ParseList(string[] args, string name, int[] fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
                continue;
            var values = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length != 0)
                return values;
        }

        return fallback;
    }

    private static string Mib(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture);
}
