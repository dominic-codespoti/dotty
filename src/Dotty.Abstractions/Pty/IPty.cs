using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dotty.Abstractions.Pty;

/// <summary>
/// Represents a pseudo-terminal (PTY) session that can spawn processes
/// and handle input/output with terminal emulation.
/// </summary>
public interface IPty : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the PTY session is running.
    /// </summary>
    bool IsRunning { get; }
    
    /// <summary>
    /// Gets the process ID of the spawned shell/process.
    /// Returns -1 if not available.
    /// </summary>
    int ProcessId { get; }
    
    /// <summary>
    /// Gets the stream for reading output from the PTY.
    /// </summary>
    Stream? OutputStream { get; }
    
    /// <summary>
    /// Gets the stream for writing input to the PTY.
    /// </summary>
    Stream? InputStream { get; }
    
    /// <summary>
    /// Event raised when the PTY process exits.
    /// </summary>
    event EventHandler<int>? ProcessExited;
    
    /// <summary>
    /// Starts the PTY session with the specified shell and terminal size.
    /// </summary>
    /// <param name="shell">The shell executable path. If null, uses platform default.</param>
    /// <param name="columns">Initial terminal width in columns.</param>
    /// <param name="rows">Initial terminal height in rows.</param>
    /// <param name="workingDirectory">Optional working directory for the process.</param>
    /// <param name="environmentVariables">Optional additional environment variables.</param>
    /// <exception cref="InvalidOperationException">Thrown if the PTY is already started.</exception>
    /// <exception cref="PtyException">Thrown if the PTY creation fails.</exception>
    void Start(
        string? shell = null, 
        int columns = 80, 
        int rows = 24,
        string? workingDirectory = null,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null);
    
    /// <summary>
    /// Resizes the PTY to the new dimensions.
    /// </summary>
    /// <param name="columns">New width in columns.</param>
    /// <param name="rows">New height in rows.</param>
    void Resize(int columns, int rows);
    
    /// <summary>
    /// Terminates the PTY process.
    /// </summary>
    /// <param name="force">If true, force kill the process. Otherwise graceful termination.</param>
    void Kill(bool force = false);
    
    /// <summary>
    /// Waits for the PTY process to exit asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The process exit code.</returns>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Exception thrown when PTY operations fail.
/// </summary>
public class PtyException : Exception
{
    public PtyException(string message) : base(message) { }
    public PtyException(string message, Exception inner) : base(message, inner) { }
}
