using Avalonia;

namespace Dotty.App;

static class Program
{
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .LogToTrace();

        try
        {
            builder = builder.UsePlatformDetect();
        }
        catch
        {
            try { builder = builder.UseX11(); } catch { }
        }

        return builder;
    }
}
