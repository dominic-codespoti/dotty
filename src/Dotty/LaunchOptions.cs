using System.Collections.ObjectModel;

namespace Dotty.Silk;

public sealed class DottyLaunchOptions
{
    public string? Shell { get; set; }
    public string? WorkingDirectory { get; set; }
    public IReadOnlyList<string>? Command { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }

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
    public const string HelpText = """
Usage: dotty [--working-directory DIR|-d DIR] [--shell PATH] [--] [COMMAND [ARG...]]
       dotty --help|-h
       dotty --version

Options:
  -h, --help                    Show this help and exit
      --version                 Show version and exit
  -d, --working-directory DIR   Start in DIR
      --shell PATH              Start PATH as the interactive shell
  --                            End Dotty options; run COMMAND with exact arguments
""";

    public static DottyLaunchOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new DottyLaunchOptions();
        int index = 0;
        bool sawShell = false;
        IReadOnlyList<string>? command = null;

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
                    throw new ArgumentException($"Unknown option or unexpected argument '{arg}'. Use '--' before a command.");
            }
        }

        if (options.ShowHelp && options.ShowVersion)
            throw new ArgumentException("Choose either '--help' or '--version'.");
        if (sawShell && command is not null)
            throw new ArgumentException("'--shell' cannot be combined with a command.");
        options.Command = command;
        return options;
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
