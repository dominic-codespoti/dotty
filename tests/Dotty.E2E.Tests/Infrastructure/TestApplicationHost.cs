using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Manages the lifecycle of the Dotty application for E2E testing.
/// Handles startup, shutdown, and health monitoring.
/// 
/// This is a high-level wrapper around AppLifecycleManager that provides:
/// - Automatic app path detection and building
/// - TCP command interface management
/// - Integration with the E2E test configuration
/// </summary>
public sealed class TestApplicationHost : IAsyncDisposable
{
    private readonly ILogger<TestApplicationHost> _logger;
    private readonly IConfiguration? _configuration;
    private AppLifecycleManager? _lifecycleManager;
    private TestCommandInterface? _commandInterface;
    private bool _isDisposed;
    private int _commandPort;
    
    /// <summary>
    /// Gets the port used for the test command interface.
    /// </summary>
    public int CommandPort => _commandPort;
    
    /// <summary>
    /// Gets a value indicating whether the application is running.
    /// </summary>
    public bool IsRunning => _lifecycleManager?.IsRunning ?? false;
    
    /// <summary>
    /// Gets the command interface for sending commands to the application.
    /// </summary>
    public ITestCommandInterface? CommandInterface => _commandInterface;
    
    /// <summary>
    /// Event raised when the application process exits.
    /// </summary>
    public event EventHandler<int>? ProcessExited;
    
    /// <summary>
    /// Creates a new test application host.
    /// </summary>
    public TestApplicationHost(ILogger<TestApplicationHost>? logger = null, IConfiguration? configuration = null)
    {
        _logger = logger ?? new LoggerFactory().CreateLogger<TestApplicationHost>();
        _configuration = configuration;
    }
    
    /// <summary>
    /// Starts the Dotty application with the specified configuration.
    /// </summary>
    /// <param name="config">Configuration for the test run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StartAsync(TestApplicationConfig config, CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(TestApplicationHost));
            
        if (IsRunning)
            throw new InvalidOperationException("Application is already running");
        
        _logger.LogInformation("Starting Dotty application for E2E testing");
        
        try
        {
            // Step 1: Build the application if needed
            var appPath = await GetOrBuildApplicationAsync(config);
            _logger.LogInformation("Application path: {Path}", appPath);
            
            // Step 2: Find available port for test command interface
            _commandPort = config.CommandPort ?? FindAvailablePort();
            _logger.LogInformation("Using test command port: {Port}", _commandPort);
            
            // Step 3: Kill any orphaned processes from previous runs (if enabled)
            var killOrphaned = _configuration?.GetValue<bool>("E2ETest:ProcessManagement:KillOrphanedProcesses") ?? true;
            if (killOrphaned)
            {
                _logger.LogInformation("Cleaning up orphaned processes...");
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    AppLifecycleManager.KillOrphanedProcesses("Dotty.App");
                    AppLifecycleManager.KillOrphanedProcesses("dotnet");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up orphaned processes, continuing...");
                }
            }
            else
            {
                _logger.LogInformation("Orphaned process cleanup disabled in configuration");
            }
            
            // Step 4: Create and configure the lifecycle manager
            var startupTimeout = config.StartupTimeout ?? GetStartupTimeoutFromConfig();
            var healthCheckInterval = GetHealthCheckIntervalFromConfig();
            var autoRestart = GetAutoRestartFromConfig();
            
            // Create a typed logger for AppLifecycleManager
            var lifecycleLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<AppLifecycleManager>();
            
            // Get Xvfb configuration from settings
            var useXvfb = _configuration?.GetValue<bool>("E2ETest:Headless:UseXvfbIfAvailable") ?? true;
            var xvfbDisplay = _configuration?.GetValue<string>("E2ETest:Headless:Display") ?? ":99";
            var xvfbScreen = _configuration?.GetValue<string>("E2ETest:Headless:Screen") ?? "1024x768x24";
            
            _logger.LogInformation("Xvfb configuration: UseXvfb={UseXvfb}, Display={Display}, Screen={Screen}",
                useXvfb, xvfbDisplay, xvfbScreen);
            
