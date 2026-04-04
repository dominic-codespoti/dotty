using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Interface for sending commands to the Dotty application under test.
/// </summary>
public interface ITestCommandInterface
{
    /// <summary>
    /// Sends text input to the terminal.
    /// </summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sends a keyboard key to the terminal.
    /// </summary>
    Task SendKeyAsync(string key, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sends a key combination with modifiers.
    /// </summary>
    Task SendKeyComboAsync(string key, string[] modifiers, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resizes the terminal to specified dimensions.
    /// </summary>
    Task ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sets a configuration value.
    /// </summary>
    Task SetConfigAsync(string key, string value, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the current terminal state.
    /// </summary>
    Task<TerminalState> GetStateAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Captures a screenshot of the current terminal state.
    /// </summary>
    Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Waits for the terminal to become idle (rendering complete).
    /// </summary>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Injects a raw ANSI escape sequence.
    /// </summary>
    Task InjectAnsiAsync(string sequence, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Scrolls the terminal buffer.
    /// </summary>
    Task ScrollAsync(int lines, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Copies the current selection to clipboard.
    /// </summary>
    Task CopySelectionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Pastes from clipboard.
    /// </summary>
    Task PasteClipboardAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new tab.
    /// </summary>
    Task CreateTabAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Closes the current tab.
    /// </summary>
    Task CloseTabAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Switches to the next tab.
    /// </summary>
    Task NextTabAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Switches to the previous tab.
    /// </summary>
    Task PrevTabAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets application statistics.
    /// </summary>
    Task<ApplicationStats> GetStatsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sends a raw command string to the command interface.
    /// </summary>
    Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Starts performance metrics collection.
    /// </summary>
    Task<string> StartMetricsCollectionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stops performance metrics collection and returns collected data.
    /// </summary>
    Task<string> StopMetricsCollectionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets current performance counter values.
    /// </summary>
    Task<string> GetMetricsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resets all performance counters.
    /// </summary>
    Task<string> ResetMetricsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets a full performance snapshot.
    /// </summary>
    Task<string> GetPerformanceSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Terminal state information.
/// </summary>
public record TerminalState
{
    public int CursorRow { get; init; }
    public int CursorCol { get; init; }
    public int Rows { get; init; }
    public int Cols { get; init; }
    public int ScrollbackLines { get; init; }
    public bool IsAlternateScreen { get; init; }
    public string? Title { get; init; }
    public Dictionary<string, object> Attributes { get; init; } = new();
}

/// <summary>
/// Application statistics.
/// </summary>
public record ApplicationStats
{
    public int TotalTabs { get; init; }
    public int SessionsCreated { get; init; }
    public int SessionsStarted { get; init; }
    public int MountedViews { get; init; }
    public int InactiveTimers { get; init; }
    public int Snapshots { get; init; }
    public int ActiveTabIndex { get; init; }
    public ScrollbackStats? Scrollback { get; init; }
}

/// <summary>
/// Scrollback buffer statistics.
/// </summary>
public record ScrollbackStats
{
    public int ScrollbackCount { get; init; }
    public int NonEmptyCount { get; init; }
    public string[] SampleLines { get; init; } = Array.Empty<string>();
}

/// <summary>
/// TCP-based implementation of the test command interface with retry logic and connection resilience.
/// </summary>
public sealed class TestCommandInterface : ITestCommandInterface, IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<TestCommandInterface> _logger;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private bool _isConnected;
    private bool _isDisposed;
    private int _retryCount;
    private readonly int _maxRetries = 3;
    private readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(500);
    private DateTime _lastSuccessfulCommand = DateTime.MinValue;
    
    public TestCommandInterface(int port, string host = "127.0.0.1", ILogger<TestCommandInterface>? logger = null)
    {
        _port = port;
        _host = host;
        _logger = logger ?? new LoggerFactory().CreateLogger<TestCommandInterface>();
    }
    
    /// <summary>
    /// Gets the time of the last successful command execution.
    /// </summary>
    public DateTime LastSuccessfulCommand => _lastSuccessfulCommand;
    
    /// <summary>
    /// Gets a value indicating whether the connection is healthy.
    /// </summary>
    public bool IsConnected => _isConnected && _client?.Connected == true;
    
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_isConnected && _client?.Connected == true)
            return;
            
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_isConnected && _client?.Connected == true)
                return;
                
            // Dispose old connection
            _writer?.Dispose();
            _reader?.Dispose();
            _client?.Dispose();
            
            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port);
            
            var stream = _client.GetStream();
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            _reader = new StreamReader(stream, Encoding.UTF8);
            
            _isConnected = true;
            _logger.LogDebug("Connected to command interface at {Host}:{Port}", _host, _port);
        }
        finally
        {
            _connectionLock.Release();
        }
    }
    
