using System.Runtime.CompilerServices;
using System.Text;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Seeded randomized reflow equivalence tests (plan 08, WI-R1): the safety net for the anchor-only mapping, rows-only,
/// streaming and deferred-hidden-main reflow rewrites (WI-R2..R5). Three layers, from strongest to weakest evidence:
///
/// 1. <see cref="MappingConsumersMatchLegacyDenseOracleAcrossRandomResizes"/>: after every random resize, the active cursor,
///    saved cursor, alternate-saved cursor, rebased prompt marks and scrollback counters equal what the frozen
///    <see cref="LegacyDenseReflowMapping"/> oracle predicts from the pre-resize state. This is a true differential test; it
///    needs no recorded data and keeps checking whatever replaces the mapper.
/// 2. <see cref="CanonicalDumpIsDeterministic"/>: the canonical dump (<see cref="ReflowCanonicalDump"/>) is a pure function of
///    (seed, resize sequence), so recorded dumps are comparable at all.
/// 3. <see cref="CanonicalDumpsMatchRecordedGoldens"/>: per-step hashes of the full canonical dump (every retained row's cells,
///    row metadata, cursors, scrollback count, prompt marks) against <c>Goldens/reflow-canonical.txt</c>. That file must be
///    recorded ON THE PRE-CHANGE IMPLEMENTATION; it could not be generated where this test was written (no .NET SDK), so
///    until it exists the test is reported as skipped (never silently green). Record it once, before WI-R2 lands:
///    <c>DOTTY_RECORD_REFLOW_GOLDENS=1 dotnet test tests/Dotty.App.Tests --filter ReflowRandomizedEquivalenceTests</c>, then
///    commit the generated file.
/// </summary>
public sealed class ReflowRandomizedEquivalenceTests
{
    private const int ResizeSteps = 6;
    private const string RecordEnvironmentVariable = "DOTTY_RECORD_REFLOW_GOLDENS";

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 1; seed <= 48; seed++)
            data.Add(seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MappingConsumersMatchLegacyDenseOracleAcrossRandomResizes(int seed)
    {
        foreach (ReflowScenarioKind kind in Enum.GetValues<ReflowScenarioKind>())
        {
            var buffer = ReflowScenario.Build(seed, kind).Buffer;
            try
            {
                var random = new Random(seed * 7919 + 13);
                for (int step = 0; step < ResizeSteps; step++)
                {
                    var (rows, columns) = ReflowScenario.NextSize(random, buffer.Rows, buffer.Columns);
                    if (rows == buffer.Rows && columns == buffer.Columns)
                        continue; // TerminalBuffer.Resize ignores same-size requests

                    string context = $"seed {seed} {kind} step {step} -> {rows}x{columns}";
                    var expected = LegacyReflowPrediction.Predict(buffer, rows, columns);
                    buffer.Resize(rows, columns);
                    expected.AssertMatches(buffer, context);
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
    public void CanonicalDumpIsDeterministic(int seed)
    {
        foreach (ReflowScenarioKind kind in Enum.GetValues<ReflowScenarioKind>())
        {
            string[] first = RunAndDump(seed, kind);
            string[] second = RunAndDump(seed, kind);
            Assert.Equal(first, second);
            Assert.All(first, dump => Assert.True(dump == "unchanged" || dump.StartsWith("buffer ", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void CanonicalDumpListsRetainedRowsOfAKnownBuffer()
    {
        // 2 rows x 3 columns, 4 lines of scrollback: "abcdef" soft-wraps over both rows; nothing scrolls.
        var buffer = new TerminalBuffer(rows: 2, columns: 3, scrollbackCapacity: 4);
        try
        {
            buffer.WriteText("abcdef".AsSpan(), CellAttributes.Default);
            string dump = ReflowCanonicalDump.Dump(buffer);

            Assert.StartsWith("buffer 2x3 alt=0 scrollback=0 ", dump);
            Assert.Contains(" : 61 62 63\n", dump);
            Assert.Contains(" : 64 65 66\n", dump);
            Assert.Contains("screen active 2x3 retainedScrollback=0\n", dump);
            Assert.Equal(16, ReflowCanonicalDump.Hash(dump).Length);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Fact]
    public void CanonicalDumpsMatchRecordedGoldens()
    {
        string goldenPath = GoldenFile();
        string current = BuildGoldenText();

        if (Environment.GetEnvironmentVariable(RecordEnvironmentVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, current, new UTF8Encoding(false));
            return;
        }

        if (!File.Exists(goldenPath))
        {
            Assert.Skip(
                $"No recorded reflow goldens at {goldenPath}. Record them on the pre-change implementation with " +
                $"{RecordEnvironmentVariable}=1 and commit the file (see the class remarks).");
        }

        string[] expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] actual = current.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var mismatches = new List<string>();
        for (int i = 0; i < Math.Max(expected.Length, actual.Length) && mismatches.Count < 8; i++)
        {
            string e = i < expected.Length ? expected[i] : "<missing>";
            string a = i < actual.Length ? actual[i] : "<missing>";
            if (e != a)
                mismatches.Add($"expected '{e}', actual '{a}'");
        }

        Assert.True(mismatches.Count == 0, "Canonical reflow dumps differ from the recorded goldens: " + string.Join("; ", mismatches));
    }

    private static string[] RunAndDump(int seed, ReflowScenarioKind kind)
    {
        var buffer = ReflowScenario.Build(seed, kind).Buffer;
        try
        {
            var dumps = new List<string> { ReflowCanonicalDump.Dump(buffer) };
            var random = new Random(seed * 7919 + 13);
            for (int step = 0; step < ResizeSteps; step++)
            {
                var (rows, columns) = ReflowScenario.NextSize(random, buffer.Rows, buffer.Columns);
                if (rows == buffer.Rows && columns == buffer.Columns)
                {
                    dumps.Add("unchanged");
                    continue;
                }

                buffer.Resize(rows, columns);
                dumps.Add(ReflowCanonicalDump.Dump(buffer));
            }

            return dumps.ToArray();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    // One line per (seed, kind, step): "seed kind step rows x columns hash". Step 0 is the pre-resize state.
    private static string BuildGoldenText()
    {
        var text = new StringBuilder();
        for (int seed = 1; seed <= 24; seed++)
        {
            foreach (ReflowScenarioKind kind in Enum.GetValues<ReflowScenarioKind>())
            {
                string[] dumps = RunAndDump(seed, kind);
                for (int step = 0; step < dumps.Length; step++)
                {
                    string hash = dumps[step] == "unchanged" ? "unchanged" : ReflowCanonicalDump.Hash(dumps[step]);
                    text.Append(seed).Append(' ').Append(kind).Append(' ').Append(step).Append(' ').Append(hash).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    private static string GoldenFile([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Goldens", "reflow-canonical.txt");
}
