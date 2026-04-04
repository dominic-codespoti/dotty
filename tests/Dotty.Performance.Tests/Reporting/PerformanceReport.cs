using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Dotty.Performance.Tests.Infrastructure;

namespace Dotty.Performance.Tests.Reporting;

/// <summary>
/// Generates performance reports from benchmark results
/// </summary>
public class PerformanceReport
{
    private readonly string _outputDirectory;
    private readonly BaselineComparer _baselineComparer;

    public PerformanceReport(string outputDirectory, string? baselineFile = null)
    {
        _outputDirectory = outputDirectory;
        _baselineComparer = new BaselineComparer(
            baselineFile ?? Path.Combine(outputDirectory, "baselines.json"),
            regressionThreshold: 0.10);
        
        Directory.CreateDirectory(outputDirectory);
    }

    /// <summary>
    /// Generate HTML report from BenchmarkDotNet summary
    /// </summary>
    public void GenerateHtmlReport(Summary summary, string fileName = "PerformanceReport.html")
    {
        var html = @"<!DOCTYPE html>
<html>
<head>
    <title>Dotty Terminal Emulator - Performance Report</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 40px; background: #f5f5f5; }
        .container { max-width: 1400px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
        h1 { color: #333; border-bottom: 2px solid #0078d4; padding-bottom: 10px; }
        h2 { color: #555; margin-top: 30px; }
        table { width: 100%; border-collapse: collapse; margin: 20px 0; }
        th { background: #0078d4; color: white; padding: 12px; text-align: left; }
        td { padding: 10px; border-bottom: 1px solid #eee; }
        tr:hover { background: #f8f9fa; }
        .pass { color: #28a745; font-weight: bold; }
        .fail { color: #dc3545; font-weight: bold; }
        .warning { color: #ffc107; font-weight: bold; }
        .metric { font-family: 'Consolas', monospace; }
        .summary-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 20px; margin: 20px 0; }
        .stat-card { background: #f8f9fa; padding: 20px; border-radius: 6px; border-left: 4px solid #0078d4; }
        .stat-value { font-size: 24px; font-weight: bold; color: #333; }
        .stat-label { color: #666; font-size: 14px; margin-top: 5px; }
        .timestamp { color: #999; font-size: 12px; margin-top: 20px; }
    </style>
</head>
<body>
    <div class='container'>
        <h1>Dotty Terminal Emulator - Performance Report</h1>
        <div class='timestamp'>Generated: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC") + @"</div>
        
        <div class='summary-stats'>
            <div class='stat-card'>
                <div class='stat-value'>" + summary.Reports.Length + @"</div>
                <div class='stat-label'>Benchmarks Run</div>
            </div>
            <div class='stat-card'>
                <div class='stat-value'>" + summary.TotalTime.ToString("mm\\:ss") + @"</div>
                <div class='stat-label'>Total Duration</div>
            </div>
        </div>

        <h2>Results Summary</h2>
        <table>
            <thead>
                <tr>
                    <th>Benchmark</th>
                    <th>Mean</th>
                    <th>StdDev</th>
                    <th>Throughput</th>
                    <th>Allocations</th>
                    <th>Status</th>
                </tr>
            </thead>
            <tbody>";

        foreach (var report in summary.Reports)
        {
            var result = ExtractResult(report);
            var comparison = _baselineComparer.Compare(report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo, result);
            var status = !comparison.HasBaseline ? "<span class='warning'>NEW</span>" :
                         comparison.Passed ? "<span class='pass'>PASS</span>" :
                         "<span class='fail'>FAIL</span>";

            html += $@"
                <tr>
                    <td>{report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo}</td>
                    <td class='metric'>{result.MeanMs:F3} ms</td>
                    <td class='metric'>{result.StdDevMs:F3} ms</td>
                    <td class='metric'>{result.ThroughputOpsPerSec:F0} ops/s</td>
                    <td class='metric'>{FormatBytes(result.AllocatedBytesPerOp)}</td>
                    <td>{status}</td>
                </tr>";
        }

        html += @"
            </tbody>
        </table>

        <h2>Configuration</h2>
        <table>
            <tr><th>Property</th><th>Value</th></tr>
            <tr><td>Runtime</td><td>" + BenchmarkDotNet.Environments.HostEnvironmentInfo.BenchmarkDotNetCaption + " " + summary.HostEnvironmentInfo.BenchmarkDotNetVersion + @"</td></tr>
            <tr><td>OS</td><td>" + summary.HostEnvironmentInfo.OsVersion.Value + @"</td></tr>
            <tr><td>CPU</td><td>" + summary.HostEnvironmentInfo.CpuInfo.Value.ToString() + @"</td></tr>
            <tr><td>.NET Version</td><td>" + summary.HostEnvironmentInfo.DotNetSdkVersion.Value + @"</td></tr>
        </table>
    </div>
</body>
</html>";

        var filePath = Path.Combine(_outputDirectory, fileName);
        File.WriteAllText(filePath, html);
        Console.WriteLine($"HTML report generated: {filePath}");
    }

    /// <summary>
    /// Generate JSON report for CI/CD integration
    /// </summary>
    public void GenerateJsonReport(Summary summary, string fileName = "PerformanceReport.json")
    {
        var report = new PerformanceReportData
        {
            Timestamp = DateTime.UtcNow,
            BenchmarkDotNetVersion = summary.HostEnvironmentInfo.BenchmarkDotNetVersion,
            OsVersion = summary.HostEnvironmentInfo.OsVersion.Value,
            CpuInfo = summary.HostEnvironmentInfo.CpuInfo.Value.ToString(),
            DotNetSdkVersion = summary.HostEnvironmentInfo.DotNetSdkVersion.Value,
            TotalDuration = summary.TotalTime,
            Benchmarks = summary.Reports.Select(r => new BenchmarkReportData
            {
                Name = r.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo,
                Namespace = r.BenchmarkCase.Descriptor.Type.Namespace,
                Type = r.BenchmarkCase.Descriptor.Type.Name,
                Method = r.BenchmarkCase.Descriptor.WorkloadMethod.Name,
                Parameters = r.BenchmarkCase.Parameters?.PrintInfo ?? "",
                MeanMs = r.ResultStatistics?.Mean ?? 0,
                StdDevMs = r.ResultStatistics?.StandardDeviation ?? 0,
                MinMs = r.ResultStatistics?.Min ?? 0,
                MaxMs = r.ResultStatistics?.Max ?? 0,
                Q1Ms = r.ResultStatistics?.Q1 ?? 0,
                Q3Ms = r.ResultStatistics?.Q3 ?? 0,
                P50Ms = r.ResultStatistics?.Median ?? 0,
                P95Ms = r.AllMeasurements?.Any() == true ? 
                    r.AllMeasurements.OrderBy(m => m.Nanoseconds).Skip((int)(r.AllMeasurements.Count * 0.95)).FirstOrDefault().Nanoseconds / 1000000.0 : 0,
                P99Ms = r.AllMeasurements?.Any() == true ? 
                    r.AllMeasurements.OrderBy(m => m.Nanoseconds).Skip((int)(r.AllMeasurements.Count * 0.99)).FirstOrDefault().Nanoseconds / 1000000.0 : 0,
                ThroughputOpsPerSec = r.ResultStatistics != null ? 
                    1.0 / (r.ResultStatistics.Mean / 1000.0) : 0,
                AllocatedBytesPerOp = (double)(r.GcStats.GetTotalAllocatedBytes(false) ?? 0L),
                Gen0Collections = r.GcStats.Gen0Collections,
                Gen1Collections = r.GcStats.Gen1Collections,
                Gen2Collections = r.GcStats.Gen2Collections,
                TotalOperations = r.ResultStatistics?.N ?? 0
            }).ToList()
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var filePath = Path.Combine(_outputDirectory, fileName);
        File.WriteAllText(filePath, json);
        Console.WriteLine($"JSON report generated: {filePath}");
    }

    /// <summary>
    /// Generate baseline comparison report
    /// </summary>
    public void GenerateComparisonReport(Summary summary, string fileName = "ComparisonReport.html")
    {
        var html = @"<!DOCTYPE html>
<html>
<head>
    <title>Dotty Performance - Baseline Comparison</title>
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 40px; background: #f5f5f5; }
        .container { max-width: 1400px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
        h1 { color: #333; }
        table { width: 100%; border-collapse: collapse; margin: 20px 0; }
        th { background: #0078d4; color: white; padding: 12px; text-align: left; }
        td { padding: 10px; border-bottom: 1px solid #eee; }
        .improved { color: #28a745; }
        .regressed { color: #dc3545; font-weight: bold; }
        .unchanged { color: #6c757d; }
        .metric { font-family: monospace; }
        .diff-positive::before { content: '+'; }
    </style>
</head>
<body>
    <div class='container'>
        <h1>Baseline Comparison Report</h1>
        <table>
            <thead>
                <tr>
                    <th>Benchmark</th>
                    <th>Baseline</th>
                    <th>Current</th>
                    <th>Diff</th>
                    <th>Diff %</th>
                    <th>Status</th>
                </tr>
            </thead>
            <tbody>";

        foreach (var report in summary.Reports)
        {
            var result = ExtractResult(report);
            var comparison = _baselineComparer.Compare(report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo, result);
            
            foreach (var comp in comparison.Comparisons)
            {
                var cssClass = comp.PercentageDiff < -5 ? "improved" :
                               comp.PercentageDiff > 5 ? "regressed" : "unchanged";
                
                html += $@"
                <tr>
                    <td>{report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo} - {comp.Metric}</td>
                    <td class='metric'>{comp.Baseline:F3} {comp.Unit}</td>
                    <td class='metric'>{comp.Actual:F3} {comp.Unit}</td>
                    <td class='metric {cssClass} diff-positive'>{comp.Actual - comp.Baseline:F3}</td>
                    <td class='metric {cssClass}'>{comp.PercentageDiff:F1}%</td>
                    <td>{(comp.Passed ? "PASS" : "FAIL")}</td>
                </tr>";
            }
        }

        html += @"
            </tbody>
        </table>
    </div>
</body>
</html>";

        var filePath = Path.Combine(_outputDirectory, fileName);
        File.WriteAllText(filePath, html);
        Console.WriteLine($"Comparison report generated: {filePath}");
    }

    /// <summary>
    /// Check for performance regressions
    /// </summary>
    public bool CheckRegressions(Summary summary, out List<string> regressions)
    {
        regressions = new List<string>();
        bool hasRegressions = false;

        foreach (var report in summary.Reports)
        {
            var result = ExtractResult(report);
            var comparison = _baselineComparer.Compare(report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo, result);

            if (comparison.HasBaseline && !comparison.Passed)
            {
                hasRegressions = true;
                regressions.Add($"{report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo}: {comparison.Message}");
            }
        }

        return !hasRegressions;
    }

    private BenchmarkResult ExtractResult(BenchmarkReport report)
    {
        return new BenchmarkResult
        {
            MeanMs = report.ResultStatistics?.Mean ?? 0,
            P50Ms = report.ResultStatistics?.Median ?? 0,
            P95Ms = report.AllMeasurements?.Any() == true ? 
                report.AllMeasurements.OrderBy(m => m.Nanoseconds).Skip((int)(report.AllMeasurements.Count * 0.95)).FirstOrDefault().Nanoseconds / 1000000.0 : 0,
            P99Ms = report.AllMeasurements?.Any() == true ? 
                report.AllMeasurements.OrderBy(m => m.Nanoseconds).Skip((int)(report.AllMeasurements.Count * 0.99)).FirstOrDefault().Nanoseconds / 1000000.0 : 0,
            StdDevMs = report.ResultStatistics?.StandardDeviation ?? 0,
            ThroughputOpsPerSec = report.ResultStatistics != null ? 
                1.0 / (report.ResultStatistics.Mean / 1000.0) : 0,
            AllocatedBytesPerOp = (double)(report.GcStats.GetTotalAllocatedBytes(false) ?? 0L),
            Gen0Collections = report.GcStats.Gen0Collections,
            Gen1Collections = report.GcStats.Gen1Collections,
            Gen2Collections = report.GcStats.Gen2Collections
        };
    }

    private static string FormatBytes(double bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int suffixIndex = 0;
        double value = bytes;
        
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }
        
        return $"{value:F2} {suffixes[suffixIndex]}";
    }
}

/// <summary>
/// Performance report data structure for JSON serialization
/// </summary>
public class PerformanceReportData
{
    public DateTime Timestamp { get; set; }
    public string BenchmarkDotNetVersion { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string CpuInfo { get; set; } = "";
    public string DotNetSdkVersion { get; set; } = "";
    public TimeSpan TotalDuration { get; set; }
    public List<BenchmarkReportData> Benchmarks { get; set; } = new();
}

/// <summary>
/// Individual benchmark result data
/// </summary>
public class BenchmarkReportData
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Type { get; set; } = "";
    public string Method { get; set; } = "";
    public string Parameters { get; set; } = "";
    public double MeanMs { get; set; }
    public double StdDevMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double Q1Ms { get; set; }
    public double Q3Ms { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double ThroughputOpsPerSec { get; set; }
    public double AllocatedBytesPerOp { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int TotalOperations { get; set; }
}
