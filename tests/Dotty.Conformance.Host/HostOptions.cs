using System.Globalization;

namespace Dotty.Conformance.Host;

/// <summary>Parsed command line of the conformance host.</summary>
/// <param name="Rows">Initial grid rows (esctest's reset() forces 25x80).</param>
/// <param name="Columns">Initial grid columns.</param>
/// <param name="Timeout">Whole-run wall-clock cap.</param>
/// <param name="WorkingDirectory">Child working directory; null = current directory.</param>
/// <param name="TranscriptPath">Optional byte transcript of the conversation; null = off.</param>
/// <param name="Environment">Extra child environment variables (--env K=V, repeatable).</param>
/// <param name="EnableChecksumReports">Request the DECRQCRA (checksum report) core switch; applied by <see cref="ConformanceHost.ApplyCoreSwitches"/>.</param>
/// <param name="EnableWindowResize">Request the CSI 8 t window-resize core switch; applied by <see cref="ConformanceHost.ApplyCoreSwitches"/>.</param>
/// <param name="EnableTitleReports">Request opt-in title-report replies.</param>
public sealed record HostOptions(
    int Rows,
    int Columns,
    TimeSpan Timeout,
    string? WorkingDirectory,
    string? TranscriptPath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Command,
    bool EnableChecksumReports = false,
    bool EnableWindowResize = false,
    bool EnableTitleReports = false)
{
    public const int DefaultRows = 25;
    public const int DefaultColumns = 80;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
}

public static class HostOptionsParser
{
    /// <summary>Environment variable that enables the checksum core switch (1/true/yes/on).</summary>
    public const string ChecksumEnvironmentVariable = "DOTTY_CONFORMANCE_CHECKSUM";
    /// <summary>Environment variable that enables title-report replies (1/true/yes/on).</summary>
    public const string TitleReportsEnvironmentVariable = "DOTTY_CONFORMANCE_TITLE_REPORTS";

    /// <summary>Environment variable that enables the window-resize core switch (1/true/yes/on).</summary>
    public const string ResizeEnvironmentVariable = "DOTTY_CONFORMANCE_RESIZE";

    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(1);

    public const string HelpText =
        """
        dotty-conformance-host [options] -- <command> [args...]

        Runs <command> on a real PTY whose terminal side is a headless Dotty
        TerminalSession (parser + adapter + buffer, no GUI). Exits with the
        child's exit code.

        Options:
          --rows N                 initial rows (default 25)
          --columns N              initial columns (default 80)
          --timeout-seconds N      wall-clock cap, 1..86400 (default 900)
          --working-directory DIR  child working directory
          --transcript FILE        write a byte transcript (C = child output, R = terminal reply)
          --env KEY=VALUE          extra child environment variable (repeatable)
          --checksum-reports       enable the DECRQCRA core switch (or DOTTY_CONFORMANCE_CHECKSUM=1)
          --window-resize          enable the CSI 8 t core switch (or DOTTY_CONFORMANCE_RESIZE=1)
          --title-reports          enable title-report replies (or DOTTY_CONFORMANCE_TITLE_REPORTS=1)
          -h, --help               print this help and exit 0
          --version                print the host version and exit 0

        Exit codes: child's exit code; 2 usage error; 124 timeout; 125 host error; 130 interrupted.
        """;

    /// <summary>Parses host arguments; throws <see cref="ArgumentException"/> on any usage error.</summary>
    /// <param name="args">Arguments, without the program name.</param>
    /// <param name="getEnvironmentVariable">Environment lookup for the core-switch variables (defaults to the process environment).</param>
    public static HostOptions Parse(IReadOnlyList<string> args, Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        getEnvironmentVariable ??= System.Environment.GetEnvironmentVariable;

        int rows = HostOptions.DefaultRows;
        int columns = HostOptions.DefaultColumns;
        TimeSpan timeout = HostOptions.DefaultTimeout;
        string? workingDirectory = null;
        string? transcript = null;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string>? command = null;
        bool checksum = IsTruthy(getEnvironmentVariable(ChecksumEnvironmentVariable));
        bool resize = IsTruthy(getEnvironmentVariable(ResizeEnvironmentVariable));
        bool titleReports = IsTruthy(getEnvironmentVariable(TitleReportsEnvironmentVariable));

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                command = new List<string>(args.Count - i - 1);
                for (int j = i + 1; j < args.Count; j++)
                    command.Add(args[j]);
                break;
            }

            switch (arg)
            {
                case "--rows":
                    rows = ParsePositiveInt(arg, RequireValue(args, ref i));
                    break;
                case "--columns":
                    columns = ParsePositiveInt(arg, RequireValue(args, ref i));
                    break;
                case "--timeout-seconds":
                    {
                        int seconds = ParsePositiveInt(arg, RequireValue(args, ref i));
                        timeout = TimeSpan.FromSeconds(seconds);
                        if (timeout > MaxTimeout)
                            throw new ArgumentException($"{arg} must be at most {(int)MaxTimeout.TotalSeconds}.");
                        break;
                    }
                case "--working-directory":
                    workingDirectory = RequireValue(args, ref i);
                    break;
                case "--transcript":
                    transcript = RequireValue(args, ref i);
                    break;
                case "--env":
                    {
                        string pair = RequireValue(args, ref i);
                        int eq = pair.IndexOf('=');
                        if (eq <= 0)
                            throw new ArgumentException($"--env expects KEY=VALUE, got '{pair}'.");
                        environment[pair[..eq]] = pair[(eq + 1)..];
                        break;
                    }
                case "--checksum-reports":
                    checksum = true;
                    break;
                case "--window-resize":
                    resize = true;
                    break;
                case "--title-reports":
                    titleReports = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        if (command is null)
            throw new ArgumentException("Missing '--' followed by the command to run.");
        if (command.Count == 0 || string.IsNullOrWhiteSpace(command[0]))
            throw new ArgumentException("The command after '--' is empty.");

        return new HostOptions(rows, columns, timeout, workingDirectory, transcript, environment, command,
            checksum, resize, titleReports);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index)
    {
        string name = args[index];
        if (index + 1 >= args.Count)
            throw new ArgumentException($"{name} requires a value.");
        return args[++index];
    }

    private static int ParsePositiveInt(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
            throw new ArgumentException($"{name} expects a positive integer, got '{value}'.");
        return parsed;
    }

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
