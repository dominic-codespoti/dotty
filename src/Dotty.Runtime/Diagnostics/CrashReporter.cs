using System.Runtime.InteropServices;

namespace Dotty.Runtime.Diagnostics;

/// <summary>
/// Process-wide crash reporting into a local <see cref="CrashLog"/>; never networked. After
/// <see cref="Install"/>, unhandled exceptions, unobserved task exceptions and GLFW errors (through
/// <see cref="ReportGlfwError"/>) are written to the log. Before installation every report is a no-op,
/// so tests and command-line actions never write a crash log. Reporting never throws.
/// </summary>
public static class CrashReporter
{
    /// <summary>GLFW errors logged per process; later ones are counted but not written.</summary>
    public const int MaxGlfwErrors = 16;

    private static readonly object Gate = new();
    private static CrashLog? _log;
    private static int _glfwErrors;

    /// <summary>The log being written, or null before <see cref="Install"/>.</summary>
    public static CrashLog? Log => Volatile.Read(ref _log);

    /// <summary>Subscribes the managed handlers once; later calls are ignored.</summary>
    public static void Install(CrashLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Interlocked.CompareExchange(ref _log, log, null) is not null)
            return;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>Writes one entry; returns the log path, or null when not installed or the write failed.</summary>
    public static string? Report(string source, string details)
    {
        CrashLog? log = Log;
        if (log is null)
            return null;
        try
        {
            var entry = new CrashEntry(
                DateTimeOffset.UtcNow,
                source,
                DottyVersion.Describe(),
                $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})",
                $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
                details);
            string text = CrashLog.Format(entry);
            lock (Gate)
                return log.Append(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Logs a GLFW error (called from the native error callback, possibly on any thread). Only the first
    /// <see cref="MaxGlfwErrors"/> are written; the last written entry says that further errors are dropped.
    /// </summary>
    public static void ReportGlfwError(string code, string? description)
    {
        if (Log is null)
            return;
        int count = Interlocked.Increment(ref _glfwErrors);
        if (count > MaxGlfwErrors)
            return;
        string details = $"GLFW error {code}: {description ?? "(no description)"}";
        if (count == MaxGlfwErrors)
            details += $"\nThis is GLFW error {MaxGlfwErrors}; later GLFW errors in this process are not logged.";
        Report("GLFW error callback", details);
    }

    /// <summary>
    /// Logs an exception that ended the process from <c>Main</c> and tells the user where the report is.
    /// </summary>
    public static void ReportFatal(Exception exception, TextWriter error)
    {
        string? path = Report("exception escaping Main", exception.ToString());
        if (path is not null)
            TryWrite(error, $"dotty: crash report written to {path}");
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        string? path = Report(
            args.IsTerminating ? "unhandled exception (process terminating)" : "unhandled exception",
            args.ExceptionObject?.ToString() ?? "(no exception object)");
        if (path is not null)
            TryWrite(Console.Error, $"dotty: crashed; crash report written to {path}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        Report("unobserved task exception", args.Exception.ToString());

    private static void TryWrite(TextWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
