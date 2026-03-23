using BenchmarkDotNet.Running;

namespace PerfHarness
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<FullStackBenchmarks>();
        }
    }
}
