using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dotty.Abstractions.Pty;

/// <summary>
/// Represents a pseudo-terminal session that can spawn a process and handle its terminal I/O.
/// </summary>
public interface IPty : IDisposable
{
    /// <summary>Gets whether the child process is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Gets the child process ID, or -1 when it is unavailable.</summary>
    int ProcessId { get; }

    /// <summary>Gets the stream carrying terminal output from the child.</summary>
    Stream? OutputStream { get; }

    /// <summary>Gets the stream carrying terminal input to the child.</summary>
    Stream? InputStream { get; }

    /// <summary>Raised when the child process exits; the value is its exit code.</summary>
    event EventHandler<int>? ProcessExited;

    /// <summary>Starts the PTY with a shell or command and the requested initial working directory.</summary>
    /// <param name="shell">Shell executable or legacy shell command; null selects the platform default.</param>
    /// <param name="columns">Initial terminal width in columns.</param>
    /// <param name="rows">Initial terminal height in rows.</param>
    /// <param name="workingDirectory">Initial child working directory, or null for the platform default.</param>
    /// <param name="environmentVariables">Optional additional child environment variables.</param>
    /// <param name="command">Optional exact argv to execute directly, including argv[0].</param>
    /// <param name="shellIsExecutable">True when shell is a literal executable path rather than a legacy shell command string.</param>
    /// <exception cref="InvalidOperationException">The PTY has already been started.</exception>
    /// <exception cref="PtyException">PTY creation or child process startup failed.</exception>
    void Start(
        string? shell = null,
        int columns = 80,
        int rows = 24,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        IReadOnlyList<string>? command = null,
        bool shellIsExecutable = false);

    /// <summary>Resizes the PTY to the requested terminal dimensions.</summary>
    void Resize(int columns, int rows);

    /// <summary>Terminates the child process.</summary>
    void Kill(bool force = false);

    /// <summary>Waits asynchronously for the child process to exit.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Exception thrown when PTY operations fail.</summary>
public class PtyException : Exception
{
    /// <summary>Gets the typed error category associated with the failure.</summary>
    public PtyErrorCode Code { get; }

    /// <summary>Creates an exception with an unclassified error message.</summary>
    public PtyException(string message) : base(message) { }

    /// <summary>Creates an exception with a typed error category.</summary>
    public PtyException(PtyErrorCode code, string message) : base(message) => Code = code;

    /// <summary>Creates an exception with a typed error category and inner exception.</summary>
    public PtyException(PtyErrorCode code, string message, Exception innerException) : base(message, innerException) => Code = code;
}
