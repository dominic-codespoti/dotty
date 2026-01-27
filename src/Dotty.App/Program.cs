using System;
using System.IO;
using Avalonia;

namespace Dotty.App;

static class Program
{
    public static void Main(string[] args)
    {
        // Optional global silencing of Console output to avoid flooding the
        // terminal when debugging/logging is still present in code. Set
        // DOTTY_DISABLE_LOGGING=1 in your environment to enable this.
            // Environment-driven global silencing removed; keep Console as-is.

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace()
            .UsePlatformDetect()
            .UseSkia();

        return builder;
    }
}
