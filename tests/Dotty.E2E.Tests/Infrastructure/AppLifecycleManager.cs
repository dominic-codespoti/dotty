using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Manages the lifecycle of the Dotty application process for E2E testing.
/// Handles process start/stop, health monitoring, auto-restart, and cleanup.
/// </summary>
public sealed class AppLifecycleManager : IAsyncDisposable
{
    private readonly ILogger<AppLifecycleManager> _logger;
    private Process? _process;
    private CancellationTokenSource? _healthCheckCts;
    private CancellationTokenSource? _startupCts;
    private readonly object _lock = new();
    private int _commandPort;
    private bool _isDisposed;
    private int _restartCount;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _healthCheckInterval;
    private readonly bool _autoRestart;
    private readonly string _appExecutablePath;
    private readonly bool _headless;
    private readonly bool _useXvfb;
    private readonly string _xvfbDisplay;
    private readonly string _xvfbScreen;
    
    /// <summary>
    /// Event raised when the application process exits.
    /// </summary>
    public event EventHandler<ProcessExitEventArgs>? ProcessExited;
    
    /// <summary>
    /// Event raised when the application becomes ready (responsive on command port).
    /// </summary>
    public event EventHandler? ApplicationReady;
    
    /// <summary>
    /// Event raised when the application fails to start.
    /// </summary>
    public event EventHandler<StartupFailureEventArgs>? StartupFailed;
    
    /// <summary>
    /// Gets the port used for the test command interface.
    /// </summary>
    public int CommandPort => _commandPort;
    
    /// <summary>
    /// Gets a value indicating whether the application is running.
    /// </summary>
    public bool IsRunning 
    { 
        get 
        { 
            lock (_lock)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }
    
    /// <summary>
    /// Gets the process ID of the running application.
    /// </summary>
    public int? ProcessId
    {
        get
        {
            lock (_lock)
            {
                return _process?.Id;
            }
        }
    }
    
    /// <summary>
    /// Gets the number of times the application has been restarted.
    /// </summary>
    public int RestartCount => _restartCount;
    
    /// <summary>
    /// Gets the executable path being used to run the application.
    /// </summary>
    public string AppExecutablePath => _appExecutablePath;

    /// <summary>
    /// Creates a new app lifecycle manager with the specified configuration.
    /// </summary>
    public AppLifecycleManager(
        string appExecutablePath,
        bool headless = true,
        TimeSpan? startupTimeout = null,
        TimeSpan? healthCheckInterval = null,
        bool autoRestart = false,
        ILogger<AppLifecycleManager>? logger = null,
        bool useXvfb = true,
        string xvfbDisplay = ":99",
        string xvfbScreen = "1024x768x24")
    {
        _appExecutablePath = appExecutablePath ?? throw new ArgumentNullException(nameof(appExecutablePath));
        _headless = headless;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        _healthCheckInterval = healthCheckInterval ?? TimeSpan.FromSeconds(5);
        _autoRestart = autoRestart;
        _logger = logger ?? new LoggerFactory().CreateLogger<AppLifecycleManager>();
        _useXvfb = useXvfb && headless && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        _xvfbDisplay = xvfbDisplay;
        _xvfbScreen = xvfbScreen;
        
        // Validate the executable path
        if (!File.Exists(appExecutablePath) && !IsDotnetCommand(appExecutablePath))
        {
            throw new FileNotFoundException($"Application executable not found: {appExecutablePath}");
        }
        
        _logger.LogInformation("AppLifecycleManager created: Headless={Headless}, UseXvfb={UseXvfb}, Display={Display}", 
            _headless, _useXvfb, _xvfbDisplay);
    }

    /// <summary>
    /// Starts the Dotty application.
    /// </summary>
    public async Task StartAsync(
        int? commandPort = null,
        string? arguments = null,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(AppLifecycleManager));

        lock (_lock)
        {
            if (IsRunning)
                throw new InvalidOperationException("Application is already running");
        }

        _logger.LogInformation("Starting Dotty application for E2E testing");
        _logger.LogInformation("Executable: {Path}", _appExecutablePath);

        // Find or use the specified command port
        _commandPort = commandPort ?? FindAvailablePort();
        _logger.LogInformation("Using test command port: {Port}", _commandPort);

        // Create cancellation token source for startup
        _startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await StartProcessAsync(arguments, environmentVariables, _startupCts.Token);
            await WaitForReadyAsync(_startupTimeout, _startupCts.Token);
            
            _logger.LogInformation("Dotty application started successfully (PID: {Pid})", ProcessId);
            
            ApplicationReady?.Invoke(this, EventArgs.Empty);
            
            // Start health monitoring if enabled
            if (_autoRestart)
            {
                StartHealthMonitoring();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start application");
            StartupFailed?.Invoke(this, new StartupFailureEventArgs(ex));
            throw;
        }
    }

