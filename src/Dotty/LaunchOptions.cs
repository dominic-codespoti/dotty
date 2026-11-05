using System.Collections.ObjectModel;

namespace Dotty.Silk;

public sealed class DottyLaunchOptions
{
    public string? Shell { get; set; }
    public string? WorkingDirectory { get; set; }
    public IReadOnlyList<string>? Command { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }

    /// <summary>Command-line action such as <c>+explain-config</c>; null when launching a terminal. Actions never create a window.</summary>
    public string? Action { get; set; }

    /// <summary>Positional arguments that followed <see cref="Action"/>.</summary>
    public IReadOnlyList<string> ActionArguments { get; set; } = [];

    /// <summary>Action-specific flags such as <c>--force</c>, each at most once; only flags the action accepts get here.</summary>
    public IReadOnlyList<string> ActionFlags { get; set; } = [];

    public bool HasActionFlag(string flag)
    {
        foreach (string candidate in ActionFlags)
        {
            if (candidate == flag)
                return true;
        }
        return false;
    }

    public string EffectiveWorkingDirectory => WorkingDirectory ?? Environment.CurrentDirectory;

    public void ValidateForLaunch()
    {
        string cwd;
        try
        {
            cwd = Path.GetFullPath(EffectiveWorkingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            throw new ArgumentException($"Invalid working directory '{EffectiveWorkingDirectory}': {ex.Message}", ex);
        }

        if (!Directory.Exists(cwd))
            throw new ArgumentException($"Working directory '{cwd}' does not exist or is not a directory.");
        WorkingDirectory = cwd;

        if (Shell is not null)
            Shell = ValidateExecutable(Shell, "shell", Environment.CurrentDirectory);
        if (Command is { Count: > 0 })
            _ = ValidateExecutable(Command[0], "command", cwd);
    }

    private static string ValidateExecutable(string executable, string kind, string relativeBase)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException($"The {kind} executable must not be empty.");

        string? resolved = FindExecutable(executable, relativeBase);
        if (resolved is null)
            throw new ArgumentException($"The {kind} executable '{executable}' was not found or is not executable.");
        return resolved;
    }

    private static string? FindExecutable(string executable, string relativeBase)
    {
        if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            string candidate = Path.IsPathFullyQualified(executable)
                ? Path.GetFullPath(executable)
                : Path.GetFullPath(executable, relativeBase);
            return IsUsableExecutable(candidate) ? candidate : null;
        }

