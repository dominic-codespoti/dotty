using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Represents a complete snapshot of performance metrics with accurate CPU and memory data.
/// </summary>
public record PerformanceSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string TestName { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    
    // FPS Metrics
    public double Fps { get; init; }
    public double FpsMin { get; init; }
    public double FpsMax { get; init; }
    public double FpsAvg { get; init; }
    
    // Frame Time Metrics (milliseconds)
    public double FrameTimeMin { get; init; }
    public double FrameTimeMax { get; init; }
    public double FrameTimeAvg { get; init; }
    public double FrameTimeP95 { get; init; }
    public double FrameTimeP99 { get; init; }
    public int FrameTimeCount { get; init; }
    
    // Parser Throughput
    public double ParserBytesPerSecond { get; init; }
    public double ParserSequencesPerSecond { get; init; }
    public long TotalBytesProcessed { get; init; }
    public long TotalSequencesProcessed { get; init; }
    
    // CPU Metrics (Cross-platform)
    public double CpuPercentage { get; init; }
    public double CpuUserPercentage { get; init; }
    public double CpuSystemPercentage { get; init; }
    public double CpuProcessTimeSeconds { get; init; }
    public double CpuUserTimeSeconds { get; init; }
    public double CpuSystemTimeSeconds { get; init; }
    public double[]? PerCoreCpuUsage { get; init; }
    public int ProcessorCount { get; init; }
    
    // Memory Metrics (Detailed)
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long VirtualMemoryBytes { get; init; }
    public long PagedMemoryBytes { get; init; }
    public long NonPagedMemoryBytes { get; init; }
    
    // Managed Memory
    public long ManagedHeapBytes { get; init; }
    public long ManagedHeapUsedBytes { get; init; }
    public long LargeObjectHeapBytes { get; init; }
    public long Gen0HeapBytes { get; init; }
    public long Gen1HeapBytes { get; init; }
    public long Gen2HeapBytes { get; init; }
    public long AllocatedBytes { get; init; }
    public double AllocationsPerSecond { get; init; }
    
    // Unmanaged Memory
    public long UnmanagedMemoryBytes { get; init; }
    public long GCHandleCount { get; init; }
    
    // Memory Trends
    public long PeakWorkingSetBytes { get; init; }
    public long PeakVirtualMemoryBytes { get; init; }
    public long PeakPagedMemoryBytes { get; init; }
    
    // GC Metrics
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public TimeSpan TotalGCTime { get; init; }
    public double GCPausePercentage { get; init; }
    public long GCFinalizationPendingCount { get; init; }
    public long GCCommissionedGCCount { get; init; }
    
    // Input Latency
    public double InputLatencyMinMs { get; init; }
    public double InputLatencyMaxMs { get; init; }
    public double InputLatencyAvgMs { get; init; }
    public double InputLatencyP95Ms { get; init; }
    
    // Scroll Performance
    public double ScrollLinesPerSecond { get; init; }
    public double ScrollTimeAvgMs { get; init; }
    public int ScrollOperationsCount { get; init; }
    
    // Cell Update Rate
    public double CellUpdatesPerSecond { get; init; }
    public long TotalCellsUpdated { get; init; }
    
    // Thread Metrics
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    
    // Platform Info
    public string Platform { get; init; } = RuntimeInformation.OSDescription;
    public string Framework { get; init; } = RuntimeInformation.FrameworkDescription;
    public bool Is64Bit { get; init; } = Environment.Is64BitProcess;
    
    // Raw counter data for advanced analysis
    public Dictionary<string, double> RawCounters { get; init; } = new();
    
    /// <summary>
    /// Gets a formatted summary of the performance snapshot.
    /// </summary>
    public string GetSummary()
    {
        return $"""
        Performance Snapshot: {TestName}
        Duration: {Duration.TotalSeconds:F2}s | Platform: {Platform}
        
        FPS: {Fps:F1} (min: {FpsMin:F1}, max: {FpsMax:F1}, avg: {FpsAvg:F1})
        Frame Time: avg={FrameTimeAvg:F2}ms, p95={FrameTimeP95:F2}ms, p99={FrameTimeP99:F2}ms
        
        CPU: {CpuPercentage:F1}% (User: {CpuUserPercentage:F1}%, System: {CpuSystemPercentage:F1}%)
        Process Time: {CpuProcessTimeSeconds:F2}s (User: {CpuUserTimeSeconds:F2}s, System: {CpuSystemTimeSeconds:F2}s)
        
        Parser Throughput: {ParserBytesPerSecond:F0} bytes/sec, {ParserSequencesPerSecond:F0} seq/sec
        
        Memory (Managed):
          Managed Heap: {ManagedHeapBytes / 1024 / 1024:F1} MB ({ManagedHeapUsedBytes / 1024 / 1024:F1} MB used)
          Large Object Heap: {LargeObjectHeapBytes / 1024 / 1024:F1} MB
          Gen0: {Gen0HeapBytes / 1024:F0} KB, Gen1: {Gen1HeapBytes / 1024:F0} KB, Gen2: {Gen2HeapBytes / 1024:F0} KB
          Allocated: {AllocatedBytes / 1024 / 1024:F1} MB
        
        Memory (Process):
          Working Set: {WorkingSetBytes / 1024 / 1024:F1} MB (Peak: {PeakWorkingSetBytes / 1024 / 1024:F1} MB)
          Private: {PrivateMemoryBytes / 1024 / 1024:F1} MB
          Virtual: {VirtualMemoryBytes / 1024 / 1024:F1} MB
          Unmanaged: {UnmanagedMemoryBytes / 1024 / 1024:F1} MB
        
        GC: Gen0={Gen0Collections}, Gen1={Gen1Collections}, Gen2={Gen2Collections}, Pause={GCPausePercentage:F2}%
        
        Input Latency: avg={InputLatencyAvgMs:F2}ms, p95={InputLatencyP95Ms:F2}ms
        Cell Updates: {CellUpdatesPerSecond:F0}/sec, Total: {TotalCellsUpdated}
        Threads: {ThreadCount}, Handles: {HandleCount}
        """;
    }
    
