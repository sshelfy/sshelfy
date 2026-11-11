using Avalonia;
using Avalonia.Headless;
using Sshelfy.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Sshelfy.App.Tests;

/// <summary>Starts the real <see cref="App"/> (styles, theme, templates) on the headless platform, with data in a temp folder.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("SSHELFY_DATA_DIR", Path.Combine(Path.GetTempPath(), "sshelfy-ui-tests-" + Environment.ProcessId));
        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}