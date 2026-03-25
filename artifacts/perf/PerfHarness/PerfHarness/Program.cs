using System;
using System.Diagnostics;
using System.Text;
using Dotty.Terminal.Parser;
using Dotty.Terminal.Adapter;

class Program {
    static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "bench") {
            BenchmarkDotNet.Running.BenchmarkRunner.Run<PerfHarness.FullStackBenchmarks>();
            return;
        }

        var adapter = new TerminalAdapter(24, 80);
        var parser = new BasicAnsiParser();
        parser.Handler = adapter;
        
        var sb = new StringBuilder();
        for(int i=0; i<500000; i++) sb.Append('y').Append('\n');
        byte[] payload = Encoding.UTF8.GetBytes(sb.ToString());
        
        var sw = Stopwatch.StartNew();
        parser.Feed(payload);
        sw.Stop();
        
        Console.WriteLine($"Parsed {payload.Length} bytes in {sw.ElapsedMilliseconds} ms");
    }
}
