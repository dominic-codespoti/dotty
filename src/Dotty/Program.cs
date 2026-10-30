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

            // Actions print and exit: no config load, console detach, or graphics.
            if (options.Action is not null)
                return CliActions.Run(options.Action, options.ActionArguments, Console.Out, Console.Error);

            options.ValidateForLaunch();
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
            return 1;
        }
    }
}
