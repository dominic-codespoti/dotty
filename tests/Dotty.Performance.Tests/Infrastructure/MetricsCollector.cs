using System.Diagnostics;
using System.Runtime;

namespace Dotty.Performance.Tests.Infrastructure;

/// <summary>
/// Collects detailed performance metrics including CPU, memory, and GC statistics
/// </summary>
public class MetricsCollector : IDisposable
{
    private readonly Process _process;
    private readonly Stopwatch _stopwatch;
    private readonly List<MetricSnapshot> _snapshots;
    private long _startTime;
    private long _baselineAllocatedBytes;
    private int _baselineGcCount0;
    private int _baselineGcCount1;
    private int _baselineGcCount2;

    public MetricsCollector()
    {
        _process = Process.GetCurrentProcess();
        _stopwatch = new Stopwatch();
        _snapshots = new List<MetricSnapshot>();
    }

    /// <summary>
    /// Start collecting metrics
    /// </summary>
    public void Start()
    {
        _process.Refresh();
        _stopwatch.Restart();
        _startTime = _stopwatch.ElapsedMilliseconds;
        
        _baselineAllocatedBytes = GC.GetTotalAllocatedBytes();
        _baselineGcCount0 = GC.CollectionCount(0);
        _baselineGcCount1 = GC.CollectionCount(1);
        _baselineGcCount2 = GC.CollectionCount(2);
    }

    /// <summary>
    /// Take a snapshot of current metrics
    /// </summary>
    public MetricSnapshot Snapshot(string label = "")
    {
        _process.Refresh();
        
        var snapshot = new MetricSnapshot
        {
            Timestamp = _stopwatch.ElapsedMilliseconds - _startTime,
            Label = label,
            WorkingSetBytes = _process.WorkingSet64,
            PrivateMemoryBytes = _process.PrivateMemorySize64,
            VirtualMemoryBytes = _process.VirtualMemorySize64,
            PagedMemoryBytes = _process.PagedMemorySize64,
            GcGen0Collections = GC.CollectionCount(0) - _baselineGcCount0,
            GcGen1Collections = GC.CollectionCount(1) - _baselineGcCount1,
            GcGen2Collections = GC.CollectionCount(2) - _baselineGcCount2,
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes() - _baselineAllocatedBytes,
            Gen0HeapSize = GC.GetGeneration(0),
            Gen1HeapSize = GC.GetGeneration(1),
            Gen2HeapSize = GC.GetGeneration(2),
            ThreadCount = _process.Threads.Count,
            HandleCount = _process.HandleCount,
            CpuUsagePercent = GetCpuUsage()
        };

        _snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Stop collecting and return final summary
    /// </summary>
    public MetricsSummary Stop()
    {
        _stopwatch.Stop();
        var finalSnapshot = Snapshot("Final");
        
        return new MetricsSummary
        {
            DurationMs = _stopwatch.ElapsedMilliseconds,
            Snapshots = _snapshots.ToArray(),
            PeakWorkingSetBytes = _snapshots.Max(s => s.WorkingSetBytes),
            AverageWorkingSetBytes = (long)_snapshots.Average(s => s.WorkingSetBytes),
            FinalWorkingSetBytes = finalSnapshot.WorkingSetBytes,
            TotalAllocatedBytes = finalSnapshot.TotalAllocatedBytes,
            GcGen0Collections = finalSnapshot.GcGen0Collections,
            GcGen1Collections = finalSnapshot.GcGen1Collections,
            GcGen2Collections = finalSnapshot.GcGen2Collections,
            AllocationRateBytesPerSec = finalSnapshot.TotalAllocatedBytes / (_stopwatch.ElapsedMilliseconds / 1000.0),
            SnapshotCount = _snapshots.Count
        };
    }

    /// <summary>
    /// Get all collected snapshots
    /// </summary>
    public IReadOnlyList<MetricSnapshot> GetSnapshots() => _snapshots;

    private double GetCpuUsage()
    {
        // Simplified CPU usage - for accurate measurement would need time-based calculation
        return 0;
    }

    public void Dispose()
    {
        _process?.Dispose();
    }
}

/// <summary>
/// Single point-in-time metric snapshot
/// </summary>
public struct MetricSnapshot
{
    public long Timestamp { get; set; }
    public string Label { get; set; }
    public long WorkingSetBytes { get; set; }
    public long PrivateMemoryBytes { get; set; }
    public long VirtualMemoryBytes { get; set; }
    public long PagedMemoryBytes { get; set; }
    public int GcGen0Collections { get; set; }
    public int GcGen1Collections { get; set; }
    public int GcGen2Collections { get; set; }
    public long TotalAllocatedBytes { get; set; }
    public int Gen0HeapSize { get; set; }
    public int Gen1HeapSize { get; set; }
    public int Gen2HeapSize { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public double CpuUsagePercent { get; set; }

    public override string ToString() =>
        $"[{Timestamp}ms] {Label}: WS={FormatBytes(WorkingSetBytes)}, " +
        $"Alloc={FormatBytes(TotalAllocatedBytes)}, GC0={GcGen0Collections}";

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int suffixIndex = 0;
        double value = bytes;
        
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }
        
        return $"{value:F2}{suffixes[suffixIndex]}";
    }
}

/// <summary>
/// Summary of metrics collected over a time period
/// </summary>
public class MetricsSummary
{
    public long DurationMs { get; set; }
    public MetricSnapshot[] Snapshots { get; set; } = Array.Empty<MetricSnapshot>();
    public long PeakWorkingSetBytes { get; set; }
    public long AverageWorkingSetBytes { get; set; }
    public long FinalWorkingSetBytes { get; set; }
    public long TotalAllocatedBytes { get; set; }
    public int GcGen0Collections { get; set; }
    public int GcGen1Collections { get; set; }
    public int GcGen2Collections { get; set; }
    public double AllocationRateBytesPerSec { get; set; }
    public int SnapshotCount { get; set; }

    public override string ToString() =>
        $"Duration: {DurationMs}ms\n" +
        $"Peak WS: {FormatBytes(PeakWorkingSetBytes)}\n" +
        $"Avg WS: {FormatBytes(AverageWorkingSetBytes)}\n" +
        $"Total Allocated: {FormatBytes(TotalAllocatedBytes)}\n" +
        $"Alloc Rate: {FormatBytes((long)AllocationRateBytesPerSec)}/s\n" +
        $"GC Collections: Gen0={GcGen0Collections}, Gen1={GcGen1Collections}, Gen2={GcGen2Collections}";

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int suffixIndex = 0;
        double value = bytes;
        
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }
        
        return $"{value:F2}{suffixes[suffixIndex]}";
    }
}

/// <summary>
/// Lightweight operation timer for microbenchmarks
/// </summary>
public readonly struct OperationTimer : IDisposable
{
    private readonly long _startTimestamp;
    private readonly Action<TimeSpan> _onComplete;

    public OperationTimer(Action<TimeSpan> onComplete)
    {
        _startTimestamp = Stopwatch.GetTimestamp();
        _onComplete = onComplete;
    }

    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        _onComplete?.Invoke(elapsed);
    }

    /// <summary>
    /// Create a timer that records to a list
    /// </summary>
    public static OperationTimer ToList(List<double> listMs) =>
        new(elapsed => listMs.Add(elapsed.TotalMilliseconds));

    /// <summary>
    /// Create a timer with custom callback
    /// </summary>
    public static OperationTimer Create(Action<TimeSpan> callback) =>
        new(callback);
}