    /// <summary>
    /// Gets a compact one-line summary for logging.
    /// </summary>
    public string GetCompactSummary()
    {
        return $"[{TestName}] FPS={Fps:F1} | CPU={CpuPercentage:F1}% | " +
               $"Heap={ManagedHeapBytes / 1024 / 1024:F1}MB | " +
               $"WorkingSet={WorkingSetBytes / 1024 / 1024:F1}MB | " +
               $"FrameTime={FrameTimeAvg:F2}ms";
    }
    
    /// <summary>
    /// Alias for ManagedHeapUsedBytes for backward compatibility.
    /// </summary>
    public long HeapSizeBytes 
    { 
        get => ManagedHeapUsedBytes;
        init => ManagedHeapUsedBytes = value;
    }
    
    /// <summary>
    /// Serializes the snapshot to JSON.
    /// </summary>
    public string ToJson(JsonSerializerOptions? options = null)
    {
        options ??= new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(this, options);
    }
    
    /// <summary>
    /// Deserializes a snapshot from JSON.
    /// </summary>
    public static PerformanceSnapshot? FromJson(string json)
    {
        return JsonSerializer.Deserialize<PerformanceSnapshot>(json);
    }
}

/// <summary>
/// Interface for collecting performance metrics from the running application.
/// </summary>
public interface IPerformanceCounterCollector : IAsyncDisposable
{
    /// <summary>
    /// Starts collecting performance metrics.
    /// </summary>
    Task StartCollectionAsync(string testName, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stops collecting metrics and returns the results.
    /// </summary>
    Task<PerformanceSnapshot> StopCollectionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the current counter values without stopping collection.
    /// </summary>
    Task<PerformanceSnapshot> GetCurrentMetricsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resets all performance counters.
    /// </summary>
    Task ResetCountersAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a full performance snapshot including all available counters.
    /// </summary>
    Task<PerformanceSnapshot> GetPerformanceSnapshotAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Indicates whether performance counters are available.
    /// </summary>
    bool IsAvailable { get; }
    
    /// <summary>
    /// Event raised when a performance threshold is exceeded.
    /// </summary>
    event EventHandler<PerformanceThresholdEventArgs>? ThresholdExceeded;
}

/// <summary>
/// Event args for performance threshold exceeded events.
/// </summary>
public class PerformanceThresholdEventArgs : EventArgs
{
    public string MetricName { get; init; } = string.Empty;
    public double CurrentValue { get; init; }
    public double Threshold { get; init; }
    public string TestName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    
    public override string ToString() => 
        $"[{Timestamp:HH:mm:ss.fff}] Performance threshold exceeded in '{TestName}': " +
        $"{MetricName} = {CurrentValue:F2} (threshold: {Threshold:F2})";
}

/// <summary>
/// Cross-platform system performance metrics collector.
/// </summary>
public sealed class SystemPerformanceCollector
{
    private readonly Process _process;
    private readonly ILogger? _logger;
    private DateTime _lastCpuSampleTime;
    private TimeSpan _lastCpuUserTime;
    private TimeSpan _lastCpuSystemTime;
    private TimeSpan _lastCpuTotalTime;
    private long _lastGcCollections;
    private readonly List<double> _cpuSamples;
    private readonly List<long> _memorySamples;
    
    public SystemPerformanceCollector(ILogger? logger = null)
    {
        _process = Process.GetCurrentProcess();
        _logger = logger;
        _lastCpuSampleTime = DateTime.UtcNow;
        _lastCpuUserTime = _process.UserProcessorTime;
        _lastCpuSystemTime = _process.PrivilegedProcessorTime;
        _lastCpuTotalTime = _process.TotalProcessorTime;
        _lastGcCollections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        _cpuSamples = new List<double>();
        _memorySamples = new List<long>();
    }
    
