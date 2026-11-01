using Dotty.Runtime;

namespace Dotty.Silk;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            DottyLaunchOptions options = DottyLaunchOptionsParser.Parse(args);
            if (options.ShowHelp)
            {
                WindowsConsole.AttachParentConsole();
                Console.Out.WriteLine(DottyLaunchOptionsParser.HelpText);
                return 0;
            }
            if (options.ShowVersion)
            {
                WindowsConsole.AttachParentConsole();
                Console.Out.WriteLine(DottyVersion.Describe());
                return 0;
            }

            // Actions print and exit: no window or config watcher.
            if (options.Action is not null)
            {
                WindowsConsole.AttachParentConsole();
                return CliActions.Run(options, Console.Out, Console.Error);
            }

            options.ValidateForLaunch();
            // Local crash log for the GUI process only; actions and usage errors never write one.
            Dotty.Runtime.Diagnostics.CrashReporter.Install(new Dotty.Runtime.Diagnostics.CrashLog(
                Dotty.Runtime.Diagnostics.CrashLog.DirectoryFor(Dotty.Runtime.Config.PlatformPaths.StateRoot)));
            return DottyWindowHost.Run(options);
        }
        catch (ArgumentException ex)
        {
            WindowsConsole.AttachParentConsole();
            Console.Error.WriteLine($"dotty: {ex.Message}");
            Console.Error.WriteLine("Try 'dotty --help' for usage.");
            return 2;
        }
        catch (Exception ex)
        {
            WindowsConsole.AttachParentConsole();
            Console.Error.WriteLine($"dotty: {ex.Message}");
            Dotty.Runtime.Diagnostics.CrashReporter.ReportFatal(ex, Console.Error);
            return 1;
        }
    }
}
