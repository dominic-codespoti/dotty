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
                Console.Out.WriteLine(DottyLaunchOptionsParser.HelpText);
                return 0;
            }
            if (options.ShowVersion)
            {
                Console.Out.WriteLine(DottyVersion.Describe());
                return 0;
            }

            // Actions print and exit: no window, config watcher, or console detach.
            if (options.Action is not null)
                return CliActions.Run(options, Console.Out, Console.Error);

            options.ValidateForLaunch();
            // Local crash log for the GUI process only; actions and usage errors never write one.
            Dotty.Runtime.Diagnostics.CrashReporter.Install(new Dotty.Runtime.Diagnostics.CrashLog(
                Dotty.Runtime.Diagnostics.CrashLog.DirectoryFor(Dotty.Runtime.Config.PlatformPaths.StateRoot)));
            WindowsConsole.DetachOwnedConsole();
            return DottyWindowHost.Run(options);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"dotty: {ex.Message}");
            Console.Error.WriteLine("Try 'dotty --help' for usage.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotty: {ex.Message}");
            Dotty.Runtime.Diagnostics.CrashReporter.ReportFatal(ex, Console.Error);
            return 1;
        }
    }
}