    /// <summary>
    /// Samples current system-level performance metrics.
    /// </summary>
    public SystemPerformanceMetrics SampleMetrics()
    {
        _process.Refresh();
        var now = DateTime.UtcNow;
        var sampleInterval = now - _lastCpuSampleTime;
        
        // CPU Time calculations
        var currentUserTime = _process.UserProcessorTime;
        var currentSystemTime = _process.PrivilegedProcessorTime;
        var currentTotalTime = _process.TotalProcessorTime;
        
        var userTimeDelta = currentUserTime - _lastCpuUserTime;
        var systemTimeDelta = currentSystemTime - _lastCpuSystemTime;
        var totalTimeDelta = currentTotalTime - _lastCpuTotalTime;
        
        // Calculate CPU percentage
        double cpuPercentage = 0;
        double cpuUserPercentage = 0;
        double cpuSystemPercentage = 0;
        
        if (sampleInterval.TotalSeconds > 0)
        {
            var processorCount = Environment.ProcessorCount;
            var totalAvailable = sampleInterval.TotalSeconds * processorCount;
            
            cpuPercentage = (totalTimeDelta.TotalSeconds / totalAvailable) * 100;
            cpuUserPercentage = (userTimeDelta.TotalSeconds / totalAvailable) * 100;
            cpuSystemPercentage = (systemTimeDelta.TotalSeconds / totalAvailable) * 100;
        }
        
        // Store samples for averaging
        _cpuSamples.Add(cpuPercentage);
        if (_cpuSamples.Count > 100) _cpuSamples.RemoveAt(0);
        
        // Memory metrics
        var workingSet = _process.WorkingSet64;
        var privateMemory = _process.PrivateMemorySize64;
        var virtualMemory = _process.VirtualMemorySize64;
        var pagedMemory = _process.PagedMemorySize64;
        
        _memorySamples.Add(workingSet);
        if (_memorySamples.Count > 100) _memorySamples.RemoveAt(0);
        
        // Managed heap metrics
        var gcInfo = GC.GetGCMemoryInfo();
        var totalMemory = GC.GetTotalMemory(false);
        var totalAllocated = GC.GetTotalAllocatedBytes(false);
        
        // Calculate unmanaged memory
        var unmanagedMemory = workingSet - totalMemory;
        if (unmanagedMemory < 0) unmanagedMemory = 0;
        
        // Per-core CPU (if available)
        double[]? perCoreCpu = null;
        try
        {
            // Try to get per-core usage via /proc/stat on Linux
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                perCoreCpu = GetLinuxPerCoreCpuUsage();
            }
            // macOS doesn't expose per-process per-core CPU easily
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not get per-core CPU usage");
        }
        
        // GC Collections
        var gen0Collections = GC.CollectionCount(0);
        var gen1Collections = GC.CollectionCount(1);
        var gen2Collections = GC.CollectionCount(2);
        var totalCollections = gen0Collections + gen1Collections + gen2Collections;
        var collectionsDelta = totalCollections - _lastGcCollections;
        _lastGcCollections = totalCollections;
        
        // Update last sample times
        _lastCpuSampleTime = now;
        _lastCpuUserTime = currentUserTime;
        _lastCpuSystemTime = currentSystemTime;
        _lastCpuTotalTime = currentTotalTime;
        
        return new SystemPerformanceMetrics
        {
            Timestamp = now,
            SampleInterval = sampleInterval,
            
            // CPU
            CpuPercentage = cpuPercentage,
            CpuUserPercentage = cpuUserPercentage,
            CpuSystemPercentage = cpuSystemPercentage,
            CpuProcessTimeSeconds = currentTotalTime.TotalSeconds,
            CpuUserTimeSeconds = currentUserTime.TotalSeconds,
            CpuSystemTimeSeconds = currentSystemTime.TotalSeconds,
            PerCoreCpuUsage = perCoreCpu,
            ProcessorCount = Environment.ProcessorCount,
            
            // Process Memory
            WorkingSetBytes = workingSet,
            PrivateMemoryBytes = privateMemory,
            VirtualMemoryBytes = virtualMemory,
            PagedMemoryBytes = pagedMemory,
            NonPagedMemoryBytes = GetNonPagedMemory(),
            PeakWorkingSetBytes = _process.PeakWorkingSet64,
            PeakVirtualMemoryBytes = _process.PeakVirtualMemorySize64,
            PeakPagedMemoryBytes = _process.PeakPagedMemorySize64,
            
            // Managed Memory
            ManagedHeapBytes = gcInfo.TotalAvailableMemoryBytes,
            ManagedHeapUsedBytes = totalMemory,
            LargeObjectHeapBytes = gcInfo.TotalAvailableMemoryBytes - GetGenerationTotalSize(gcInfo),
            Gen0HeapBytes = GetGenerationSize(gcInfo, 0),
            Gen1HeapBytes = GetGenerationSize(gcInfo, 1),
            Gen2HeapBytes = GetGenerationSize(gcInfo, 2),
            AllocatedBytes = totalAllocated,
            
            // Unmanaged Memory
            UnmanagedMemoryBytes = unmanagedMemory,
            GCHandleCount = GetGCHandleCount(),
            
            // GC
            Gen0Collections = gen0Collections,
            Gen1Collections = gen1Collections,
            Gen2Collections = gen2Collections,
            CollectionsDelta = (int)collectionsDelta,
            GCFinalizationPendingCount = gcInfo.FinalizationPendingCount,
            GCCommissionedGCCount = (int)gcInfo.Index,
            GCPauseTimePercentage = gcInfo.PauseTimePercentage,
            
            // Process Stats
            ThreadCount = _process.Threads.Count,
            HandleCount = _process.HandleCount,
            
            // Averages
            AvgCpuPercentage = _cpuSamples.Any() ? _cpuSamples.Average() : 0,
            PeakMemoryBytes = _memorySamples.Any() ? _memorySamples.Max() : workingSet,
            MemoryGrowthBytes = _memorySamples.Any() ? workingSet - _memorySamples.First() : 0
        };
    }
    
