using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Manages performance baselines for E2E tests.
/// Stores, retrieves, and compares performance snapshots against historical data.
/// </summary>
public sealed class PerformanceBaselineManager
{
    private readonly string _baselinesDirectory;
    private readonly ILogger<PerformanceBaselineManager> _logger;
    private readonly Dictionary<string, BaselineEntry> _cache;
    private readonly object _lock = new();
    
    /// <summary>
    /// Creates a new performance baseline manager.
    /// </summary>
    public PerformanceBaselineManager(string baselinesDirectory, ILogger<PerformanceBaselineManager>? logger = null)
    {
        _baselinesDirectory = baselinesDirectory ?? throw new ArgumentNullException(nameof(baselinesDirectory));
        _logger = logger ?? new LoggerFactory().CreateLogger<PerformanceBaselineManager>();
        _cache = new Dictionary<string, BaselineEntry>();
        
        // Ensure directory exists
        Directory.CreateDirectory(_baselinesDirectory);
        
        _logger.LogInformation("PerformanceBaselineManager initialized at: {Path}", _baselinesDirectory);
    }
    
    /// <summary>
    /// Records a new baseline for a test.
    /// </summary>
    public async Task RecordBaselineAsync(string testName, PerformanceSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(testName))
            throw new ArgumentException("Test name cannot be empty", nameof(testName));
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        
        var entry = new BaselineEntry
        {
            TestName = testName,
            BaselineSnapshot = snapshot,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            RecordedBy = Environment.UserName,
            Environment = GetEnvironmentInfo(),
            Version = GetAppVersion()
        };
        
        var filePath = GetBaselineFilePath(testName);
        
        lock (_lock)
        {
            _cache[testName] = entry;
        }
        
        var json = JsonSerializer.Serialize(entry, BaselineJsonContext.Default.BaselineEntry);
        await File.WriteAllTextAsync(filePath, json);
        
