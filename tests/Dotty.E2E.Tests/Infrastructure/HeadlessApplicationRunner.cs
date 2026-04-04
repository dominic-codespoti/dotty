using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Runs the Dotty application in headless mode for CI/testing.
/// </summary>
public sealed class HeadlessApplicationRunner : IAsyncDisposable
{
    private readonly ILogger<HeadlessApplicationRunner> _logger;
    private Application? _application;
    private Window? _mainWindow;
    private bool _isDisposed;
    
    public HeadlessApplicationRunner(ILogger<HeadlessApplicationRunner>? logger = null)
    {
        _logger = logger ?? new LoggerFactory().CreateLogger<HeadlessApplicationRunner>();
    }
    
    /// <summary>
    /// Starts the application in headless mode.
    /// </summary>
    public async Task StartAsync(TestApplicationConfig config, CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(HeadlessApplicationRunner));
        
        _logger.LogInformation("Starting application in headless mode");
        
        // Initialize Avalonia in headless mode
        var builder = Avalonia.AppBuilder.Configure<Dotty.App.App>()
            .WithInterFont()
            .UsePlatformDetect()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true
            });
        
        _application = builder.Instance as Application;
        
        // Create main window
        _mainWindow = new Dotty.App.Views.MainWindow();
        
        // Set window size
        if (config.WindowWidth.HasValue && config.WindowHeight.HasValue)
        {
            _mainWindow.Width = config.WindowWidth.Value;
            _mainWindow.Height = config.WindowHeight.Value;
        }
        
        // Show the window
        _mainWindow.Show();
        
        // Wait for window to be ready
        await Task.Delay(500, cancellationToken);
        
        _logger.LogInformation("Headless application started");
    }
    
    /// <summary>
    /// Captures a screenshot of the current window.
    /// </summary>
    public async Task<byte[]> CaptureScreenshotAsync()
    {
        if (_mainWindow == null)
            throw new InvalidOperationException("Application not started");
        
        return await Task.Run(() =>
        {
            // Use Avalonia's headless screenshot capability
            var bitmap = _mainWindow.CaptureRenderedFrame();
            
            if (bitmap == null)
                return Array.Empty<byte>();
            
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        });
    }
    
    /// <summary>
    /// Gets the main window for direct interaction.
    /// </summary>
    public Window? GetMainWindow() => _mainWindow;
    
    /// <summary>
    /// Stops the headless application.
    /// </summary>
    public Task StopAsync()
    {
        _mainWindow?.Close();
        _mainWindow = null;
        _application = null;
        
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Disposes the runner.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;
            
        _isDisposed = true;
        
        await StopAsync();
    }
}

/// <summary>
/// Wrapper around a running application instance.
/// Provides convenient access to the application state and operations.
/// </summary>
public sealed class ApplicationInstance : IAsyncDisposable
{
    private readonly TestApplicationHost _host;
    private readonly TestCommandInterface _commandInterface;
    private readonly ILogger<ApplicationInstance> _logger;
    private bool _isDisposed;
    
    /// <summary>
    /// Gets the command interface for sending commands.
    /// </summary>
    public ITestCommandInterface Commands => _commandInterface;
    
    /// <summary>
    /// Gets the application host.
    /// </summary>
    public TestApplicationHost Host => _host;
    
    /// <summary>
    /// Creates a new application instance wrapper.
    /// </summary>
    public ApplicationInstance(
        TestApplicationHost host,
        ILogger<ApplicationInstance>? logger = null)
    {
        _host = host;
        _logger = logger ?? new LoggerFactory().CreateLogger<ApplicationInstance>();
        _commandInterface = new TestCommandInterface(host.CommandPort);
    }
    
    /// <summary>
    /// Waits for the application to be idle.
    /// </summary>
    public async Task WaitForIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(5);
        
        try
        {
            using var cts = new CancellationTokenSource(actualTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            
            await _commandInterface.WaitForIdleAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("WaitForIdle timed out after {TimeoutMs}ms", actualTimeout.TotalMilliseconds);
        }
    }
    
    /// <summary>
    /// Waits for a condition to be met.
    /// </summary>
    public async Task<bool> WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        return await TimeoutHelper.WaitForConditionAsync(
            async (ct) => await condition(),
            timeout ?? TimeSpan.FromSeconds(10),
            pollInterval,
            cancellationToken);
    }
    
    /// <summary>
    /// Gets the current application statistics.
    /// Checks if the application is running first and throws if it has crashed.
    /// </summary>
    public async Task<ApplicationStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        // Check if host is still running
        if (!_host.IsRunning)
        {
            _logger.LogError("Cannot get stats: Application process is not running");
            throw new InvalidOperationException(
                "Application is not running. The process may have crashed or exited unexpectedly.");
        }
        
        return await _commandInterface.GetStatsAsync(cancellationToken);
    }
    
    /// <summary>
    /// Captures a screenshot of the current state.
    /// </summary>
    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
    {
        return await _commandInterface.ScreenshotAsync(cancellationToken);
    }
    
    /// <summary>
    /// Disposes the instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;
            
        _isDisposed = true;
        
        await _commandInterface.DisposeAsync();
        await _host.DisposeAsync();
    }
}