    private double[]? GetLinuxPerCoreCpuUsage()
    {
        try
        {
            if (!File.Exists("/proc/stat"))
                return null;
                
            var lines = File.ReadAllLines("/proc/stat");
            var cpuLines = lines.Where(l => l.StartsWith("cpu") && char.IsDigit(l[3])).ToList();
            
            if (!cpuLines.Any())
                return null;
                
            var usages = new List<double>();
            foreach (var line in cpuLines)
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    // user, nice, system, idle = parts[1], parts[2], parts[3], parts[4]
                    if (long.TryParse(parts[1], out var user) &&
                        long.TryParse(parts[2], out var nice) &&
                        long.TryParse(parts[3], out var system) &&
                        long.TryParse(parts[4], out var idle))
                    {
                        var total = user + nice + system + idle;
                        var used = user + nice + system;
                        if (total > 0)
                        {
                            usages.Add((used / (double)total) * 100);
                        }
                    }
                }
            }
            
            return usages.ToArray();
        }
        catch
        {
            return null;
        }
    }
    
    private long GetNonPagedMemory()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows-specific: Use performance counters or WMI
                return _process.NonpagedSystemMemorySize64;
            }
        }
        catch { }
        
        return 0;
    }
    
    private long GetGCHandleCount()
    {
        // This is a best-effort approximation
        // GC.GetGCHandleCount() is not available in all .NET versions
        return 0;
    }
    
    private static long GetGenerationSize(GCMemoryInfo gcInfo, int generation)
    {
        // Get size for specific generation from GenerationInfo span
        if (generation >= 0 && generation < gcInfo.GenerationInfo.Length)
        {
            return gcInfo.GenerationInfo[generation].SizeBeforeBytes;
        }
        return 0;
    }
    
    private static long GetGenerationTotalSize(GCMemoryInfo gcInfo)
    {
        // Sum up all generation sizes
        long total = 0;
        for (int i = 0; i < gcInfo.GenerationInfo.Length; i++)
        {
            total += gcInfo.GenerationInfo[i].SizeBeforeBytes;
        }
        return total;
    }

    public void Dispose()
    {
        _process.Dispose();
    }
}

/// <summary>
/// System-level performance metrics.
/// </summary>
public record SystemPerformanceMetrics
{
    public DateTime Timestamp { get; init; }
    public TimeSpan SampleInterval { get; init; }
    
    // CPU
    public double CpuPercentage { get; init; }
    public double CpuUserPercentage { get; init; }
    public double CpuSystemPercentage { get; init; }
    public double CpuProcessTimeSeconds { get; init; }
    public double CpuUserTimeSeconds { get; init; }
    public double CpuSystemTimeSeconds { get; init; }
    public double[]? PerCoreCpuUsage { get; init; }
    public int ProcessorCount { get; init; }
    public double AvgCpuPercentage { get; init; }
    
    // Process Memory
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long VirtualMemoryBytes { get; init; }
    public long PagedMemoryBytes { get; init; }
    public long NonPagedMemoryBytes { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public long PeakVirtualMemoryBytes { get; init; }
    public long PeakPagedMemoryBytes { get; init; }
    
    // Managed Memory
    public long ManagedHeapBytes { get; init; }
    public long ManagedHeapUsedBytes { get; init; }
    public long LargeObjectHeapBytes { get; init; }
    public long Gen0HeapBytes { get; init; }
    public long Gen1HeapBytes { get; init; }
    public long Gen2HeapBytes { get; init; }
    public long AllocatedBytes { get; init; }
    
    // Unmanaged Memory
    public long UnmanagedMemoryBytes { get; init; }
    public long GCHandleCount { get; init; }
    
    // GC
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int CollectionsDelta { get; init; }
    public long GCFinalizationPendingCount { get; init; }
    public long GCCommissionedGCCount { get; init; }
    public double GCPauseTimePercentage { get; init; }
    
    // Process Stats
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    
    // Trends
    public long PeakMemoryBytes { get; init; }
    public long MemoryGrowthBytes { get; init; }
}

/// <summary>
/// TCP-based implementation of the performance counter collector with accurate system metrics.
/// </summary>
public sealed class PerformanceCounterCollector : IPerformanceCounterCollector, IAsyncDisposable
{
    private readonly ITestCommandInterface _commandInterface;
    private readonly ILogger<PerformanceCounterCollector> _logger;
    private readonly PerformanceCounterConfig _config;
    private readonly List<double> _frameTimes;
    private readonly List<double> _inputLatencies;
    private readonly SystemPerformanceCollector _systemCollector;
    private readonly object _lock = new();
    
