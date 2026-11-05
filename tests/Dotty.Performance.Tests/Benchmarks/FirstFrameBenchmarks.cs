using BenchmarkDotNet.Attributes;
using Dotty.Performance.Tests.Data;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.Performance.Tests.Benchmarks;

[BenchmarkCategory("Startup")]
public class FirstFrameBenchmarks : PerformanceTestBase
{
    private byte[] _ansiText1Kb = null!;

    protected override bool UseNoGcRegion => false;

    public override void SetupBenchmark()
    {
        base.SetupBenchmark();
        // StartupBenchmarks consumed this deterministic plain-text fixture first; preserve
        // the shared generator's RNG position so the ANSI payload remains identical.
        _ = TestDataGenerator.GeneratePlainText(1000);
        _ansiText1Kb = TestDataGenerator.GenerateBasicAnsiText(1000, 0.1);
    }

    [Benchmark(Description = "First Frame: Parse + Render 1KB")]
    // Each invocation constructs fresh state. A longer batch gives the short operation
    // sufficient timing granularity while preserving per-operation allocation accounting.
    [InvocationCount(4096)]
    public void FirstFrame_ParseAndRender()
    {
        var adapter = new TerminalAdapter(24, 80, scrollbackCapacity: 0);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(_ansiText1Kb);
        adapter.RequestRenderExtern();
    }
}