        _logger.LogInformation("Recorded baseline for test '{TestName}' at: {Path}", testName, filePath);
    }
    
    /// <summary>
    /// Gets the baseline for a test, if one exists.
    /// </summary>
    public async Task<PerformanceSnapshot?> GetBaselineAsync(string testName)
    {
        if (string.IsNullOrWhiteSpace(testName))
            return null;
        
        // Check cache first
        lock (_lock)
        {
            if (_cache.TryGetValue(testName, out var cached))
            {
                return cached.BaselineSnapshot;
            }
        }
        
        var filePath = GetBaselineFilePath(testName);
        
        if (!File.Exists(filePath))
        {
            _logger.LogDebug("No baseline found for test '{TestName}'", testName);
            return null;
        }
        
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var entry = JsonSerializer.Deserialize(json, BaselineJsonContext.Default.BaselineEntry);
            
            if (entry == null)
            {
                _logger.LogWarning("Failed to deserialize baseline for test '{TestName}'", testName);
                return null;
            }
            
            // Update cache
            lock (_lock)
            {
                _cache[testName] = entry;
            }
            
            return entry.BaselineSnapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading baseline for test '{TestName}'", testName);
            return null;
        }
    }
    
    /// <summary>
    /// Compares current performance against the baseline.
    /// </summary>
    public async Task<PerformanceComparison> CompareToBaselineAsync(string testName, PerformanceSnapshot currentSnapshot)
    {
        var baselineSnapshot = await GetBaselineAsync(testName);
        
        if (baselineSnapshot == null)
        {
            return new PerformanceComparison
            {
                HasBaseline = false,
                CurrentSnapshot = currentSnapshot
            };
        }
        
        // Calculate deltas
        var fpsDelta = CalculateDeltaPercentage(baselineSnapshot.Fps, currentSnapshot.Fps);
        var frameTimeDelta = CalculateDeltaPercentage(baselineSnapshot.FrameTimeP95, currentSnapshot.FrameTimeP95);
        var memoryDelta = CalculateDeltaPercentage(baselineSnapshot.HeapSizeBytes, currentSnapshot.HeapSizeBytes);
        var parserDelta = CalculateDeltaPercentage(baselineSnapshot.ParserBytesPerSecond, currentSnapshot.ParserBytesPerSecond);
        var latencyDelta = CalculateDeltaPercentage(baselineSnapshot.InputLatencyP95Ms, currentSnapshot.InputLatencyP95Ms);
        
        var comparison = new PerformanceComparison
        {
            HasBaseline = true,
            BaselineSnapshot = baselineSnapshot,
            CurrentSnapshot = currentSnapshot,
            FpsDeltaPercentage = fpsDelta,
            FrameTimeDeltaPercentage = frameTimeDelta,
            MemoryDeltaPercentage = memoryDelta,
            ParserThroughputDeltaPercentage = parserDelta,
            LatencyDeltaPercentage = latencyDelta
        };
        
        _logger.LogInformation(
            "Performance comparison for '{TestName}': FPS {FpsDelta:F1}%, FrameTime {FrameTimeDelta:F1}%, Memory {MemoryDelta:F1}%",
            testName, fpsDelta, frameTimeDelta, memoryDelta);
        
        return comparison;
    }
    
    /// <summary>
    /// Updates an existing baseline with new data.
    /// </summary>
    public async Task UpdateBaselineAsync(string testName, PerformanceSnapshot newSnapshot, string? reason = null)
    {
        var existingBaseline = await GetBaselineAsync(testName);
        
        var entry = new BaselineEntry
        {
            TestName = testName,
            BaselineSnapshot = newSnapshot,
            PreviousSnapshot = existingBaseline,
            CreatedAt = existingBaseline?.Timestamp ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            RecordedBy = Environment.UserName,
            UpdateReason = reason,
            Environment = GetEnvironmentInfo(),
            Version = GetAppVersion()
        };
        
        var filePath = GetBaselineFilePath(testName);
        
        lock (_lock)
        {
            _cache[testName] = entry;
        }
        
        var json = JsonSerializer.Serialize(entry, BaselineJsonContext.Default.BaselineEntry);
        await File.WriteAllTextAsync(filePath, json);
        
        _logger.LogInformation("Updated baseline for test '{TestName}' (reason: {Reason})", testName, reason ?? "none provided");
    }
    
    /// <summary>
    /// Deletes a baseline for a test.
    /// </summary>
    public Task DeleteBaselineAsync(string testName)
    {
        var filePath = GetBaselineFilePath(testName);
        
        lock (_lock)
        {
            _cache.Remove(testName);
        }
        
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted baseline for test '{TestName}'", testName);
        }
        
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Lists all available baselines.
    /// </summary>
    public IEnumerable<string> ListBaselineNames()
    {
        if (!Directory.Exists(_baselinesDirectory))
            return Enumerable.Empty<string>();
        
        return Directory.GetFiles(_baselinesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>();
    }
    
    /// <summary>
    /// Generates a delta report comparing multiple tests.
    /// </summary>
    public async Task<PerformanceDeltaReport> GenerateDeltaReportAsync(
        Dictionary<string, PerformanceSnapshot> currentResults,
        double regressionTolerancePercentage = 10.0)
    {
        var results = new List<PerformanceComparison>();
        var regressions = new List<PerformanceComparison>();
        var improvements = new List<PerformanceComparison>();
        var newTests = new List<string>();
        
        foreach (var (testName, currentSnapshot) in currentResults)
        {
            var comparison = await CompareToBaselineAsync(testName, currentSnapshot);
            results.Add(comparison);
            
            if (!comparison.HasBaseline)
            {
                newTests.Add(testName);
            }
            else
            {
                // Check for regression (FPS down OR frame time up OR memory up significantly)
                bool isRegression = comparison.FpsDeltaPercentage < -regressionTolerancePercentage ||
                                    comparison.FrameTimeDeltaPercentage > regressionTolerancePercentage ||
                                    comparison.MemoryDeltaPercentage > regressionTolerancePercentage * 2;
                
                // Check for improvement (FPS up OR frame time down)
                bool isImprovement = comparison.FpsDeltaPercentage > regressionTolerancePercentage ||
                                     comparison.FrameTimeDeltaPercentage < -regressionTolerancePercentage;
                
                if (isRegression)
                    regressions.Add(comparison);
                else if (isImprovement)
                    improvements.Add(comparison);
            }
        }
        
        return new PerformanceDeltaReport
        {
            GeneratedAt = DateTime.UtcNow,
            TotalTests = results.Count,
            NewTests = newTests,
            Regressions = regressions,
            Improvements = improvements,
            AllComparisons = results,
            TolerancePercentage = regressionTolerancePercentage,
            Summary = GenerateReportSummary(results, regressions, improvements, newTests)
        };
    }
    
    /// <summary>
    /// Clears the baseline cache.
    /// </summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
        _logger.LogDebug("Baseline cache cleared");
    }
    
    /// <summary>
    /// Exports all baselines to a single JSON file.
    /// </summary>
    public async Task<string> ExportBaselinesAsync(string? exportPath = null)
    {
        exportPath ??= Path.Combine(_baselinesDirectory, $"baselines_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        
        var allBaselines = new Dictionary<string, BaselineEntry>();
        
        foreach (var testName in ListBaselineNames())
        {
            var baseline = await GetBaselineEntryAsync(testName);
            if (baseline != null)
            {
                allBaselines[testName] = baseline;
            }
        }
        
        var export = new BaselineExport
        {
            ExportedAt = DateTime.UtcNow,
            Baselines = allBaselines,
            TotalCount = allBaselines.Count
        };
        
        var json = JsonSerializer.Serialize(export, BaselineJsonContext.Default.BaselineExport);
        await File.WriteAllTextAsync(exportPath, json);
        
        _logger.LogInformation("Exported {Count} baselines to: {Path}", allBaselines.Count, exportPath);
        
        return exportPath;
    }
    
    /// <summary>
    /// Imports baselines from an export file.
    /// </summary>
    public async Task<int> ImportBaselinesAsync(string importPath, bool overwriteExisting = false)
    {
        if (!File.Exists(importPath))
        {
            _logger.LogWarning("Import file not found: {Path}", importPath);
            return 0;
        }
        
        try
        {
            var json = await File.ReadAllTextAsync(importPath);
            var export = JsonSerializer.Deserialize(json, BaselineJsonContext.Default.BaselineExport);
            
            if (export?.Baselines == null)
            {
                _logger.LogWarning("Failed to deserialize import file: {Path}", importPath);
                return 0;
            }
            
            int imported = 0;
            foreach (var (testName, entry) in export.Baselines)
            {
                var filePath = GetBaselineFilePath(testName);
                
                if (File.Exists(filePath) && !overwriteExisting)
                {
                    _logger.LogDebug("Skipping existing baseline: {TestName}", testName);
                    continue;
                }
                
                var updatedEntry = entry with { UpdatedAt = DateTime.UtcNow };
                var entryJson = JsonSerializer.Serialize(updatedEntry, BaselineJsonContext.Default.BaselineEntry);
                await File.WriteAllTextAsync(filePath, entryJson);
                
                lock (_lock)
                {
                    _cache[testName] = updatedEntry;
                }
                
                imported++;
            }
            
            _logger.LogInformation("Imported {Imported}/{Total} baselines from: {Path}", 
                imported, export.Baselines.Count, importPath);
            
            return imported;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing baselines from: {Path}", importPath);
            return 0;
        }
    }
    
    private async Task<BaselineEntry?> GetBaselineEntryAsync(string testName)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(testName, out var cached))
            {
                return cached;
            }
        }
        
        var filePath = GetBaselineFilePath(testName);
        
        if (!File.Exists(filePath))
            return null;
        
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var entry = JsonSerializer.Deserialize(json, BaselineJsonContext.Default.BaselineEntry);
            
            if (entry != null)
            {
                lock (_lock)
                {
                    _cache[testName] = entry;
                }
            }
            
            return entry;
        }
        catch
        {
            return null;
        }
    }
    
    private string GetBaselineFilePath(string testName)
    {
        // Sanitize test name for file system
        var safeName = string.Join("_", testName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_baselinesDirectory, $"{safeName}.json");
    }
    
    private static double CalculateDeltaPercentage(double baseline, double current)
    {
        if (baseline == 0)
            return current == 0 ? 0 : double.PositiveInfinity;
        
        return ((current - baseline) / baseline) * 100.0;
    }
    
    private static string GetEnvironmentInfo()
    {
        return $"{Environment.OSVersion.Platform}-{Environment.ProcessorCount}cores";
    }
    
    private static string GetAppVersion()
    {
        return typeof(PerformanceBaselineManager).Assembly.GetName().Version?.ToString() ?? "unknown";
    }
    
    private static string GenerateReportSummary(
        List<PerformanceComparison> all,
        List<PerformanceComparison> regressions,
        List<PerformanceComparison> improvements,
        List<string> newTests)
    {
        var lines = new List<string>
        {
            $"Performance Delta Report Summary",
            $"================================",
            $"",
            $"Total Tests: {all.Count}",
            $"  - New (no baseline): {newTests.Count}",
            $"  - Regressions: {regressions.Count}",
            $"  - Improvements: {improvements.Count}",
            $"  - Unchanged: {all.Count - newTests.Count - regressions.Count - improvements.Count}",
            $""
        };
        
        if (regressions.Count > 0)
        {
            lines.Add("Regressions:");
            foreach (var r in regressions)
            {
                lines.Add($"  - {r.CurrentSnapshot?.TestName}: " +
                    $"FPS {r.FpsDeltaPercentage:F1}%, " +
                    $"FrameTime {r.FrameTimeDeltaPercentage:F1}%");
            }
            lines.Add("");
        }
        
        if (improvements.Count > 0)
        {
            lines.Add("Improvements:");
            foreach (var i in improvements)
            {
                lines.Add($"  - {i.CurrentSnapshot?.TestName}: " +
                    $"FPS {i.FpsDeltaPercentage:F1}%, " +
                    $"FrameTime {i.FrameTimeDeltaPercentage:F1}%");
            }
        }
        
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Entry for a stored performance baseline.
/// </summary>
public record BaselineEntry
{
    public string TestName { get; init; } = string.Empty;
    public PerformanceSnapshot BaselineSnapshot { get; init; } = new();
    public PerformanceSnapshot? PreviousSnapshot { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string RecordedBy { get; init; } = string.Empty;
    public string? UpdateReason { get; init; }
    public string Environment { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
}

/// <summary>
/// Report containing performance delta analysis.
/// </summary>
public class PerformanceDeltaReport
{
    public DateTime GeneratedAt { get; init; }
    public int TotalTests { get; init; }
    public List<string> NewTests { get; init; } = new();
    public List<PerformanceComparison> Regressions { get; init; } = new();
    public List<PerformanceComparison> Improvements { get; init; } = new();
    public List<PerformanceComparison> AllComparisons { get; init; } = new();
    public double TolerancePercentage { get; init; }
    public string Summary { get; init; } = string.Empty;
    
    /// <summary>
    /// Gets whether there are any regressions.
    /// </summary>
    public bool HasRegressions => Regressions.Count > 0;
    
    /// <summary>
    /// Gets whether there are any improvements.
    /// </summary>
    public bool HasImprovements => Improvements.Count > 0;
    
    /// <summary>
    /// Gets a formatted report.
    /// </summary>
    public string GetFormattedReport()
    {
        return Summary;
    }
    
    /// <summary>
    /// Serializes the report to JSON.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, BaselineJsonContext.Default.PerformanceDeltaReport);
    }
}

/// <summary>
/// Export container for multiple baselines.
/// </summary>
public record BaselineExport
{
    public DateTime ExportedAt { get; init; }
    public Dictionary<string, BaselineEntry> Baselines { get; init; } = new();
    public int TotalCount { get; init; }
}

/// <summary>
/// JSON serialization context for baseline types.
/// </summary>
[JsonSerializable(typeof(BaselineEntry))]
[JsonSerializable(typeof(BaselineExport))]
[JsonSerializable(typeof(PerformanceDeltaReport))]
[JsonSerializable(typeof(PerformanceSnapshot))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public partial class BaselineJsonContext : JsonSerializerContext
{
}
