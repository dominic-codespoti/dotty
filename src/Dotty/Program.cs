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
                Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
                return 0;
            }

            options.ValidateForLaunch();
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
