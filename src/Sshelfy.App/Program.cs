using Avalonia;

namespace Sshelfy.App;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or any SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var services = Bootstrapper.BuildServices();
        BuildAvaloniaApp(() => new App(services)).StartWithClassicDesktopLifetime(args);
    }

    // Used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(() => new App());

    private static AppBuilder BuildAvaloniaApp(Func<App> factory)
        => AppBuilder.Configure(factory)
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
