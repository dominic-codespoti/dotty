using Avalonia;

namespace Dotty.App;

static class Program
{
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseX11()
            .UseSkia()
            .WithInterFont()
            .LogToTrace();
}
