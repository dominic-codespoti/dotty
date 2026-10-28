using Dotty.Conformance.Host;
using Dotty.Runtime;

internal static class Program
{
    private static int Main(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg == "--")
                break;
            if (arg is "-h" or "--help")
            {
                Console.Out.WriteLine(HostOptionsParser.HelpText);
                return 0;
            }
            if (arg == "--version")
            {
                Console.Out.WriteLine(DottyVersion.Current);
                return 0;
            }
        }

        HostOptions options;
        try
        {
            options = HostOptionsParser.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Try with --help.");
            return ConformanceHost.ExitUsage;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return ConformanceHost.Run(options, Console.Error, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