    /// <summary>
    /// Stops the application gracefully with force kill fallback.
    /// </summary>
    public async Task StopAsync(TimeSpan? gracefulTimeout = null, CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            return;

        _logger.LogInformation("Stopping Dotty application");

        // Stop health monitoring
        _healthCheckCts?.Cancel();
        _startupCts?.Cancel();

        Process? processToKill = null;
        lock (_lock)
        {
            processToKill = _process;
            _process = null;
        }

        if (processToKill == null || processToKill.HasExited)
        {
            _logger.LogInformation("Application already stopped");
            return;
        }

        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(5);

        // Try graceful shutdown first
        try
        {
            _logger.LogDebug("Attempting graceful shutdown...");
            
            // Send shutdown command via TCP if possible
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _commandPort);
                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync("SHUTDOWN");
            }
            catch
            {
                // Command interface not available, try CloseMainWindow
            }

            // Try CloseMainWindow (for GUI apps)
            if (!processToKill.HasExited)
            {
                try
                {
                    processToKill.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // Process might not have a main window
                }
            }

            // Wait for graceful exit
            using var gracefulCts = new CancellationTokenSource(timeout);
            try
            {
                await processToKill.WaitForExitAsync(gracefulCts.Token);
                _logger.LogInformation("Application stopped gracefully");
                return;
            }
            catch (OperationCanceledException)
            {
                // Timeout, continue to force kill
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful shutdown failed");
        }

        // Force kill if still running
        if (!processToKill.HasExited)
        {
            try
            {
                _logger.LogWarning("Force killing process (PID: {Pid})...", processToKill.Id);
                processToKill.Kill();
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await processToKill.WaitForExitAsync(killCts.Token);
                _logger.LogInformation("Process killed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Process did not exit within 5 seconds after kill signal");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to kill process");
            }
        }