    private bool _isCollecting;
    private DateTime _collectionStartTime;
    private string _currentTestName = string.Empty;
    private Timer? _samplingTimer;
    private PerformanceSnapshot? _lastSnapshot;
    private SystemPerformanceMetrics? _lastSystemMetrics;
    private long _initialAllocatedBytes;
    
    public bool IsAvailable => _commandInterface != null;
    public event EventHandler<PerformanceThresholdEventArgs>? ThresholdExceeded;
    
    public PerformanceCounterCollector(
        ITestCommandInterface commandInterface,
        ILogger<PerformanceCounterCollector>? logger = null,
        PerformanceCounterConfig? config = null)
    {
        _commandInterface = commandInterface ?? throw new ArgumentNullException(nameof(commandInterface));
        _logger = logger ?? new LoggerFactory().CreateLogger<PerformanceCounterCollector>();
        _config = config ?? new PerformanceCounterConfig();
        _frameTimes = new List<double>();
        _inputLatencies = new List<double>();
        _systemCollector = new SystemPerformanceCollector(logger);
    }
    
    public async Task StartCollectionAsync(string testName, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            _logger.LogWarning("Performance counters are not available");
            return;
        }
        
        lock (_lock)
        {
            if (_isCollecting)
            {
                _logger.LogWarning("Collection already in progress for test '{CurrentTest}', stopping before starting '{NewTest}'", 
                    _currentTestName, testName);
                return;
            }
            
            _isCollecting = true;
            _collectionStartTime = DateTime.UtcNow;
            _currentTestName = testName;
            _frameTimes.Clear();
            _inputLatencies.Clear();
            _initialAllocatedBytes = GC.GetTotalAllocatedBytes(false);
        }
        
        try
        {
            // Reset counters before starting
            await _commandInterface.SendCommandAsync("PERF:RESET", cancellationToken);
            
            // Start metrics collection on the app side
            var response = await _commandInterface.SendCommandAsync("PERF:START", cancellationToken);
            
            if (!response.Contains("OK"))
            {
                _logger.LogWarning("Failed to start performance collection: {Response}", response);
            }
            else
            {
                _logger.LogInformation("Started performance collection for test: {TestName}", testName);
            }
            
            // Start sampling timer if configured
            if (_config.SamplingInterval > TimeSpan.Zero)
            {
                _samplingTimer = new Timer(
                    async _ => await SampleMetricsAsync(),
                    null,
                    _config.SamplingInterval,
                    _config.SamplingInterval);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting performance collection");
            _isCollecting = false;
        }
    }
    
    public async Task<PerformanceSnapshot> StopCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || !_isCollecting)
        {
            return _lastSnapshot ?? CreateEmptySnapshot();
        }
        
        lock (_lock)
        {
            _samplingTimer?.Dispose();
            _samplingTimer = null;
            _isCollecting = false;
        }
        