        string? searchPath = Environment.GetEnvironmentVariable("PATH");
        if (searchPath is null)
            return null;
        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD").Split(';')
            : [string.Empty];
        foreach (string directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsUsableExecutable(Path.Combine(directory, executable)))
                return executable;
            foreach (string extension in extensions)
            {
                if (extension.Length == 0 || executable.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsUsableExecutable(Path.Combine(directory, executable + extension)))
                    return executable;
            }
        }
        return null;
    }

    private static bool IsUsableExecutable(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

public static class DottyLaunchOptionsParser
{
    public const string ExplainConfigAction = "+explain-config";
    public const string InitConfigAction = "+init-config";
    public const string ShowConfigAction = "+show-config";
    public const string ValidateConfigAction = "+validate-config";
    public const string ListThemesAction = "+list-themes";
    public const string DiagnoseAction = "+diagnose";

    public const string ForceFlag = "--force";
    public const string DefaultFlag = "--default";
    public const string GlFlag = "--gl";

    private sealed record ActionSpec(string Name, int MaxArguments, string[] Flags);

    private static readonly ActionSpec[] Actions =
    [
        new(ExplainConfigAction, 1, []),
        new(InitConfigAction, 0, [ForceFlag]),
        new(ShowConfigAction, 0, [DefaultFlag]),
        new(ValidateConfigAction, 0, []),
        new(ListThemesAction, 0, []),
        new(DiagnoseAction, 0, [GlFlag]),
    ];

    public const string HelpText = """
Usage: dotty [--working-directory DIR|-d DIR] [--shell PATH] [--] [COMMAND [ARG...]]
       dotty --help|-h
       dotty --version
       dotty +ACTION [ARG...]

Options:
  -h, --help                    Show this help and exit
      --version                 Show version and exit
  -d, --working-directory DIR   Start in DIR
      --shell PATH              Start PATH as the interactive shell
  --                            End Dotty options; run COMMAND with exact arguments

Actions (print and exit without opening a window):
  +explain-config [OPTION]      List every config option, or document OPTION
                                (JSON path or Lua key, e.g. font.size)
  +init-config [--force]        Write a commented starter config.json; --force
                                replaces an existing file
  +show-config [--default]      Print the effective config.json settings as
                                JSON, or the built-in defaults with --default
  +validate-config              Check config.json and config.lua; exit 1 and
                                print file:line errors when something is wrong
  +list-themes                  List built-in and user theme names
  +diagnose [--gl]              Print version, platform, paths, config status
                                and helper locations; --gl also probes OpenGL
""";

    public static DottyLaunchOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new DottyLaunchOptions();
        int index = 0;
        bool sawShell = false;
        IReadOnlyList<string>? command = null;
        List<string>? actionArguments = null;
        List<string>? actionFlags = null;
        ActionSpec? spec = null;

        while (index < args.Count)
        {
            string arg = args[index];
            if (arg == "--")
            {
                index++;
                if (index == args.Count)
                    throw new ArgumentException("Expected a command after '--'.");
                var argv = new string[args.Count - index];
                for (int i = 0; i < argv.Length; i++)
                    argv[i] = args[index + i];
                command = new ReadOnlyCollection<string>(argv);
                break;
            }

            if (arg.StartsWith('+'))
            {
                if (options.Action is not null)
                    throw new ArgumentException($"Only one action may be given; found '{options.Action}' and '{arg}'.");
                spec = FindAction(arg) ?? throw new ArgumentException($"Unknown action '{arg}'.");
                options.Action = arg;
                actionArguments = [];
                actionFlags = [];
                index++;
                continue;
            }

            switch (arg)
            {
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    index++;
                    break;
                case "--version":
                    options.ShowVersion = true;
                    index++;
                    break;
                case "--working-directory":
                case "-d":
                    options.WorkingDirectory = GetValue(args, ref index, arg);
                    break;
                case "--shell":
                    options.Shell = GetValue(args, ref index, arg);
                    sawShell = true;
                    break;
                default:
                    if (spec is not null && !arg.StartsWith('-'))
                    {
                        actionArguments!.Add(arg);
                        index++;
                        break;
                    }
                    if (spec is not null && Array.IndexOf(spec.Flags, arg) >= 0)
                    {
                        if (actionFlags!.Contains(arg))
                            throw new ArgumentException($"'{arg}' may be given only once.");
                        actionFlags.Add(arg);
                        index++;
                        break;
                    }
                    throw new ArgumentException($"Unknown option or unexpected argument '{arg}'. Use '--' before a command.");
            }
        }

        if (options.ShowHelp && options.ShowVersion)
            throw new ArgumentException("Choose either '--help' or '--version'.");
        if (sawShell && command is not null)
            throw new ArgumentException("'--shell' cannot be combined with a command.");
        if (options.Action is not null)
        {
            if (sawShell || options.WorkingDirectory is not null || command is not null)
                throw new ArgumentException($"'{options.Action}' cannot be combined with '--shell', '--working-directory', or a command.");
            if (actionArguments!.Count > spec!.MaxArguments)
            {
                throw new ArgumentException(spec.MaxArguments == 0
                    ? $"'{options.Action}' takes no arguments; found '{actionArguments[0]}'."
                    : $"'{options.Action}' accepts at most one OPTION.");
            }
            options.ActionArguments = actionArguments;
            options.ActionFlags = actionFlags!;
        }
        options.Command = command;
        return options;
    }

    private static ActionSpec? FindAction(string name)
    {
        foreach (ActionSpec spec in Actions)
        {
            if (spec.Name == name)
                return spec;
        }
        return null;
    }

    private static string GetValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || args[index] == "--")
            throw new ArgumentException($"Option '{option}' requires a value.");
        string value = args[index++];
        if (value.Length == 0)
            throw new ArgumentException($"Option '{option}' requires a non-empty value.");
        return value;
    }
}