        processToKill.Dispose();
    }

    /// <summary>
    /// Restarts the application.
    /// </summary>
    public async Task RestartAsync(
        int? commandPort = null,
        string? arguments = null,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restarting application...");
        _restartCount++;
        
        await StopAsync(cancellationToken: cancellationToken);
        await Task.Delay(1000, cancellationToken); // Brief pause between stop/start
        await StartAsync(commandPort, arguments, environmentVariables, cancellationToken);
    }

    /// <summary>
    /// Sends a command to the running application via TCP with health check and auto-restart.
    /// </summary>
    public async Task<string> SendCommandAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("Application is not running");

        // Check if process has actually exited
        lock (_lock)
        {
            if (_process?.HasExited == true)
            {
                _logger.LogWarning("Process has exited unexpectedly (exit code: {ExitCode})", _process.ExitCode);
                throw new InvalidOperationException($"Application process has exited with code {_process.ExitCode}");
            }
        }

        var commandTimeout = timeout ?? TimeSpan.FromSeconds(10);
        
        // Track consecutive failures for auto-restart decision
        var consecutiveFailures = 0;
        const int maxConsecutiveFailures = 3;
        
        while (consecutiveFailures < maxConsecutiveFailures)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _commandPort, cancellationToken);
                
                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                
                await writer.WriteLineAsync(command.ToString());
                
                // Read response with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(commandTimeout);
                
                try
                {
                    var response = await reader.ReadLineAsync(cts.Token);
                    return response ?? "OK";
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Command response timeout after {commandTimeout.TotalSeconds}s");
                }
            }
            catch (IOException ex) when (ex.Message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) || 
                                         ex.Message.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
            {
                consecutiveFailures++;
                _logger.LogWarning("Broken pipe error (failure {ConsecutiveFailures}/{MaxFailures}): {Message}", 
                    consecutiveFailures, maxConsecutiveFailures, ex.Message);
                
                // Check process status
                lock (_lock)
                {
                    if (_process?.HasExited == true)
                    {
                        _logger.LogError("Application process has crashed (exit code: {ExitCode})", _process.ExitCode);
                        
                        if (_autoRestart && consecutiveFailures < maxConsecutiveFailures)
                        {
                            _logger.LogInformation("Auto-restart enabled, attempting to restart application...");
                            // Trigger restart outside of lock
                            break;
                        }
                        
                        throw new InvalidOperationException(
                            $"Application crashed with exit code {_process.ExitCode}. " +
                            "Last error: " + ex.Message, ex);
                    }
                }
                
                // Brief delay before retry
                if (consecutiveFailures < maxConsecutiveFailures)
                {
                    await Task.Delay(500 * consecutiveFailures, cancellationToken);
                }
            }
            catch (Exception ex) when (consecutiveFailures < maxConsecutiveFailures - 1)
            {
                consecutiveFailures++;
                _logger.LogWarning("Command failed (failure {ConsecutiveFailures}/{MaxFailures}): {Message}. Retrying...", 
                    consecutiveFailures, maxConsecutiveFailures, ex.Message);
                await Task.Delay(500 * consecutiveFailures, cancellationToken);
            }
        }
        
        // Check if we should auto-restart
        if (_autoRestart)
        {
            _logger.LogWarning("Auto-restarting application after {ConsecutiveFailures} consecutive failures...", consecutiveFailures);
            await RestartAsync(cancellationToken: cancellationToken);
            
            // Retry the command once after restart
            return await SendCommandAsync(command, timeout, cancellationToken);
        }
        
        throw new InvalidOperationException(
            $"Command failed after {consecutiveFailures} attempts. " +
            "The application may be unresponsive or crashed.");
    }

    /// <summary>
    /// Checks if the application is responsive by sending a ping command.
    /// Includes process liveness check and connection health validation.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        // First check if process is still alive
        lock (_lock)
        {
            if (_process == null || _process.HasExited)
            {
                _logger.LogDebug("Health check failed: Process is not running");
                return false;
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3)); // Quick timeout for health check
            
            var response = await SendCommandAsync("STATS", TimeSpan.FromSeconds(2), cts.Token);
            
            // Validate response is not empty and looks like JSON
            if (string.IsNullOrWhiteSpace(response) || !response.StartsWith("{"))
            {
                _logger.LogDebug("Health check failed: Invalid response format");
                return false;
            }
            
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Health check failed: Operation cancelled (timeout)");
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogDebug("Health check failed: IO error - {Message}", ex.Message);
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exited"))
        {
            _logger.LogDebug("Health check failed: Process has exited");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Health check failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Kills any orphaned Dotty processes that might be running from previous test runs.
    /// </summary>
    public static void KillOrphanedProcesses(string processName = "Dotty.App")
    {
        try
        {
            var currentProcessId = Environment.ProcessId;
            Process[] processes;
            
            // Get processes with a timeout to avoid hanging
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                // If we can't enumerate processes, just continue
                return;
            }
            
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id != currentProcessId && !process.HasExited)
                    {
                        process.Kill();
                        // Short wait for process to exit
                        process.WaitForExit(2000);
                    }
                }
                catch (Exception)
                {
                    // Ignore errors for individual processes
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Ignore all errors during cleanup
        }
    }

    private async Task StartProcessAsync(
        string? arguments,
        Dictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo(arguments, environmentVariables);
        
        lock (_lock)
        {
            _process = new Process { StartInfo = startInfo };
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;
        }

        // Attach to output streams
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        _process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                outputBuilder.AppendLine(e.Data);
                _logger.LogDebug("[Dotty stdout] {Data}", e.Data);
            }
        };

        _process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                errorBuilder.AppendLine(e.Data);
                _logger.LogDebug("[Dotty stderr] {Data}", e.Data);
            }
        };

        _logger.LogInformation("Starting process...");
        
        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start process");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Wait a moment for process to initialize
        await Task.Delay(100, cancellationToken);

        if (_process.HasExited)
        {
            var exitCode = _process.ExitCode;
            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            
            throw new InvalidOperationException(
                $"Process exited immediately with code {exitCode}. " +
                $"Output: {output}. Error: {error}");
        }
    }

    private ProcessStartInfo CreateProcessStartInfo(
        string? arguments,
        Dictionary<string, string>? environmentVariables)
    {
        var isDotnetCommand = IsDotnetCommand(_appExecutablePath);
        
        // Determine if we should use xvfb-run
        var useXvfbRun = _useXvfb && IsXvfbAvailable();
        
        string fileName;
        string processArguments;
        
        if (useXvfbRun)
        {
            // Wrap the command with xvfb-run
            fileName = "xvfb-run";
            var xvfbArgs = $"--server-args=\"-screen 0 {_xvfbScreen}\" --auto-servernum";
            
            if (isDotnetCommand)
            {
                processArguments = $"{xvfbArgs} dotnet \"{_appExecutablePath}\" {arguments ?? ""}".Trim();
            }
            else
            {
                processArguments = $"{xvfbArgs} \"{_appExecutablePath}\" {arguments ?? ""}".Trim();
            }
            
            _logger.LogInformation("Using xvfb-run for headless execution: {Args}", processArguments);
        }
        else
        {
            // Normal execution without xvfb-run
            if (isDotnetCommand)
            {
                fileName = "dotnet";
                processArguments = $"\"{_appExecutablePath}\" {arguments ?? ""}".Trim();
            }
            else
            {
                fileName = _appExecutablePath;
                processArguments = arguments ?? "";
            }
        }
        
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = processArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = _headless,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };

        // Set environment variables
        startInfo.EnvironmentVariables["DOTTY_TEST_PORT"] = _commandPort.ToString();
        startInfo.EnvironmentVariables["DOTTY_E2E_MODE"] = "1";
        
        // Headless mode configuration
        // NOTE: When using Xvfb, we DON'T set AVALONIA_HEADLESS because that prevents
        // the MainWindow from being created, which prevents the test command listener from starting.
        // Instead, we rely on Xvfb to provide a virtual display while the app runs normally.
        if (_headless)
        {
            // Only use Avalonia headless mode if we're NOT using Xvfb
            // Xvfb provides a real display, so we want normal window creation
            if (!_useXvfb)
            {
                startInfo.EnvironmentVariables["AVALONIA_HEADLESS"] = "1";
            }
            startInfo.EnvironmentVariables["DOTTY_HEADLESS"] = "1";
            
            // Set DISPLAY if not using xvfb-run (which handles this automatically)
            if (!useXvfbRun)
            {
                startInfo.EnvironmentVariables["DISPLAY"] = _xvfbDisplay;
            }
        }

        // Add any additional environment variables
        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        return startInfo;
    }
    
    /// <summary>
    /// Checks if xvfb-run is available on the system.
    /// </summary>
    private static bool IsXvfbAvailable()
    {
        try
        {
            // Check if xvfb-run exists in PATH
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "xvfb-run",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Waiting for application to become ready (timeout: {Timeout}s)...", timeout.TotalSeconds);
        
        var startTime = DateTime.UtcNow;
        var lastError = "";
        var attemptCount = 0;
        var processExited = false;
        int exitCode = -1;
        
        while (DateTime.UtcNow - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptCount++;
            
            // Check if process is still alive
            lock (_lock)
            {
                if (_process?.HasExited == true)
                {
                    processExited = true;
                    exitCode = _process.ExitCode;
                    break;
                }
            }

            // Try to connect to command interface
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _commandPort);
                
                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                
                await writer.WriteLineAsync("STATS");
                var response = await reader.ReadLineAsync();
                
                if (!string.IsNullOrEmpty(response) && response.StartsWith("{"))
                {
                    _logger.LogInformation("Application is ready (after {Attempts} attempts)", attemptCount);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                // Not ready yet, wait and retry
                await Task.Delay(100, cancellationToken);
            }
        }
        
        if (processExited)
        {
            throw new InvalidOperationException(
                $"Process exited unexpectedly during startup with exit code {exitCode}. " +
                "The application may have crashed. Check application logs for details.");
        }
        
        throw new TimeoutException(
            $"Application failed to start within {timeout.TotalSeconds} seconds. " +
            $"Last error: {lastError}");
    }

    private void StartHealthMonitoring()
    {
        _healthCheckCts = new CancellationTokenSource();
        var consecutiveFailures = 0;
        const int maxConsecutiveFailures = 3;
        
        _ = Task.Run(async () =>
        {
            while (!_healthCheckCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_healthCheckInterval, _healthCheckCts.Token);
                    
                    // Perform health check
                    var isHealthy = await IsHealthyAsync(_healthCheckCts.Token);
                    
                    if (!isHealthy)
                    {
                        consecutiveFailures++;
                        _logger.LogWarning(
                            "Health check failed ({ConsecutiveFailures}/{MaxFailures})", 
                            consecutiveFailures, maxConsecutiveFailures);
                        
                        if (consecutiveFailures >= maxConsecutiveFailures)
                        {
                            _logger.LogError(
                                "Application unresponsive after {MaxFailures} consecutive health check failures", 
                                maxConsecutiveFailures);
                            
                            if (_autoRestart && !_isDisposed)
                            {
                                _logger.LogWarning("Auto-restart enabled, restarting application...");
                                try
                                {
                                    await RestartAsync(cancellationToken: _healthCheckCts.Token);
                                    consecutiveFailures = 0; // Reset counter after successful restart
                                    _logger.LogInformation("Application restarted successfully");
                                }
                                catch (Exception restartEx)
                                {
                                    _logger.LogError(restartEx, "Failed to restart application");
                                }
                            }
                        }
                    }
                    else
                    {
                        // Reset counter on successful health check
                        if (consecutiveFailures > 0)
                        {
                            _logger.LogDebug("Health check passed, resetting failure counter");
                            consecutiveFailures = 0;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Health monitoring error");
                    // Brief pause before next iteration to avoid tight error loops
                    await Task.Delay(1000, _healthCheckCts.Token).ContinueWith(_ => { });
                }
            }
        });
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode = -1;
        lock (_lock)
        {
            if (_process != null && _process.HasExited)
            {
                exitCode = _process.ExitCode;
            }
        }
        
        _logger.LogWarning("Process exited with code {ExitCode}", exitCode);
        ProcessExited?.Invoke(this, new ProcessExitEventArgs(exitCode));
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsDotnetCommand(string path)
    {
        return path.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Disposes the lifecycle manager and stops the application.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        await StopAsync();

        _healthCheckCts?.Dispose();
        _startupCts?.Dispose();
    }
}

/// <summary>
/// Event arguments for process exit events.
/// </summary>
public class ProcessExitEventArgs : EventArgs
{
    /// <summary>
    /// The exit code of the process.
    /// </summary>
    public int ExitCode { get; }

    public ProcessExitEventArgs(int exitCode)
    {
        ExitCode = exitCode;
    }
}

/// <summary>
/// Event arguments for startup failure events.
/// </summary>
public class StartupFailureEventArgs : EventArgs
{
    /// <summary>
    /// The exception that caused the startup failure.
    /// </summary>
    public Exception Exception { get; }

    public StartupFailureEventArgs(Exception exception)
    {
        Exception = exception;
    }
}
