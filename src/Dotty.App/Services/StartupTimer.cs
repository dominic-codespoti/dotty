using System;
using System.Diagnostics;
using System.IO;

namespace Dotty.App.Services;

/// <summary>
/// Writes nanosecond-precision stage timestamps to a log file during app startup.
/// Controlled by the DOTTY_BENCH_STARTUP_LOG environment variable.
/// </summary>
internal sealed class StartupTimer : IDisposable
{
    private readonly string _path;
    private readonly long _startNs;

    public StartupTimer(string path)
    {
        _path = path;
        _startNs = Stopwatch.GetTimestamp();
        WriteLine("__startup_start");
    }

    public void Stage(string name)
    {
        long nowNs = Stopwatch.GetTimestamp();
        double elapsedMs = (nowNs - _startNs) * 1000.0 / Stopwatch.Frequency;
        WriteLine($"{name} {elapsedMs:F3}");
    }

    private void WriteLine(string line)
    {
        try
        {
            File.AppendAllText(_path, $"{line}\n");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Stage("__end");
    }
}
