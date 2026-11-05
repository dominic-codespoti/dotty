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

    /// <summary>Character cell width, in pixels, reported by <c>CSI 16 t</c> and used to derive <c>CSI 14 t</c>.</summary>
    public const int CellPixelWidth = 8;

    /// <summary>Character cell height, in pixels, reported by <c>CSI 16 t</c> and used to derive <c>CSI 14 t</c>.</summary>
    public const int CellPixelHeight = 16;

    /// <summary>
    /// Applies the core switches the conformance run needs to the adapter before the child starts.
    /// <list type="bullet">
    /// <item><description><see cref="HostOptions.EnableChecksumReports"/> sets
    /// <see cref="TerminalAdapter.ChecksumReportEnabled"/>.</description></item>
    /// <item><description><see cref="HostOptions.EnableTitleReports"/> sets
    /// <see cref="TerminalAdapter.TitleReportEnabled"/> independently and permits title-report replies.</description></item>
    /// <item><description><see cref="HostOptions.EnableWindowResize"/> honors <c>CSI 8 ; rows ; cols t</c> /
    /// <c>CSI Ps t</c> (Ps &gt;= 24) and mode-40-authorized DECCOLM by calling <paramref name="resizeGrid"/> on the parser
    /// thread, so a following <c>CSI 18 t</c> in the same byte stream already sees the new grid; a requested 0
    /// keeps that dimension. Without the flag requests are ignored, like a product window that disallows them.</description></item>
    /// <item><description>The cell size is pinned to <see cref="CellPixelWidth"/> x <see cref="CellPixelHeight"/>
    /// (the adapter default, set explicitly so the conformance contract does not drift). No text-area pixel
    /// override is installed: without one the adapter derives <c>CSI 14 t</c> from the live grid, so it stays
    /// consistent with <c>CSI 18 t</c> after any resize, including ones this host did not make.</description></item>
    /// <item><description>The headless theme is explicitly black on white. Dynamic-color resets restore
    /// these defaults rather than a previous OSC override; product window themes are unaffected.</description></item>
    /// </list>
    /// </summary>
    /// <param name="resizeGrid">
    /// Applies a resize as (rows, columns); <c>null</c> resizes only the adapter's buffer. <see cref="Run"/>
    /// passes the session's resize so the PTY window size follows.
    /// </param>
    public static void ApplyCoreSwitches(TerminalAdapter adapter, HostOptions options, Action<int, int>? resizeGrid = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);

        adapter.SetCellPixelSize(CellPixelWidth, CellPixelHeight);
        adapter.SetDefaultColors("#000000", "#FFFFFF");
        adapter.ChecksumReportEnabled = options.EnableChecksumReports;
        adapter.TitleReportEnabled = options.EnableTitleReports;

        if (!options.EnableWindowResize)
            return;

        Action<int, int> resize = resizeGrid ?? new Action<int, int>(adapter.ResizeBuffer);
        adapter.WindowResizeRequested += (rows, columns) =>
            resize(
                rows == 0 ? adapter.Buffer.Rows : rows,
                columns == 0 ? adapter.Buffer.Columns : columns);
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

        ApplyCoreSwitches(adapter, options, (rows, columns) => session.Resize(columns, rows, 0, 0));

        if (transcript is not null)
        {
            session.RawInputReceived += bytes => transcript.FromChild(bytes);
            adapter.ReplyRequested += reply => transcript.ToChild(reply);
            adapter.RawReplyBytesRequested += reply => transcript.ToChild(reply.Span);
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
