using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests;

/// <summary>
/// Base class for all E2E tests providing common functionality.
/// Automatically manages the application lifecycle (start before tests, stop after).
/// </summary>
public abstract class E2ETestBase : IAsyncLifetime
{
    private readonly string _testName;
    private ApplicationInstance? _app;
    private TestLogger? _logger;
    private TestApplicationHost? _host;
    private ScreenshotCapture? _screenshotCapture;
    private StateDumper? _stateDumper;
    private bool _isDisposed;
    private static readonly object _initLock = new();
    private static bool _appStarted;
    
    /// <summary>
    /// Gets the running application instance.
    /// </summary>
    protected ApplicationInstance App => _app ?? throw new InvalidOperationException("Application not started");
    
    /// <summary>
    /// Gets the test logger.
    /// </summary>
    protected TestLogger Logger => _logger ?? throw new InvalidOperationException("Logger not initialized");
    
    /// <summary>
    /// Gets the test output helper for xUnit integration.
    /// </summary>
    protected ITestOutputHelper? OutputHelper { get; }
    
    /// <summary>
    /// Gets the configuration for E2E tests.
    /// </summary>
    protected IConfiguration Configuration { get; }
    
    /// <summary>
    /// Creates a new E2E test base instance.
    /// </summary>
    protected E2ETestBase(string testName, ITestOutputHelper? outputHelper = null)
    {
        _testName = testName;
        OutputHelper = outputHelper;
        
        // Store reference for cleanup
        _instance = this;
        
        // Load configuration from appsettings
        Configuration = new ConfigurationBuilder()
            .AddJsonFile("e2e.appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }
    
    /// <summary>
    /// Initializes the test environment.
    /// Starts the application if not already running (shared across test classes).
    /// </summary>
    public virtual async Task InitializeAsync()
    {
        // Create artifacts directories
        var artifactsDir = Path.Combine(AppContext.BaseDirectory, "artifacts", _testName);
        Directory.CreateDirectory(artifactsDir);
        
        // Initialize logging and debugging utilities
        var testLoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = new TestLogger(Path.Combine(artifactsDir, "logs"), testLoggerFactory.CreateLogger<TestLogger>());
        _screenshotCapture = new ScreenshotCapture(Path.Combine(artifactsDir, "screenshots"));
        _stateDumper = new StateDumper(Path.Combine(artifactsDir, "states"));
        
        Logger.Log($"Starting E2E test: {_testName}");
        
        // Start the application (shared instance across tests)
        await StartApplicationAsync();
        
        Logger.Log("Application started successfully");
        
        // Wait for app to be ready
        await App.WaitForIdleAsync(TimeSpan.FromSeconds(5));
    }
    
    /// <summary>
    /// Starts the application if not already running.
    /// Uses a static lock to ensure only one app instance is started across all tests.
    /// </summary>
    private async Task StartApplicationAsync()
    {
        // Fast path - check if app is already running without lock
        if (_appStarted && _host?.IsRunning == true && _app != null)
        {
            Logger.Log("Using existing application instance");
            return;
        }
        
        // Slow path - acquire lock and start app
        lock (_initLock)
        {
            // Double-check inside lock
            if (_appStarted && _host?.IsRunning == true && _app != null)
            {
                Logger.Log("Using existing application instance (post-lock)");
                return;
            }
            
            // Clean up any previous failed attempts
            if (_host != null)
            {
                try
                {
                    _host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Ignore cleanup errors
                }
                _host = null;
                _app = null;
            }
        }
        
        // Start the application outside of lock to allow other threads to proceed
        // once we have marked _appStarted
        try
        {
            Logger.Log("Creating new application instance...");
            
            // Create the host with configuration
            _host = new TestApplicationHost(
                LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<TestApplicationHost>(),
                Configuration);
            
            // Configure headless mode
            var headless = ShouldRunHeadless();
            Logger.Log($"Headless mode: {headless}");
            
            var config = new TestApplicationConfig
            {
                Headless = headless,
                WindowWidth = Configuration.GetValue<int?>("E2ETest:Window:DefaultWidth") ?? 800,
                WindowHeight = Configuration.GetValue<int?>("E2ETest:Window:DefaultHeight") ?? 600,
                StartupTimeout = GetStartupTimeout(),
                EnvironmentVariables = GetAdditionalEnvironmentVariables()
            };
            
            // Start the host
            await _host.StartAsync(config);
            
            // Create the application instance wrapper
            _app = new ApplicationInstance(_host);
            
            lock (_initLock)
            {
                _appStarted = true;
            }
            
            Logger.Log($"Application started (PID: {_host.CommandPort})");
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to start application", ex);
            throw;
        }
    }
    
    /// <summary>
    /// Cleans up the test environment.
    /// </summary>
    public virtual async Task DisposeAsync()
    {
        if (_isDisposed)
            return;
            
        _isDisposed = true;
        
        try
        {
            Logger.Log($"Cleaning up E2E test: {_testName}");
            
            // Flush logs
            if (_logger != null)
                await _logger.FlushAsync(_testName);
            
            // Note: We don't dispose the application here to allow sharing across tests
            // The application will be cleaned up when the test process exits
            // or when explicitly requested via a shutdown mechanism
        }
        catch (Exception ex)
        {
            OutputHelper?.WriteLine($"Error during cleanup: {ex}");
        }
    }
    
    /// <summary>
    /// Stops the shared application instance.
    /// Call this from a test assembly cleanup method or final test class.
    /// </summary>
    public static async Task StopSharedApplicationAsync()
    {
        lock (_initLock)
        {
            if (!_appStarted || _instance?._host == null)
                return;
        }
        
        try
        {
            var host = _instance?._host;
            if (host != null)
            {
                await host.StopAsync();
                await host.DisposeAsync();
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
        finally
        {
            lock (_initLock)
            {
                _appStarted = false;
                if (_instance != null)
                {
                    _instance._host = null;
                    _instance._app = null;
                }
            }
        }
    }
    
    // Keep a reference to the current instance for cleanup
    private static E2ETestBase? _instance;
    
    /// <summary>
    /// Determines if tests should run in headless mode.
    /// </summary>
    protected virtual bool ShouldRunHeadless()
    {
        // Priority 1: Environment variable DOTTY_E2E_HEADLESS
        var headlessEnv = Environment.GetEnvironmentVariable("DOTTY_E2E_HEADLESS");
        if (headlessEnv == "1" || headlessEnv?.ToLowerInvariant() == "true")
        {
            Logger.Log("Headless mode enabled via DOTTY_E2E_HEADLESS environment variable");
            return true;
        }
        
        // Priority 2: CI environment detection
        if (IsRunningInCI())
        {
            Logger.Log("Headless mode enabled (CI environment detected)");
            return true;
        }
        
        // Priority 3: Configuration file setting
        var headlessConfig = Configuration.GetValue<bool?>("E2ETest:HeadlessMode");
        if (headlessConfig.HasValue)
        {
            Logger.Log($"Headless mode from config: {headlessConfig.Value}");
            return headlessConfig.Value;
        }
        
        // Default: true (headless) to avoid display dependencies
        Logger.Log("Headless mode enabled (default)");
        return true;
    }
    
    /// <summary>
    /// Checks if running in a CI environment.
    /// </summary>
    private bool IsRunningInCI()
    {
        var ciVars = new[]
        {
            "CI",
            "GITHUB_ACTIONS",
            "GITLAB_CI",
            "JENKINS_URL",
            "BUILD_BUILDID", // Azure DevOps
            "TEAMCITY_VERSION",
            "TRAVIS",
            "CIRCLECI",
            "APPVEYOR",
            "DRONE"
        };
        
        foreach (var varName in ciVars)
        {
            var value = Environment.GetEnvironmentVariable(varName);
            if (!string.IsNullOrEmpty(value) && (value == "true" || value == "1"))
            {
                Logger.Log($"CI environment detected via {varName}={value}");
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Gets the startup timeout from configuration.
    /// </summary>
    private TimeSpan GetStartupTimeout()
    {
        var timeoutMs = Configuration.GetValue<int?>("E2ETest:StartupTimeoutMs");
        if (timeoutMs.HasValue && timeoutMs.Value > 0)
            return TimeSpan.FromMilliseconds(timeoutMs.Value);
            
        return TimeSpan.FromSeconds(30);
    }
    
    /// <summary>
    /// Gets additional environment variables for the test.
    /// </summary>
    protected virtual Dictionary<string, string> GetAdditionalEnvironmentVariables()
    {
        var env = new Dictionary<string, string>();
        
        // Set display for headless mode
        if (ShouldRunHeadless())
        {
            env["DISPLAY"] = ":99";
        }
        
        // Copy through any existing DOTTY_* environment variables
        foreach (var entry in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
        {
            var key = entry.Key?.ToString();
            var value = entry.Value?.ToString();
            if (!string.IsNullOrEmpty(key) && key.StartsWith("DOTTY_") && !string.IsNullOrEmpty(value))
            {
                env[key] = value;
            }
        }
        
        return env;
    }
    
    /// <summary>
    /// Captures a screenshot on test failure.
    /// </summary>
    protected async Task CaptureScreenshotOnFailureAsync(string? suffix = null)
    {
        try
        {
            var imageData = await App.CaptureScreenshotAsync();
            if (imageData.Length > 0 && _screenshotCapture != null)
            {
                await _screenshotCapture.CaptureAsync(imageData, _testName, suffix ?? "failure");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to capture screenshot", ex);
        }
    }
    
    /// <summary>
    /// Dumps terminal state for debugging.
    /// </summary>
    protected async Task DumpStateAsync(TerminalState state, string[] screenLines)
    {
        try
        {
            if (_stateDumper != null)
                await _stateDumper.DumpStateAsync(state, screenLines, _testName);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to dump state", ex);
        }
    }
    
    /// <summary>
    /// Runs a test with automatic failure handling.
    /// </summary>
    protected async Task RunTestAsync(Func<Task> testAction)
    {
        try
        {
            await testAction();
            Logger.Log("Test passed");
        }
        catch (Exception ex)
        {
            Logger.LogError("Test failed", ex);
            await CaptureScreenshotOnFailureAsync();
            throw;
        }
    }
    
    /// <summary>
    /// Waits for a condition with timeout and polling.
    /// </summary>
    protected async Task<bool> WaitForAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        return await TimeoutHelper.WaitForConditionAsync(
            condition,
            timeout ?? TimeSpan.FromSeconds(10),
            pollInterval ?? TimeSpan.FromMilliseconds(100));
    }
    
    /// <summary>
    /// Sends text to the terminal and waits for idle.
    /// </summary>
    protected async Task SendTextAndWaitAsync(string text, TimeSpan? waitTime = null)
    {
        await App.Commands.SendTextAsync(text);
        await Task.Delay(waitTime ?? TimeSpan.FromMilliseconds(500));
        await App.WaitForIdleAsync(TimeSpan.FromSeconds(2));
    }
    
    /// <summary>
    /// Gets current application statistics.
    /// </summary>
    protected async Task<ApplicationStats> GetStatsAsync()
    {
        return await App.GetStatsAsync();
    }
}