        try
        {
            // Sample system metrics one final time
            var finalSystemMetrics = _systemCollector.SampleMetrics();
            
            // Stop metrics collection and get final data
            var response = await _commandInterface.SendCommandAsync("PERF:STOP", cancellationToken);
            
            var duration = DateTime.UtcNow - _collectionStartTime;
            
            // Parse the performance data from response
            var snapshot = ParsePerformanceResponse(response, _currentTestName, duration);
            
            // Add locally collected data
            snapshot = snapshot with
            {
                FrameTimeP95 = CalculatePercentile(_frameTimes, 0.95),
                FrameTimeP99 = CalculatePercentile(_frameTimes, 0.99),
                InputLatencyP95Ms = CalculatePercentile(_inputLatencies, 0.95),
                FrameTimeCount = _frameTimes.Count,
                
                // Add system-level CPU metrics
                CpuPercentage = finalSystemMetrics.CpuPercentage,
                CpuUserPercentage = finalSystemMetrics.CpuUserPercentage,
                CpuSystemPercentage = finalSystemMetrics.CpuSystemPercentage,
                CpuProcessTimeSeconds = finalSystemMetrics.CpuProcessTimeSeconds,
                CpuUserTimeSeconds = finalSystemMetrics.CpuUserTimeSeconds,
                CpuSystemTimeSeconds = finalSystemMetrics.CpuSystemTimeSeconds,
                PerCoreCpuUsage = finalSystemMetrics.PerCoreCpuUsage,
                ProcessorCount = finalSystemMetrics.ProcessorCount,
                
                // Add system-level memory metrics
                WorkingSetBytes = finalSystemMetrics.WorkingSetBytes,
                PrivateMemoryBytes = finalSystemMetrics.PrivateMemoryBytes,
                VirtualMemoryBytes = finalSystemMetrics.VirtualMemoryBytes,
                PagedMemoryBytes = finalSystemMetrics.PagedMemoryBytes,
                NonPagedMemoryBytes = finalSystemMetrics.NonPagedMemoryBytes,
                PeakWorkingSetBytes = finalSystemMetrics.PeakWorkingSetBytes,
                PeakVirtualMemoryBytes = finalSystemMetrics.PeakVirtualMemoryBytes,
                PeakPagedMemoryBytes = finalSystemMetrics.PeakPagedMemoryBytes,
                
                // Add managed memory metrics
                ManagedHeapBytes = finalSystemMetrics.ManagedHeapBytes,
                ManagedHeapUsedBytes = finalSystemMetrics.ManagedHeapUsedBytes,
                LargeObjectHeapBytes = finalSystemMetrics.LargeObjectHeapBytes,
                Gen0HeapBytes = finalSystemMetrics.Gen0HeapBytes,
                Gen1HeapBytes = finalSystemMetrics.Gen1HeapBytes,
                Gen2HeapBytes = finalSystemMetrics.Gen2HeapBytes,
                AllocatedBytes = finalSystemMetrics.AllocatedBytes - _initialAllocatedBytes,
                
                // Add unmanaged memory metrics
                UnmanagedMemoryBytes = finalSystemMetrics.UnmanagedMemoryBytes,
                GCHandleCount = finalSystemMetrics.GCHandleCount,
                
                // Add GC metrics
                Gen0Collections = finalSystemMetrics.Gen0Collections,
                Gen1Collections = finalSystemMetrics.Gen1Collections,
                Gen2Collections = finalSystemMetrics.Gen2Collections,
                GCPausePercentage = finalSystemMetrics.GCPauseTimePercentage,
                GCFinalizationPendingCount = finalSystemMetrics.GCFinalizationPendingCount,
                GCCommissionedGCCount = finalSystemMetrics.GCCommissionedGCCount,
                
                // Add process stats
                ThreadCount = finalSystemMetrics.ThreadCount,
                HandleCount = finalSystemMetrics.HandleCount
            };
            
            _lastSnapshot = snapshot;
            _lastSystemMetrics = finalSystemMetrics;
            
            _logger.LogInformation("Stopped performance collection for test: {TestName}. Duration: {DurationMs}ms", 
                _currentTestName, duration.TotalMilliseconds);
            _logger.LogInformation("Performance Summary: {Summary}", snapshot.GetCompactSummary());
            
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping performance collection");
            return _lastSnapshot ?? CreateEmptySnapshot();
        }
    }
    
    public async Task<PerformanceSnapshot> GetCurrentMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return CreateEmptySnapshot();
        }
        
        try
        {
            var response = await _commandInterface.SendCommandAsync("PERF:GET", cancellationToken);
            var duration = _isCollecting ? DateTime.UtcNow - _collectionStartTime : TimeSpan.Zero;
            
            var appMetrics = ParsePerformanceResponse(response, _currentTestName, duration);
            var systemMetrics = _systemCollector.SampleMetrics();
            
            // Merge with system metrics
            return appMetrics with
            {
                CpuPercentage = systemMetrics.CpuPercentage,
                WorkingSetBytes = systemMetrics.WorkingSetBytes,
                PrivateMemoryBytes = systemMetrics.PrivateMemoryBytes,
                ManagedHeapBytes = systemMetrics.ManagedHeapBytes,
                Gen0Collections = systemMetrics.Gen0Collections,
                Gen1Collections = systemMetrics.Gen1Collections,
                Gen2Collections = systemMetrics.Gen2Collections,
                ThreadCount = systemMetrics.ThreadCount,
                HandleCount = systemMetrics.HandleCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current metrics");
            return CreateEmptySnapshot();
        }
    }
    
    public async Task ResetCountersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return;
        }
        
        try
        {
            await _commandInterface.SendCommandAsync("PERF:RESET", cancellationToken);
            
            lock (_lock)
            {
                _frameTimes.Clear();
                _inputLatencies.Clear();
                _initialAllocatedBytes = GC.GetTotalAllocatedBytes(false);
            }
            
            _logger.LogDebug("Performance counters reset");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting performance counters");
        }
    }
    
    public async Task<PerformanceSnapshot> GetPerformanceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return CreateEmptySnapshot();
        }
        
        try
        {
            var response = await _commandInterface.SendCommandAsync("PERF:SNAPSHOT", cancellationToken);
            var duration = _isCollecting ? DateTime.UtcNow - _collectionStartTime : TimeSpan.Zero;
            
            return ParsePerformanceResponse(response, _currentTestName, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting performance snapshot");
            return CreateEmptySnapshot();
        }
    }
    
    public async Task RecordFrameTimeAsync(double frameTimeMs, CancellationToken cancellationToken = default)
    {
        if (!_isCollecting)
            return;
            
        lock (_lock)
        {
            _frameTimes.Add(frameTimeMs);
        }
        
        // Check threshold
        if (frameTimeMs > _config.MaxFrameTimeThresholdMs)
        {
            ThresholdExceeded?.Invoke(this, new PerformanceThresholdEventArgs
            {
                MetricName = "FrameTime",
                CurrentValue = frameTimeMs,
                Threshold = _config.MaxFrameTimeThresholdMs,
                TestName = _currentTestName
            });
        }
        
        await Task.CompletedTask;
    }
    
    public async Task RecordInputLatencyAsync(double latencyMs, CancellationToken cancellationToken = default)
    {
        if (!_isCollecting)
            return;
            
        lock (_lock)
        {
            _inputLatencies.Add(latencyMs);
        }
        
        // Check threshold
        if (latencyMs > _config.MaxInputLatencyThresholdMs)
        {
            ThresholdExceeded?.Invoke(this, new PerformanceThresholdEventArgs
            {
                MetricName = "InputLatency",
                CurrentValue = latencyMs,
                Threshold = _config.MaxInputLatencyThresholdMs,
                TestName = _currentTestName
            });
        }
        
        await Task.CompletedTask;
    }
    
    private async Task SampleMetricsAsync()
    {
        if (!_isCollecting)
            return;
            
        try
        {
            // Sample system metrics
            var systemMetrics = _systemCollector.SampleMetrics();
            
            // Get app metrics
            var appResponse = await _commandInterface.SendCommandAsync("PERF:GET");
            var duration = DateTime.UtcNow - _collectionStartTime;
            var appMetrics = ParsePerformanceResponse(appResponse, _currentTestName, duration);
            
            // Create merged snapshot
            var snapshot = appMetrics with
            {
                CpuPercentage = systemMetrics.CpuPercentage,
                CpuUserPercentage = systemMetrics.CpuUserPercentage,
                CpuSystemPercentage = systemMetrics.CpuSystemPercentage,
                WorkingSetBytes = systemMetrics.WorkingSetBytes,
                PrivateMemoryBytes = systemMetrics.PrivateMemoryBytes,
                ManagedHeapBytes = systemMetrics.ManagedHeapBytes,
                ThreadCount = systemMetrics.ThreadCount,
                HandleCount = systemMetrics.HandleCount
            };
            
            _lastSnapshot = snapshot;
            _lastSystemMetrics = systemMetrics;
            
            _logger.LogDebug("Sampled metrics - FPS: {Fps:F1}, CPU: {Cpu:F1}%, Memory: {MemoryMB:F1}MB", 
                snapshot.Fps, systemMetrics.CpuPercentage, systemMetrics.WorkingSetBytes / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sampling metrics");
        }
    }
    
    private PerformanceSnapshot ParsePerformanceResponse(string response, string testName, TimeSpan duration)
    {
        try
        {
            // Try to parse as JSON first
            if (response.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                
                return new PerformanceSnapshot
                {
                    TestName = testName,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow,
                    Fps = GetDoubleProperty(root, "fps"),
                    FpsMin = GetDoubleProperty(root, "fpsMin"),
                    FpsMax = GetDoubleProperty(root, "fpsMax"),
                    FpsAvg = GetDoubleProperty(root, "fpsAvg"),
                    FrameTimeMin = GetDoubleProperty(root, "frameTimeMin"),
                    FrameTimeMax = GetDoubleProperty(root, "frameTimeMax"),
                    FrameTimeAvg = GetDoubleProperty(root, "frameTimeAvg"),
                    ParserBytesPerSecond = GetDoubleProperty(root, "parserBytesPerSec"),
                    ParserSequencesPerSecond = GetDoubleProperty(root, "parserSeqPerSec"),
                    TotalBytesProcessed = GetLongProperty(root, "totalBytes"),
                    TotalSequencesProcessed = GetLongProperty(root, "totalSequences"),
                    HeapSizeBytes = GetLongProperty(root, "heapSize"),
                    AllocatedBytes = GetLongProperty(root, "allocatedBytes"),
                    WorkingSetBytes = GetLongProperty(root, "workingSet"),
                    Gen0Collections = GetIntProperty(root, "gen0"),
                    Gen1Collections = GetIntProperty(root, "gen1"),
                    Gen2Collections = GetIntProperty(root, "gen2"),
                    InputLatencyMinMs = GetDoubleProperty(root, "inputLatencyMin"),
                    InputLatencyMaxMs = GetDoubleProperty(root, "inputLatencyMax"),
                    InputLatencyAvgMs = GetDoubleProperty(root, "inputLatencyAvg"),
                    ScrollLinesPerSecond = GetDoubleProperty(root, "scrollLinesPerSec"),
                    ScrollTimeAvgMs = GetDoubleProperty(root, "scrollTimeAvg"),
                    CellUpdatesPerSecond = GetDoubleProperty(root, "cellUpdatesPerSec"),
                    TotalCellsUpdated = GetLongProperty(root, "totalCellsUpdated"),
                    RawCounters = ParseRawCounters(root.GetProperty("rawCounters"))
                };
            }
            
            // Fallback: try to parse as simple key:value pairs
            return ParseSimpleFormat(response, testName, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing performance response: {Response}", response);
            return CreateEmptySnapshot(testName, duration);
        }
    }
    
    private PerformanceSnapshot ParseSimpleFormat(string response, string testName, TimeSpan duration)
    {
        var rawCounters = new Dictionary<string, double>();
        var lines = response.Split('\n');
        
        foreach (var line in lines)
        {
            var parts = line.Split(':');
            if (parts.Length == 2 && double.TryParse(parts[1].Trim(), out var value))
            {
                rawCounters[parts[0].Trim()] = value;
            }
        }
        
        return new PerformanceSnapshot
        {
            TestName = testName,
            Duration = duration,
            Timestamp = DateTime.UtcNow,
            Fps = GetValueOrDefault(rawCounters, "fps"),
            FpsAvg = GetValueOrDefault(rawCounters, "fps_avg"),
            FrameTimeAvg = GetValueOrDefault(rawCounters, "frame_time_ms"),
            ParserBytesPerSecond = GetValueOrDefault(rawCounters, "parser_bytes_per_sec"),
            HeapSizeBytes = (long)GetValueOrDefault(rawCounters, "heap_mb") * 1024 * 1024,
            Gen0Collections = (int)GetValueOrDefault(rawCounters, "gc_gen0"),
            Gen1Collections = (int)GetValueOrDefault(rawCounters, "gc_gen1"),
            Gen2Collections = (int)GetValueOrDefault(rawCounters, "gc_gen2"),
            RawCounters = rawCounters
        };
    }
    
    private static Dictionary<string, double> ParseRawCounters(JsonElement element)
    {
        var result = new Dictionary<string, double>();
        
        if (element.ValueKind != JsonValueKind.Object)
            return result;
            
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                result[property.Name] = property.Value.GetDouble();
            }
        }
        
        return result;
    }
    
    private static double GetDoubleProperty(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetDouble();
        }
        return 0;
    }
    
    private static long GetLongProperty(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt64();
        }
        return 0;
    }
    
    private static int GetIntProperty(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32();
        }
        return 0;
    }
    
    private static double GetValueOrDefault(Dictionary<string, double> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value : 0;
    }
    
    private static double CalculatePercentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
            
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
        index = Math.Max(0, Math.Min(index, sorted.Count - 1));
        
        return sorted[index];
    }
    
    private PerformanceSnapshot CreateEmptySnapshot(string testName = "", TimeSpan? duration = null)
    {
        return new PerformanceSnapshot
        {
            TestName = testName,
            Duration = duration ?? TimeSpan.Zero,
            Timestamp = DateTime.UtcNow
        };
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_isCollecting)
        {
            await StopCollectionAsync();
        }
        
        _samplingTimer?.Dispose();
        _systemCollector.Dispose();
    }
}