            _lifecycleManager = new AppLifecycleManager(
                appPath,
                config.Headless,
                startupTimeout,
                healthCheckInterval,
                autoRestart,
                lifecycleLogger,
                useXvfb,
                xvfbDisplay,
                xvfbScreen);
            
            _lifecycleManager.ProcessExited += (s, e) => 
            {
                ProcessExited?.Invoke(this, e.ExitCode);
            };
            
            // Step 5: Start the application
            var arguments = config.Arguments;
            if (config.Headless)
            {
                // Add headless argument if using dotnet run
                if (appPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    arguments = $"--headless {arguments ?? ""}".Trim();
                }
            }
            
            await _lifecycleManager.StartAsync(
                _commandPort,
                arguments,
                config.EnvironmentVariables,
                cancellationToken);
            
            // Step 6: Create command interface
            var commandLogger = LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<TestCommandInterface>();
            _commandInterface = new TestCommandInterface(_commandPort, "127.0.0.1", commandLogger);
            
            _logger.LogInformation("Dotty application started successfully (PID: {Pid})", _lifecycleManager.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start application");
            
            // Clean up on failure
            await CleanupAsync();
            
            throw new InvalidOperationException(
                $"Failed to start Dotty application for E2E testing: {ex.Message}", 
                ex);
        }
    }
    
    /// <summary>
    /// Stops the application gracefully.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _lifecycleManager == null)
            return;
            
        _logger.LogInformation("Stopping Dotty application");
        
        // Dispose command interface first
        if (_commandInterface != null)
        {
            await _commandInterface.DisposeAsync();
            _commandInterface = null;
        }
        
        // Stop the lifecycle manager
        await _lifecycleManager.StopAsync(
            TimeSpan.FromSeconds(5), 
            cancellationToken);
        
        _logger.LogInformation("Dotty application stopped");
    }
    
    /// <summary>
    /// Sends a command to the running application.
    /// </summary>
    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (_commandInterface == null)
            throw new InvalidOperationException("Command interface not initialized");
            
        return await _commandInterface.SendCommandAsync(command, cancellationToken);
    }
    
    /// <summary>
    /// Waits for the application to become responsive.
    /// </summary>
    public async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_lifecycleManager == null)
            throw new InvalidOperationException("Application not started");
            
        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (await _lifecycleManager.IsHealthyAsync(cancellationToken))
                return;
                
            await Task.Delay(100, cancellationToken);
        }
        
        throw new TimeoutException($"Application failed to become ready within {timeout.TotalSeconds} seconds");
    }
    
    /// <summary>
    /// Gets the current application statistics.
    /// </summary>
    public async Task<ApplicationStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        if (_commandInterface == null)
            throw new InvalidOperationException("Command interface not initialized");
            
        return await _commandInterface.GetStatsAsync(cancellationToken);
    }
    
    /// <summary>
    /// Automatically detects and builds the Dotty application if needed.
    /// </summary>
    private async Task<string> GetOrBuildApplicationAsync(TestApplicationConfig config)
    {
        // Priority 1: Use explicitly configured path
        if (!string.IsNullOrEmpty(config.ExecutablePath))
        {
            var explicitPath = ResolveAppPath(config.ExecutablePath);
            if (explicitPath != null)
                return explicitPath;
        }
        
        // Priority 2: Use path from appsettings.json
        var configPath = _configuration?["E2ETest:TestApplicationPath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            var resolvedPath = ResolveAppPath(configPath);
            if (resolvedPath != null)
                return resolvedPath;
        }
        
        // Priority 3: Try to find pre-built executable in standard locations
        var prebuiltPath = FindPrebuiltExecutable();
        if (prebuiltPath != null)
            return prebuiltPath;
        
        // Priority 4: Build the application
        _logger.LogInformation("No pre-built executable found. Building application...");
        return await BuildApplicationAsync(config);
    }
    
    /// <summary>
    /// Attempts to find a pre-built executable in standard locations.
    /// </summary>
    private string? FindPrebuiltExecutable()
    {
        var testBinDir = AppContext.BaseDirectory;
        
        // Get the solution root by going up from the test binary directory
        string? solutionDir = null;
        var currentDir = testBinDir;
        
        for (int i = 0; i < 10; i++)
        {
            // Check if this directory has src/Dotty.App subdirectory
            var srcDir = Path.Combine(currentDir, "src", "Dotty.App");
            if (Directory.Exists(srcDir))
            {
                solutionDir = currentDir;
                break;
            }
            
            // Also check for solution files
            if (File.Exists(Path.Combine(currentDir, "Dotty.sln")) ||
                File.Exists(Path.Combine(currentDir, "dotnet-term.sln")))
            {
                solutionDir = currentDir;
                break;
            }
            
            // Go up one level
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir == null)
                break;
            currentDir = parentDir.FullName;
        }
        
        if (solutionDir == null)
            return null;
        
        // Look for the executable in various build configurations relative to solution dir
        var buildConfigs = new[] { "Release", "Debug" };
        var targetFrameworks = new[] { "net10.0", "net9.0", "net8.0" };
        var runtimes = new[] { "linux-x64", "linux-arm64", "win-x64", "osx-x64", "" };
        
        foreach (var buildConfig in buildConfigs)
        {
            foreach (var tfm in targetFrameworks)
            {
                foreach (var runtime in runtimes)
                {
                    var runtimeSuffix = string.IsNullOrEmpty(runtime) ? "" : $"{runtime}{Path.DirectorySeparatorChar}";
                    var appDir = Path.Combine(solutionDir, "src", "Dotty.App", "bin", buildConfig, tfm, runtimeSuffix);
                    
                    if (!Directory.Exists(appDir))
                        continue;
                    
                    // Try different executable names
                    var exeNames = new[]
                    {
                        "Dotty.App",
                        "Dotty.App.exe",
                        "Dotty.App.dll"
                    };
                    
                    foreach (var exeName in exeNames)
                    {
                        var exePath = Path.Combine(appDir, exeName);
                        if (File.Exists(exePath))
                        {
                            _logger.LogDebug("Found pre-built executable: {Path}", exePath);
                            return exePath;
                        }
                    }
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Builds the Dotty application using dotnet build.
    /// </summary>
    private async Task<string> BuildApplicationAsync(TestApplicationConfig config)
    {
        var testBinDir = AppContext.BaseDirectory;
        
        // Find the solution directory - look for src/Dotty.App directory going up from the test binary
        string? solutionDir = null;
        var currentDir = testBinDir;
        
        for (int i = 0; i < 10; i++) // Go up at most 10 levels
        {
            // Check if this directory has src/Dotty.App subdirectory
            var srcDir = Path.Combine(currentDir, "src", "Dotty.App");
            if (Directory.Exists(srcDir))
            {
                solutionDir = currentDir;
                break;
            }
            
            // Also check for solution files
            if (File.Exists(Path.Combine(currentDir, "Dotty.sln")) ||
                File.Exists(Path.Combine(currentDir, "dotnet-term.sln")))
            {
                solutionDir = currentDir;
                break;
            }
            
            // Go up one level
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir == null)
                break;
            currentDir = parentDir.FullName;
        }
        
        if (solutionDir == null)
        {
            // Fallback: use the working directory from config or current directory
            solutionDir = Directory.GetCurrentDirectory();
            
            // Verify this is the right place
            if (!Directory.Exists(Path.Combine(solutionDir, "src", "Dotty.App")))
            {
                throw new InvalidOperationException(
                    $"Could not find solution directory with src/Dotty.App. Searched from: {testBinDir}");
            }
        }
        
        _logger.LogInformation("Solution directory: {Dir}", solutionDir);
        
        // Build the application
        var buildConfig = config.Headless ? "Release" : "Debug";
        var projectPath = Path.Combine(solutionDir, "src", "Dotty.App", "Dotty.App.csproj");
        
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException($"Project file not found: {projectPath}");
        }
        
        _logger.LogInformation("Building Dotty.App (Configuration: {Config})...", buildConfig);
        
        var processInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\" -c {buildConfig} --verbosity quiet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = solutionDir,
            CreateNoWindow = true
        };
        
        using var process = new Process { StartInfo = processInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        
        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                outputBuilder.AppendLine(e.Data);
        };
        
        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                errorBuilder.AppendLine(e.Data);
        };
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new TimeoutException("Build process timed out after 5 minutes");
        }
        
        if (process.ExitCode != 0)
        {
            var error = errorBuilder.ToString();
            var output = outputBuilder.ToString();
            throw new InvalidOperationException(
                $"Build failed with exit code {process.ExitCode}. Error: {error}. Output: {output}");
        }
        
        _logger.LogInformation("Build completed successfully");
        
        // Find the built executable
        var builtPath = FindPrebuiltExecutable();
        if (builtPath == null)
        {
            throw new InvalidOperationException("Build succeeded but could not find output executable");
        }
        
        return builtPath;
    }
    
    /// <summary>
    /// Resolves a potentially relative path to an absolute path.
    /// </summary>
    private string? ResolveAppPath(string path)
    {
        // If path is already absolute, check if it exists
        if (Path.IsPathRooted(path))
        {
            if (File.Exists(path))
                return path;
            return null;
        }
        
        // Try relative to current directory
        var currentDirPath = Path.GetFullPath(path);
        if (File.Exists(currentDirPath))
            return currentDirPath;
        
        // Try relative to test binary directory
        var binDirPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(binDirPath))
            return binDirPath;
        
        return null;
    }
    
    private TimeSpan GetStartupTimeoutFromConfig()
    {
        var timeoutMs = _configuration?.GetValue<int>("E2ETest:StartupTimeoutMs");
        if (timeoutMs.HasValue && timeoutMs.Value > 0)
            return TimeSpan.FromMilliseconds(timeoutMs.Value);
            
        return TimeSpan.FromSeconds(30);
    }
    
    private TimeSpan GetHealthCheckIntervalFromConfig()
    {
        var intervalMs = _configuration?.GetValue<int>("E2ETest:HealthCheckIntervalMs");
        if (intervalMs.HasValue && intervalMs.Value > 0)
            return TimeSpan.FromMilliseconds(intervalMs.Value);
            
        return TimeSpan.FromSeconds(5);
    }
    
    private bool GetAutoRestartFromConfig()
    {
        return _configuration?.GetValue<bool>("E2ETest:AutoRestartOnFailure") ?? false;
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
    
    private async Task CleanupAsync()
    {
        try
        {
            if (_commandInterface != null)
            {
                await _commandInterface.DisposeAsync();
                _commandInterface = null;
            }
            
            if (_lifecycleManager != null)
            {
                await _lifecycleManager.DisposeAsync();
                _lifecycleManager = null;
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
    
    /// <summary>
    /// Disposes the application host.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;
            
        _isDisposed = true;
        
        await CleanupAsync();
    }
}

/// <summary>
/// Configuration for starting the test application.
/// </summary>
public record TestApplicationConfig
{
    /// <summary>
    /// Whether to run in headless mode (no display required).
    /// </summary>
    public bool Headless { get; init; } = true;
    
    /// <summary>
    /// The port to use for the test command interface. If null, an available port will be found automatically.
    /// </summary>
    public int? CommandPort { get; init; }
    
    /// <summary>
    /// Path to the application executable. If null, will be auto-detected or built.
    /// </summary>
    public string? ExecutablePath { get; init; }
    
    /// <summary>
    /// Command line arguments to pass to the application.
    /// </summary>
    public string? Arguments { get; init; }
    
    /// <summary>
    /// Working directory for the application process.
    /// </summary>
    public string? WorkingDirectory { get; init; }
    
    /// <summary>
    /// Initial window width in pixels.
    /// </summary>
    public int? WindowWidth { get; init; } = 800;
    
    /// <summary>
    /// Initial window height in pixels.
    /// </summary>
    public int? WindowHeight { get; init; } = 600;
    
    /// <summary>
    /// Maximum time to wait for the application to start.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Additional environment variables to set for the application process.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();
}