    /// <summary>
    /// Forces a reconnection on the next command.
    /// </summary>
    public void ForceReconnect()
    {
        _logger.LogDebug("Forcing reconnection...");
        _isConnected = false;
        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _client?.Dispose();
        }
        catch { /* Ignore cleanup errors */ }
        _writer = null;
        _reader = null;
        _client = null;
    }
    
    // Removed: duplicate SendCommandAsync, using SendCommandInternalAsync instead
    
    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var escaped = text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        await SendCommandInternalAsync($"TYPE:{escaped}", cancellationToken);
    }
    
    public async Task SendKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync($"KEY:{key}", cancellationToken);
    }
    
    public async Task SendKeyComboAsync(string key, string[] modifiers, CancellationToken cancellationToken = default)
    {
        var modStr = string.Join("+", modifiers);
        await SendCommandInternalAsync($"KEYCOMBO:{modStr}+{key}", cancellationToken);
    }
    
    public async Task ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync($"RESIZE:{cols}:{rows}", cancellationToken);
    }
    
    public async Task SetConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync($"SETCONFIG:{key}:{value}", cancellationToken);
    }
    
    public async Task<TerminalState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandInternalAsync("GET_STATE", cancellationToken);
        
        // Parse JSON response
        try
        {
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            return new TerminalState
            {
                CursorRow = root.GetProperty("cursorRow").GetInt32(),
                CursorCol = root.GetProperty("cursorCol").GetInt32(),
                Rows = root.GetProperty("rows").GetInt32(),
                Cols = root.GetProperty("cols").GetInt32(),
                ScrollbackLines = root.TryGetProperty("scrollbackLines", out var sb) ? sb.GetInt32() : 0,
                IsAlternateScreen = root.TryGetProperty("isAlternateScreen", out var alt) && alt.GetBoolean(),
                Title = root.TryGetProperty("title", out var title) ? title.GetString() : null
            };
        }
        catch
        {
            // Return default state if parsing fails
            return new TerminalState();
        }
    }
    
    public async Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("SCREENSHOT", cancellationToken);
        
        // Read binary image data
        if (_client?.Connected != true)
            return Array.Empty<byte>();
            
        var stream = _client.GetStream();
        
        // Read length prefix (4 bytes)
        var lengthBytes = new byte[4];
        await stream.ReadAsync(lengthBytes.AsMemory(0, 4), cancellationToken);
        var length = BitConverter.ToInt32(lengthBytes, 0);
        
        if (length <= 0 || length > 50 * 1024 * 1024) // Max 50MB
            return Array.Empty<byte>();
            
        // Read image data
        var imageData = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = await stream.ReadAsync(
                imageData.AsMemory(read, length - read), 
                cancellationToken);
            if (chunk == 0)
                break;
            read += chunk;
        }
        
        return imageData;
    }
    
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("WAIT_FOR_IDLE", cancellationToken);
    }
    
    public async Task InjectAnsiAsync(string sequence, CancellationToken cancellationToken = default)
    {
        var escaped = Convert.ToBase64String(Encoding.UTF8.GetBytes(sequence));
        await SendCommandInternalAsync($"INJECT_ANSI:{escaped}", cancellationToken);
    }
    
    public async Task ScrollAsync(int lines, CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync($"SCROLL:{lines}", cancellationToken);
    }
    
    public async Task CopySelectionAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("COPY", cancellationToken);
    }
    
    public async Task PasteClipboardAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("PASTE", cancellationToken);
    }
    
    public async Task CreateTabAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("NEW_TAB", cancellationToken);
    }
    
    public async Task CloseTabAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("CLOSE_TAB", cancellationToken);
    }
    
    public async Task NextTabAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("NEXT_TAB", cancellationToken);
    }
    
    public async Task PrevTabAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandInternalAsync("PREV_TAB", cancellationToken);
    }
    
    public async Task<ApplicationStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandInternalAsync("STATS", cancellationToken);
        
        try
        {
            return JsonSerializer.Deserialize<ApplicationStats>(response) ?? new ApplicationStats();
        }
        catch
        {
            return new ApplicationStats();
        }
    }
    
    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        return await SendCommandInternalAsync(command, cancellationToken);
    }
    
    public async Task<string> StartMetricsCollectionAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandInternalAsync("PERF:START", cancellationToken);
    }
    
    public async Task<string> StopMetricsCollectionAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandInternalAsync("PERF:STOP", cancellationToken);
    }
    
    public async Task<string> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandInternalAsync("PERF:GET", cancellationToken);
    }
    
    public async Task<string> ResetMetricsAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandInternalAsync("PERF:RESET", cancellationToken);
    }
    
    public async Task<string> GetPerformanceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await SendCommandInternalAsync("PERF:SNAPSHOT", cancellationToken);
    }
    
    private async Task<string> SendCommandInternalAsync(string command, CancellationToken cancellationToken)
    {
        var attempt = 0;
        Exception? lastException = null;
        
        while (attempt < _maxRetries)
        {
            attempt++;
            
            try
            {
                await EnsureConnectedAsync(cancellationToken);
                
                _logger.LogDebug("Sending command (attempt {Attempt}/{MaxRetries}): {Command}", attempt, _maxRetries, command);
                
                // Check if client is still connected before writing
                if (_client?.Connected != true || _writer == null)
                {
                    _logger.LogWarning("Connection lost before writing, forcing reconnect...");
                    ForceReconnect();
                    continue;
                }
                
                await _writer.WriteLineAsync(command.ToString());
                
                // Read response with timeout
                var responseTask = _reader!.ReadLineAsync();
                var timeoutTask = Task.Delay(10000, cancellationToken);
                
                var completedTask = await Task.WhenAny(responseTask, timeoutTask);
                if (completedTask == timeoutTask)
                    throw new TimeoutException("Command response timeout");
                    
                var response = await responseTask ?? "OK";
                _logger.LogDebug("Received response: {Response}", response);
                
                // Mark as successful
                _lastSuccessfulCommand = DateTime.UtcNow;
                _retryCount = 0;
                
                return response;
            }
            catch (IOException ex) when (ex.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) || 
                                         ex.Message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
            {
                lastException = ex;
                _logger.LogWarning("Connection broken (attempt {Attempt}/{MaxRetries}): {Message}. Retrying...", 
                    attempt, _maxRetries, ex.Message);
                
                // Force reconnection and retry
                ForceReconnect();
                
                if (attempt < _maxRetries)
                {
                    await Task.Delay(_retryDelay * attempt, cancellationToken);
                }
            }
            catch (ObjectDisposedException ex)
            {
                lastException = ex;
                _logger.LogWarning("Connection disposed (attempt {Attempt}/{MaxRetries}): {Message}. Retrying...", 
                    attempt, _maxRetries, ex.Message);
                
                // Force reconnection and retry
                ForceReconnect();
                
                if (attempt < _maxRetries)
                {
                    await Task.Delay(_retryDelay * attempt, cancellationToken);
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase))
            {
                lastException = ex;
                _logger.LogWarning("Not connected (attempt {Attempt}/{MaxRetries}): {Message}. Retrying...", 
                    attempt, _maxRetries, ex.Message);
                
                // Force reconnection and retry
                ForceReconnect();
                
                if (attempt < _maxRetries)
                {
                    await Task.Delay(_retryDelay * attempt, cancellationToken);
                }
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                lastException = ex;
                _logger.LogWarning("Command failed (attempt {Attempt}/{MaxRetries}): {Message}. Retrying...", 
                    attempt, _maxRetries, ex.Message);
                
                // Force reconnection and retry
                ForceReconnect();
                await Task.Delay(_retryDelay * attempt, cancellationToken);
            }
        }
        
        // All retries exhausted
        _retryCount = attempt;
        throw new InvalidOperationException(
            $"Command failed after {attempt} attempts: {command}. " +
            $"Last error: {lastException?.Message}", lastException);
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;
            
        _isDisposed = true;
        
        // Signal that we're disposing to prevent new operations
        _isConnected = false;
        
        try
        {
            _writer?.Dispose();
        }
        catch { /* Ignore */ }
        
        try
        {
            _reader?.Dispose();
        }
        catch { /* Ignore */ }
        
        try
        {
            _client?.Dispose();
        }
        catch { /* Ignore */ }
        
        try
        {
            _connectionLock.Dispose();
        }
        catch { /* Ignore */ }
    }
}
