using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Generates comprehensive performance reports for E2E tests.
/// Supports HTML and JSON output formats with trend analysis.
/// </summary>
public sealed class E2EPerformanceReport
{
    private readonly string _outputDirectory;
    private readonly ILogger<E2EPerformanceReport> _logger;
    private readonly List<PerformanceSnapshot> _snapshots;
    private readonly List<PerformanceComparison> _comparisons;
    
    /// <summary>
    /// Creates a new performance report generator.
    /// </summary>
    public E2EPerformanceReport(string outputDirectory, ILogger<E2EPerformanceReport>? logger = null)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        _logger = logger ?? new LoggerFactory().CreateLogger<E2EPerformanceReport>();
        _snapshots = new List<PerformanceSnapshot>();
        _comparisons = new List<PerformanceComparison>();
        
        Directory.CreateDirectory(_outputDirectory);
        
        _logger.LogInformation("E2EPerformanceReport initialized at: {Path}", _outputDirectory);
    }
    
    /// <summary>
    /// Adds a performance snapshot to the report.
    /// </summary>
    public void AddSnapshot(PerformanceSnapshot snapshot)
    {
        _snapshots.Add(snapshot);
        _logger.LogDebug("Added snapshot for test: {TestName}", snapshot.TestName);
    }
    
    /// <summary>
    /// Adds multiple snapshots to the report.
    /// </summary>
    public void AddSnapshots(IEnumerable<PerformanceSnapshot> snapshots)
    {
        _snapshots.AddRange(snapshots);
        _logger.LogDebug("Added {Count} snapshots", snapshots.Count());
    }
    
    /// <summary>
    /// Adds a comparison result to the report.
    /// </summary>
    public void AddComparison(PerformanceComparison comparison)
    {
        _comparisons.Add(comparison);
        _logger.LogDebug("Added comparison for test: {TestName}", comparison.CurrentSnapshot?.TestName);
    }
    
    /// <summary>
    /// Generates a comprehensive HTML performance report.
    /// </summary>
    public async Task<string> GenerateHtmlReportAsync(string? reportName = null)
    {
        reportName ??= $"performance_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.html";
        var filePath = Path.Combine(_outputDirectory, reportName);
        
        var html = BuildHtmlReport();
        await File.WriteAllTextAsync(filePath, html);
        
        _logger.LogInformation("Generated HTML performance report: {Path}", filePath);
        
        return filePath;
    }
    
    /// <summary>
    /// Generates a JSON performance report.
    /// </summary>
    public async Task<string> GenerateJsonReportAsync(string? reportName = null)
    {
        reportName ??= $"performance_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(_outputDirectory, reportName);
        
        var report = BuildJsonReport();
        var json = JsonSerializer.Serialize(report, ReportJsonContext.Default.PerformanceReport);
        await File.WriteAllTextAsync(filePath, json);
        
        _logger.LogInformation("Generated JSON performance report: {Path}", filePath);
        
        return filePath;
    }
    
    /// <summary>
    /// Generates a trend report comparing snapshots over time.
    /// </summary>
    public async Task<string> GenerateTrendReportAsync(string testName, IEnumerable<PerformanceSnapshot> historicalData, 
        string format = "html")
    {
        var fileName = $"trend_{testName}_{DateTime.UtcNow:yyyyMMdd}.html";
        var filePath = Path.Combine(_outputDirectory, fileName);
        
        var data = historicalData.OrderBy(s => s.Timestamp).ToList();
        
        if (format.ToLowerInvariant() == "html")
        {
            var html = BuildTrendHtml(testName, data);
            await File.WriteAllTextAsync(filePath, html);
        }
        else
        {
            var json = JsonSerializer.Serialize(data, ReportJsonContext.Default.ListPerformanceSnapshot);
            filePath = filePath.Replace(".html", ".json");
            await File.WriteAllTextAsync(filePath, json);
        }
        
        _logger.LogInformation("Generated trend report for '{TestName}': {Path}", testName, filePath);
        
        return filePath;
    }
    
    /// <summary>
    /// Generates a regression report highlighting performance changes.
    /// </summary>
    public async Task<string> GenerateRegressionReportAsync(double tolerancePercentage = 10.0)
    {
        var fileName = $"regression_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.html";
        var filePath = Path.Combine(_outputDirectory, fileName);
        
        var regressions = _comparisons
            .Where(c => c.HasBaseline && (
                c.FpsDeltaPercentage < -tolerancePercentage ||
                c.FrameTimeDeltaPercentage > tolerancePercentage ||
                c.MemoryDeltaPercentage > tolerancePercentage * 2))
            .ToList();
        
        var improvements = _comparisons
            .Where(c => c.HasBaseline && (
                c.FpsDeltaPercentage > tolerancePercentage ||
                c.FrameTimeDeltaPercentage < -tolerancePercentage))
            .ToList();
        
        var html = BuildRegressionHtml(regressions, improvements, tolerancePercentage);
        await File.WriteAllTextAsync(filePath, html);
        
        _logger.LogInformation("Generated regression report: {Path} (Regressions: {Regressions}, Improvements: {Improvements})",
            filePath, regressions.Count, improvements.Count);
        
        return filePath;
    }
    
    /// <summary>
    /// Clears all collected data.
    /// </summary>
    public void Clear()
    {
        _snapshots.Clear();
        _comparisons.Clear();
        _logger.LogDebug("Cleared all report data");
    }
    
    private string BuildHtmlReport()
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("    <meta charset=\"UTF-8\">");
        sb.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("    <title>Dotty E2E Performance Report</title>");
        sb.AppendLine("    <style>");
        sb.AppendLine(GetCssStyles());
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("    <div class=\"container\">");
        sb.AppendLine("        <h1>Dotty E2E Performance Report</h1>");
        sb.AppendLine($"        <p class=\"timestamp\">Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>");
        
        // Summary section
        sb.AppendLine("        <div class=\"section\">");
        sb.AppendLine("            <h2>Summary</h2>");
        sb.AppendLine(BuildSummarySection());
        sb.AppendLine("        </div>");
        
        // Snapshots section
        if (_snapshots.Count > 0)
        {
            sb.AppendLine("        <div class=\"section\">");
            sb.AppendLine("            <h2>Performance Snapshots</h2>");
            sb.AppendLine("            <table class=\"data-table\">");
            sb.AppendLine("                <thead>");
            sb.AppendLine("                    <tr>");
            sb.AppendLine("                        <th>Test Name</th>");
            sb.AppendLine("                        <th>Duration</th>");
            sb.AppendLine("                        <th>FPS</th>");
            sb.AppendLine("                        <th>Frame Time (p95)</th>");
            sb.AppendLine("                        <th>Parser (KB/s)</th>");
            sb.AppendLine("                        <th>Memory (MB)</th>");
            sb.AppendLine("                        <th>Status</th>");
            sb.AppendLine("                    </tr>");
            sb.AppendLine("                </thead>");
            sb.AppendLine("                <tbody>");
            
            foreach (var snapshot in _snapshots.OrderBy(s => s.TestName))
            {
                var status = DetermineStatus(snapshot);
                sb.AppendLine($"                    <tr class=\"{status.ToLower()}\">");
                sb.AppendLine($"                        <td>{EscapeHtml(snapshot.TestName)}</td>");
                sb.AppendLine($"                        <td>{snapshot.Duration.TotalSeconds:F2}s</td>");
                sb.AppendLine($"                        <td>{snapshot.Fps:F1}</td>");
                sb.AppendLine($"                        <td>{snapshot.FrameTimeP95:F2}ms</td>");
                sb.AppendLine($"                        <td>{snapshot.ParserBytesPerSecond / 1024:F1}</td>");
                sb.AppendLine($"                        <td>{snapshot.HeapSizeBytes / 1024 / 1024:F1}</td>");
                sb.AppendLine($"                        <td class=\"status-{status.ToLower()}\">{status}</td>");
                sb.AppendLine("                    </tr>");
            }
            
            sb.AppendLine("                </tbody>");
            sb.AppendLine("            </table>");
            sb.AppendLine("        </div>");
        }
        
        // Comparisons section
        if (_comparisons.Count > 0)
        {
            sb.AppendLine("        <div class=\"section\">");
            sb.AppendLine("            <h2>Baseline Comparisons</h2>");
            sb.AppendLine("            <table class=\"data-table\">");
            sb.AppendLine("                <thead>");
            sb.AppendLine("                    <tr>");
            sb.AppendLine("                        <th>Test Name</th>");
            sb.AppendLine("                        <th>Baseline FPS</th>");
            sb.AppendLine("                        <th>Current FPS</th>");
            sb.AppendLine("                        <th>FPS Change</th>");
            sb.AppendLine("                        <th>Frame Time Change</th>");
            sb.AppendLine("                        <th>Memory Change</th>");
            sb.AppendLine("                    </tr>");
            sb.AppendLine("                </thead>");
            sb.AppendLine("                <tbody>");
            
            foreach (var comparison in _comparisons.Where(c => c.HasBaseline).OrderBy(c => c.CurrentSnapshot?.TestName))
            {
                var fpsClass = GetDeltaClass(comparison.FpsDeltaPercentage, true);
                var frameTimeClass = GetDeltaClass(comparison.FrameTimeDeltaPercentage, false);
                var memoryClass = GetDeltaClass(comparison.MemoryDeltaPercentage, false, 20.0);
                
                sb.AppendLine($"                    <tr>");
                sb.AppendLine($"                        <td>{EscapeHtml(comparison.CurrentSnapshot?.TestName ?? "Unknown")}</td>");
                sb.AppendLine($"                        <td>{comparison.BaselineSnapshot?.Fps:F1}</td>");
                sb.AppendLine($"                        <td>{comparison.CurrentSnapshot?.Fps:F1}</td>");
                sb.AppendLine($"                        <td class=\"{fpsClass}\">{comparison.FpsDeltaPercentage:F1}%</td>");
                sb.AppendLine($"                        <td class=\"{frameTimeClass}\">{comparison.FrameTimeDeltaPercentage:F1}%</td>");
                sb.AppendLine($"                        <td class=\"{memoryClass}\">{comparison.MemoryDeltaPercentage:F1}%</td>");
                sb.AppendLine("                    </tr>");
            }
            
            sb.AppendLine("                </tbody>");
            sb.AppendLine("            </table>");
            sb.AppendLine("        </div>");
        }
        
        // Charts section (placeholder for JavaScript charts)
        sb.AppendLine("        <div class=\"section\">");
        sb.AppendLine("            <h2>Performance Charts</h2>");
        sb.AppendLine(BuildChartsSection());
        sb.AppendLine("        </div>");
        
        sb.AppendLine("    </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        
        return sb.ToString();
    }
    
    private string BuildSummarySection()
    {
        var totalTests = _snapshots.Count;
        var testsWithBaselines = _comparisons.Count(c => c.HasBaseline);
        var regressions = _comparisons.Count(c => c.HasBaseline && c.FpsDeltaPercentage < -10);
        var improvements = _comparisons.Count(c => c.HasBaseline && c.FpsDeltaPercentage > 10);
        
        var avgFps = _snapshots.Count > 0 ? _snapshots.Average(s => s.Fps) : 0;
        var avgFrameTime = _snapshots.Count > 0 ? _snapshots.Average(s => s.FrameTimeP95) : 0;
        
        return $"""
            <div class="summary-grid">
                <div class="summary-card">
                    <h3>Total Tests</h3>
                    <p class="big-number">{totalTests}</p>
                </div>
                <div class="summary-card">
                    <h3>With Baselines</h3>
                    <p class="big-number">{testsWithBaselines}</p>
                </div>
                <div class="summary-card">
                    <h3>Regressions</h3>
                    <p class="big-number warning">{regressions}</p>
                </div>
                <div class="summary-card">
                    <h3>Improvements</h3>
                    <p class="big-number success">{improvements}</p>
                </div>
                <div class="summary-card">
                    <h3>Avg FPS</h3>
                    <p class="big-number">{avgFps:F1}</p>
                </div>
                <div class="summary-card">
                    <h3>Avg Frame Time (p95)</h3>
                    <p class="big-number">{avgFrameTime:F1}ms</p>
                </div>
            </div>
        """;
    }
    
    private string BuildChartsSection()
    {
        // Generate simple SVG charts
        var fpsChart = BuildFpsChart();
        var frameTimeChart = BuildFrameTimeChart();
        
        return $"""
            <div class="charts-grid">
                <div class="chart-container">
                    <h3>FPS Distribution</h3>
                    {fpsChart}
                </div>
                <div class="chart-container">
                    <h3>Frame Time Distribution</h3>
                    {frameTimeChart}
                </div>
            </div>
        """;
    }
    
    private string BuildFpsChart()
    {
        if (_snapshots.Count == 0)
            return "<p>No data available</p>";
        
        var values = _snapshots.Select(s => s.Fps).ToList();
        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        if (range == 0) range = 1;
        
        var bars = new StringBuilder();
        var barWidth = 100.0 / values.Count;
        
        for (int i = 0; i < values.Count; i++)
        {
            var height = ((values[i] - min) / range) * 100;
            var color = values[i] >= 60 ? "#4CAF50" : values[i] >= 30 ? "#FFC107" : "#F44336";
            bars.AppendLine($"<rect x=\"{i * barWidth}%\" y=\"{100 - height}%\" width=\"{barWidth * 0.9}%\" height=\"{height}%\" fill=\"{color}\" />");
        }
        
        return $"""
            <svg viewBox="0 0 100 100" preserveAspectRatio="none" class="bar-chart">
                {bars}
            </svg>
        """;
    }
    
    private string BuildFrameTimeChart()
    {
        if (_snapshots.Count == 0)
            return "<p>No data available</p>";
        
        var values = _snapshots.Select(s => s.FrameTimeP95).ToList();
        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        if (range == 0) range = 1;
        
        var bars = new StringBuilder();
        var barWidth = 100.0 / values.Count;
        
        for (int i = 0; i < values.Count; i++)
        {
            var height = ((values[i] - min) / range) * 100;
            var color = values[i] <= 16.67 ? "#4CAF50" : values[i] <= 33.33 ? "#FFC107" : "#F44336";
            bars.AppendLine($"<rect x=\"{i * barWidth}%\" y=\"{100 - height}%\" width=\"{barWidth * 0.9}%\" height=\"{height}%\" fill=\"{color}\" />");
        }
        
        return $"""
            <svg viewBox="0 0 100 100" preserveAspectRatio="none" class="bar-chart">
                {bars}
            </svg>
        """;
    }
    
    private string BuildTrendHtml(string testName, List<PerformanceSnapshot> data)
    {
        // Similar to BuildHtmlReport but focused on a single test's history
        return $"""
            <!DOCTYPE html>
            <html>
            <head>
                <title>Performance Trend: {EscapeHtml(testName)}</title>
                <style>{GetCssStyles()}</style>
            </head>
            <body>
                <div class="container">
                    <h1>Performance Trend: {EscapeHtml(testName)}</h1>
                    <p>Data points: {data.Count}</p>
                    <table class="data-table">
                        <thead>
                            <tr>
                                <th>Date</th>
                                <th>FPS</th>
                                <th>Frame Time (p95)</th>
                                <th>Memory (MB)</th>
                            </tr>
                        </thead>
                        <tbody>
                            {string.Join("\n", data.Select(d => $"""
                                <tr>
                                    <td>{d.Timestamp:yyyy-MM-dd HH:mm}</td>
                                    <td>{d.Fps:F1}</td>
                                    <td>{d.FrameTimeP95:F2}ms</td>
                                    <td>{d.HeapSizeBytes / 1024 / 1024:F1}</td>
                                </tr>
                            """))}
                        </tbody>
                    </table>
                </div>
            </body>
            </html>
        """;
    }
    
    private string BuildRegressionHtml(List<PerformanceComparison> regressions, List<PerformanceComparison> improvements, double tolerance)
    {
        return $"""
            <!DOCTYPE html>
            <html>
            <head>
                <title>Performance Regression Report</title>
                <style>{GetCssStyles()}</style>
            </head>
            <body>
                <div class="container">
                    <h1>Performance Regression Report</h1>
                    <p class="timestamp">Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
                    <p>Tolerance: ±{tolerance}%</p>
                    
                    <h2>Regressions ({regressions.Count})</h2>
                    {BuildComparisonTable(regressions)}
                    
                    <h2>Improvements ({improvements.Count})</h2>
                    {BuildComparisonTable(improvements)}
                </div>
            </body>
            </html>
        """;
    }
    
    private string BuildComparisonTable(List<PerformanceComparison> comparisons)
    {
        if (comparisons.Count == 0)
            return "<p>None</p>";
        
        return $"""
            <table class="data-table">
                <thead>
                    <tr>
                        <th>Test</th>
                        <th>FPS Change</th>
                        <th>Frame Time Change</th>
                        <th>Memory Change</th>
                    </tr>
                </thead>
                <tbody>
                    {string.Join("\n", comparisons.Select(c => $"""
                        <tr>
                            <td>{EscapeHtml(c.CurrentSnapshot?.TestName ?? "Unknown")}</td>
                            <td class="{GetDeltaClass(c.FpsDeltaPercentage, true)}">{c.FpsDeltaPercentage:F1}%</td>
                            <td class="{GetDeltaClass(c.FrameTimeDeltaPercentage, false)}">{c.FrameTimeDeltaPercentage:F1}%</td>
                            <td class="{GetDeltaClass(c.MemoryDeltaPercentage, false)}">{c.MemoryDeltaPercentage:F1}%</td>
                        </tr>
                    """))}
                </tbody>
            </table>
        """;
    }
    
    private PerformanceReport BuildJsonReport()
    {
        return new PerformanceReport
        {
            GeneratedAt = DateTime.UtcNow,
            Snapshots = _snapshots.ToList(),
            Comparisons = _comparisons.ToList(),
            Summary = new PerformanceSummary
            {
                TotalTests = _snapshots.Count,
                TestsWithBaselines = _comparisons.Count(c => c.HasBaseline),
                AverageFps = _snapshots.Count > 0 ? _snapshots.Average(s => s.Fps) : 0,
                AverageFrameTimeP95 = _snapshots.Count > 0 ? _snapshots.Average(s => s.FrameTimeP95) : 0,
                Regressions = _comparisons.Count(c => c.HasBaseline && c.FpsDeltaPercentage < -10),
                Improvements = _comparisons.Count(c => c.HasBaseline && c.FpsDeltaPercentage > 10)
            }
        };
    }
    
    private static string DetermineStatus(PerformanceSnapshot snapshot)
    {
        if (snapshot.Fps >= 60 && snapshot.FrameTimeP95 <= 16.67)
            return "Good";
        if (snapshot.Fps >= 30 && snapshot.FrameTimeP95 <= 33.33)
            return "Fair";
        return "Poor";
    }
    
    private static string GetDeltaClass(double delta, bool higherIsBetter, double threshold = 10.0)
    {
        var significant = Math.Abs(delta) > threshold;
        if (!significant)
            return "neutral";
        
        var isGood = higherIsBetter ? delta > 0 : delta < 0;
        return isGood ? "good" : "bad";
    }
    
    private static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
    
    private static string GetCssStyles()
    {
        return """
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; }
            .container { max-width: 1400px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); }
            h1 { color: #333; border-bottom: 3px solid #4CAF50; padding-bottom: 10px; }
            h2 { color: #555; margin-top: 30px; }
            .timestamp { color: #888; font-style: italic; }
            .section { margin-bottom: 30px; }
            .summary-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 15px; margin: 20px 0; }
            .summary-card { background: #f8f9fa; padding: 15px; border-radius: 6px; text-align: center; }
            .summary-card h3 { margin: 0 0 10px 0; font-size: 14px; color: #666; }
            .big-number { font-size: 32px; font-weight: bold; color: #333; margin: 0; }
            .big-number.success { color: #4CAF50; }
            .big-number.warning { color: #FF9800; }
            .big-number.error { color: #F44336; }
            .data-table { width: 100%; border-collapse: collapse; margin: 20px 0; }
            .data-table th { background: #4CAF50; color: white; padding: 12px; text-align: left; }
            .data-table td { padding: 10px 12px; border-bottom: 1px solid #ddd; }
            .data-table tr:hover { background: #f5f5f5; }
            .good { color: #4CAF50; font-weight: bold; }
            .bad { color: #F44336; font-weight: bold; }
            .neutral { color: #757575; }
            .status-good { color: #4CAF50; font-weight: bold; }
            .status-fair { color: #FF9800; font-weight: bold; }
            .status-poor { color: #F44336; font-weight: bold; }
            .charts-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); gap: 30px; margin: 20px 0; }
            .chart-container { background: #f8f9fa; padding: 20px; border-radius: 6px; }
            .bar-chart { width: 100%; height: 200px; }
        """;
    }
}

/// <summary>
/// Complete performance report data structure.
/// </summary>
public class PerformanceReport
{
    public DateTime GeneratedAt { get; init; }
    public List<PerformanceSnapshot> Snapshots { get; init; } = new();
    public List<PerformanceComparison> Comparisons { get; init; } = new();
    public PerformanceSummary Summary { get; init; } = new();
}

/// <summary>
/// Performance summary statistics.
/// </summary>
public class PerformanceSummary
{
    public int TotalTests { get; init; }
    public int TestsWithBaselines { get; init; }
    public double AverageFps { get; init; }
    public double AverageFrameTimeP95 { get; init; }
    public int Regressions { get; init; }
    public int Improvements { get; init; }
}

/// <summary>
/// JSON serialization context for report types.
/// </summary>
[JsonSerializable(typeof(PerformanceReport))]
[JsonSerializable(typeof(PerformanceSummary))]
[JsonSerializable(typeof(List<PerformanceSnapshot>))]
public partial class ReportJsonContext : JsonSerializerContext
{
}