/// <summary>
/// Configuration for performance counter collection.
/// </summary>
public class PerformanceCounterConfig
{
    /// <summary>
    /// Gets or sets the sampling interval for continuous collection.
    /// </summary>
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    
    /// <summary>
    /// Gets or sets the maximum frame time threshold in milliseconds.
    /// </summary>
    public double MaxFrameTimeThresholdMs { get; init; } = 33.33; // 30 FPS
    
    /// <summary>
    /// Gets or sets the maximum input latency threshold in milliseconds.
    /// </summary>
    public double MaxInputLatencyThresholdMs { get; init; } = 50;
    
    /// <summary>
    /// Gets or sets the minimum FPS threshold.
    /// </summary>
    public double MinFpsThreshold { get; init; } = 30;
    
    /// <summary>
    /// Gets or sets whether to automatically check thresholds during collection.
    /// </summary>
    public bool AutoCheckThresholds { get; init; } = true;
    
    /// <summary>
    /// Gets or sets the number of samples to keep in memory.
    /// </summary>
    public int MaxSamplesInMemory { get; init; } = 10000;
    
    /// <summary>
    /// Gets or sets the maximum CPU percentage threshold.
    /// </summary>
    public double MaxCpuPercentage { get; init; } = 80.0;
    
    /// <summary>
    /// Gets or sets the maximum working set size in bytes.
    /// </summary>
    public long MaxWorkingSetBytes { get; init; } = 512L * 1024 * 1024; // 512MB
    
    /// <summary>
    /// Gets or sets whether to collect per-core CPU usage.
    /// </summary>
    public bool CollectPerCoreCpu { get; init; } = true;
    
    /// <summary>
    /// Gets or sets whether to collect detailed memory metrics.
    /// </summary>
    public bool CollectDetailedMemory { get; init; } = true;
}
