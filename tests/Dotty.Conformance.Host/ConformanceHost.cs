using Dotty.Abstractions.Pty;
using Dotty.NativePty;
using Dotty.Runtime.Sessions;
using Dotty.Terminal.Adapter;

namespace Dotty.Conformance.Host;

/// <summary>
/// Runs a program on a real PTY whose terminal side is a headless <see cref="TerminalSession"/>
/// (the production byte path without any GUI). Contains no esctest knowledge.
/// </summary>
public static class ConformanceHost
{
    /// <summary>The child was killed after <see cref="HostOptions.Timeout"/>.</summary>
    public const int ExitTimeout = 124;

    /// <summary>PTY unsupported, helper missing, or session start failure.</summary>
    public const int ExitHostError = 125;

    /// <summary>Bad command-line arguments.</summary>
    public const int ExitUsage = 2;

    /// <summary>The host was interrupted (Ctrl-C).</summary>
    public const int ExitInterrupted = 130;

    /// <summary>
    /// Applies the opt-in core switches the conformance run needs (checksum reports, window resize,
    /// cell/pixel metrics) to the adapter before the child starts.
    /// Deliberately empty until plan item W5 populates it after the VT-sequence tracks land the
    /// adapter members it binds to; the requested switches are carried in <paramref name="options"/>
    /// (<see cref="HostOptions.EnableChecksumReports"/>, <see cref="HostOptions.EnableWindowResize"/>).
    /// </summary>
    public static void ApplyCoreSwitches(TerminalAdapter adapter, HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);
    }

    /// <summary>Runs <see cref="HostOptions.Command"/> and returns the child's exit code.</summary>
    public static int Run(HostOptions options, TextWriter diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!PtyFactory.IsSupported)
        {
            diagnostics.WriteLine(PtyFactory.GetUnsupportedReason() ?? "PTY is not supported on this platform.");
            return ExitHostError;
        }

        using TranscriptWriter? transcript = options.TranscriptPath is null
            ? null
            : new TranscriptWriter(options.TranscriptPath);
        using var session = new TerminalSession(options.Rows, options.Columns);
        TerminalAdapter adapter = session.Adapter;

        ApplyCoreSwitches(adapter, options);

        if (transcript is not null)
        {
            session.RawInputReceived += bytes => transcript.FromChild(bytes);
            adapter.ReplyRequested += reply => transcript.ToChild(reply);
        }

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProcessExited += code => exited.TrySetResult(code);

        try
        {
            session.StartWithOptions(
                shell: options.Command[0],
                workingDirectory: options.WorkingDirectory,
                environmentVariables: options.Environment.ToDictionary(p => p.Key, p => p.Value),
                command: options.Command,
                shellIsExecutable: true);
        }
        catch (PtyException ex)
        {
            diagnostics.WriteLine($"host error: {ex.Message}");
            return ExitHostError;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(options.Timeout, timeoutSource.Token);
        Task finished = Task.WhenAny(exited.Task, delay).GetAwaiter().GetResult();
        if (ReferenceEquals(finished, exited.Task))
        {
            timeoutSource.Cancel();
            return exited.Task.Result;
        }

        // Disposing the session (end of scope) closes the PTY master, which hangs up the child.
        if (cancellationToken.IsCancellationRequested)
        {
            diagnostics.WriteLine("interrupted");
            return ExitInterrupted;
        }

        diagnostics.WriteLine("timeout");
        return ExitTimeout;
    }
}
