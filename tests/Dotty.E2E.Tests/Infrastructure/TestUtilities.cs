using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Captures screenshots for debugging test failures.
/// </summary>
public sealed class ScreenshotCapture
{
    private readonly string _outputDirectory;
    private readonly ILogger<ScreenshotCapture> _logger;
    
    public ScreenshotCapture(string? outputDirectory = null, ILogger<ScreenshotCapture>? logger = null)
    {
        _outputDirectory = outputDirectory ?? Path.Combine("artifacts", "screenshots");
        _logger = logger ?? new LoggerFactory().CreateLogger<ScreenshotCapture>();
        
        Directory.CreateDirectory(_outputDirectory);
    }
    
    /// <summary>
    /// Captures and saves a screenshot.
    /// </summary>
    public async Task<string> CaptureAsync(byte[] imageData, string testName, string? suffix = null)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{testName}_{timestamp}";
        
        if (!string.IsNullOrEmpty(suffix))
            fileName += $"_{suffix}";
            
        fileName += ".png";
        
        var filePath = Path.Combine(_outputDirectory, fileName);
        
        await File.WriteAllBytesAsync(filePath, imageData);
        
        _logger.LogInformation("Screenshot saved to: {Path}", filePath);
        
        return filePath;
    }
    
    /// <summary>
    /// Captures a screenshot from a WriteableBitmap.
    /// </summary>
    public async Task<string> CaptureAsync(WriteableBitmap bitmap, string testName, string? suffix = null)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return await CaptureAsync(stream.ToArray(), testName, suffix);
    }
}

/// <summary>
/// Dumps terminal state for debugging.
/// </summary>
public sealed class StateDumper
{
    private readonly string _outputDirectory;
    private readonly ILogger<StateDumper> _logger;
    
    public StateDumper(string? outputDirectory = null, ILogger<StateDumper>? logger = null)
    {
        _outputDirectory = outputDirectory ?? Path.Combine("artifacts", "states");
        _logger = logger ?? new LoggerFactory().CreateLogger<StateDumper>();
        
        Directory.CreateDirectory(_outputDirectory);
    }
    
    /// <summary>
    /// Dumps terminal state to a file.
    /// </summary>
    public async Task<string> DumpStateAsync(TerminalState state, string[] screenLines, string testName)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{testName}_{timestamp}_state.txt";
        var filePath = Path.Combine(_outputDirectory, fileName);
        
        using var writer = new StreamWriter(filePath);
        
        await writer.WriteLineAsync("=== Terminal State ===");
        await writer.WriteLineAsync($"Cursor: Row={state.CursorRow}, Col={state.CursorCol}");
        await writer.WriteLineAsync($"Dimensions: {state.Cols}x{state.Rows}");
        await writer.WriteLineAsync($"Scrollback Lines: {state.ScrollbackLines}");
        await writer.WriteLineAsync($"Is Alternate Screen: {state.IsAlternateScreen}");
        await writer.WriteLineAsync($"Title: {state.Title}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("=== Screen Content ===");
        
        for (int i = 0; i < screenLines.Length; i++)
        {
            await writer.WriteLineAsync($"[{i:D3}] |{screenLines[i]}|");
        }
        
        _logger.LogInformation("State dumped to: {Path}", filePath);
        
        return filePath;
    }
    
    /// <summary>
    /// Dumps raw ANSI sequence for debugging.
    /// </summary>
    public async Task<string> DumpAnsiSequenceAsync(string sequence, string testName)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{testName}_{timestamp}_ansi.txt";
        var filePath = Path.Combine(_outputDirectory, fileName);
        
        await File.WriteAllTextAsync(filePath, sequence);
        
        _logger.LogInformation("ANSI sequence dumped to: {Path}", filePath);
        
        return filePath;
    }
}

/// <summary>
/// Comprehensive test logger.
/// </summary>
public sealed class TestLogger
{
    private readonly string _logDirectory;
    private readonly ILogger<TestLogger> _logger;
    private readonly List<string> _logBuffer;
    private readonly object _lock = new();
    
    public TestLogger(string? logDirectory = null, ILogger<TestLogger>? logger = null)
    {
        _logDirectory = logDirectory ?? Path.Combine("artifacts", "logs");
        _logger = logger ?? new LoggerFactory().CreateLogger<TestLogger>();
        _logBuffer = new List<string>();
        
        Directory.CreateDirectory(_logDirectory);
    }
    
    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public void Log(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss.fff}] INFO: {message}";
        
        lock (_lock)
        {
            _logBuffer.Add(entry);
        }
        
        _logger.LogInformation(message);
    }
    
    /// <summary>
    /// Logs a debug message.
    /// </summary>
    public void LogDebug(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss.fff}] DEBUG: {message}";
        
        lock (_lock)
        {
            _logBuffer.Add(entry);
        }
        
        _logger.LogDebug(message);
    }
    
    /// <summary>
    /// Logs an error message.
    /// </summary>
    public void LogError(string message, Exception? ex = null)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss.fff}] ERROR: {message}";
        
        if (ex != null)
            entry += $"\nException: {ex}";
            
        lock (_lock)
        {
            _logBuffer.Add(entry);
        }
        
        _logger.LogError(ex, message);
    }
    
    /// <summary>
    /// Flushes buffered logs to a file.
    /// </summary>
    public async Task FlushAsync(string testName)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"{testName}_{timestamp}.log";
        var filePath = Path.Combine(_logDirectory, fileName);
        
        string[] logs;
        lock (_lock)
        {
            logs = _logBuffer.ToArray();
            _logBuffer.Clear();
        }
        
        await File.WriteAllLinesAsync(filePath, logs);
        
        _logger.LogInformation("Test log saved to: {Path}", filePath);
    }
}
