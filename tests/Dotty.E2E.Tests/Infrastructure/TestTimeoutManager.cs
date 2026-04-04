using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Dotty.E2E.Tests.Infrastructure;

/// <summary>
/// Manages test timeouts and ensures tests don't hang indefinitely.
/// </summary>
public sealed class TestTimeoutManager
{
    private readonly ILogger<TestTimeoutManager> _logger;
    private CancellationTokenSource? _timeoutCts;
    private DateTime _startTime;
    private TimeSpan _timeout;
    
    /// <summary>
    /// Creates a new timeout manager.
    /// </summary>
    public TestTimeoutManager(ILogger<TestTimeoutManager>? logger = null)
    {
        _logger = logger ?? new LoggerFactory().CreateLogger<TestTimeoutManager>();
    }
    
    /// <summary>
    /// Starts a timeout for the current test.
    /// </summary>
    public CancellationToken StartTimeout(TimeSpan timeout)
    {
        _timeout = timeout;
        _startTime = DateTime.UtcNow;
        _timeoutCts = new CancellationTokenSource(timeout);
        
        _logger.LogDebug("Started timeout: {TimeoutMs}ms", timeout.TotalMilliseconds);
        
        return _timeoutCts.Token;
    }
    
    /// <summary>
    /// Gets the remaining time before timeout.
    /// </summary>
    public TimeSpan RemainingTime => 
        _timeout - (DateTime.UtcNow - _startTime);
    
    /// <summary>
    /// Checks if the timeout has expired.
    /// </summary>
    public bool IsExpired => 
        _timeoutCts?.IsCancellationRequested == true;
    
    /// <summary>
    /// Throws if the timeout has expired.
    /// </summary>
    public void ThrowIfExpired()
    {
        if (IsExpired)
            throw new TimeoutException($"Test timed out after {_timeout.TotalSeconds} seconds");
    }
    
    /// <summary>
    /// Extends the current timeout.
    /// </summary>
    public void ExtendTimeout(TimeSpan additionalTime)
    {
        if (_timeoutCts == null)
            throw new InvalidOperationException("No timeout has been started");
            
        var newTimeout = RemainingTime + additionalTime;
        _timeoutCts.CancelAfter(newTimeout);
        _timeout += additionalTime;
        
        _logger.LogDebug("Extended timeout by {AdditionalMs}ms, new remaining: {RemainingMs}ms",
            additionalTime.TotalMilliseconds, RemainingTime.TotalMilliseconds);
    }
    
    /// <summary>
    /// Resets the timeout with a new duration.
    /// </summary>
    public void ResetTimeout(TimeSpan newTimeout)
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        
        _timeout = newTimeout;
        _startTime = DateTime.UtcNow;
        _timeoutCts = new CancellationTokenSource(newTimeout);
        
        _logger.LogDebug("Reset timeout to {TimeoutMs}ms", newTimeout.TotalMilliseconds);
    }
    
    /// <summary>
    /// Cancels the current timeout.
    /// </summary>
    public void Cancel()
    {
        _timeoutCts?.Cancel();
        _logger.LogDebug("Timeout cancelled");
    }
    
    /// <summary>
    /// Disposes the timeout manager.
    /// </summary>
    public void Dispose()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _timeoutCts = null;
    }
}

/// <summary>
/// Helper for running tasks with timeout.
/// </summary>
public static class TimeoutHelper
{
    /// <summary>
    /// Runs a task with a specified timeout.
    /// </summary>
    public static async Task<T> RunWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> taskFactory,
        TimeSpan timeout,
        string? operationName = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        
        try
        {
            return await taskFactory(cts.Token);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Operation '{operationName ?? "task"}' timed out after {timeout.TotalSeconds} seconds");
        }
    }
    
    /// <summary>
    /// Runs an action with a specified timeout.
    /// </summary>
    public static async Task RunWithTimeoutAsync(
        Func<CancellationToken, Task> taskFactory,
        TimeSpan timeout,
        string? operationName = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        
        try
        {
            await taskFactory(cts.Token);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Operation '{operationName ?? "task"}' timed out after {timeout.TotalSeconds} seconds");
        }
    }
    
    /// <summary>
    /// Waits for a condition to become true with a timeout.
    /// </summary>
    public static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (condition())
                return true;
                
            await Task.Delay(interval, cancellationToken);
        }
        
        return false;
    }
    
    /// <summary>
    /// Waits for a condition to become true with a timeout.
    /// </summary>
    public static async Task<bool> WaitForConditionAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (await condition(cancellationToken))
                return true;
                
            await Task.Delay(interval, cancellationToken);
        }
        
        return false;
    }
}
