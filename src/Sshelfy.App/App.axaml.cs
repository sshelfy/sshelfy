using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels;
using Sshelfy.App.Views;
using Sshelfy.Core.History;
using Sshelfy.Core.Services;

namespace Sshelfy.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
    }

    // Required by the XAML previewer.
    public App() : this(Bootstrapper.BuildServices())
    {
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("sshelfy");
        logger.LogInformation("sshelfy {Version} starting on {OS} (.NET {Runtime})",
            typeof(App).Assembly.GetName().Version, Environment.OSVersion, Environment.Version);
        HookUnhandledExceptions(logger);

        var settings = _services.GetRequiredService<ISettingsService>();
        var removed = _services.GetRequiredService<Sshelfy.Core.Terminal.ISessionLogStore>().DeleteOlderThan(settings.Current.SessionLogRetentionDays);
        if (removed > 0)
            logger.LogInformation("Deleted {Count} session recording(s) past the retention period", removed);
        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);
        _services.GetRequiredService<TerminalThemeService>().Start();
        SessionLauncher.DeleteSecurityKeyFiles();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = _services.GetRequiredService<ShellViewModel>();
            desktop.MainWindow = new ShellWindow { DataContext = shell };
            desktop.MainWindow.Opened += async (_, _) =>
            {
                await shell.RestoreTabsAsync();
                await shell.Updates.CheckInBackgroundAsync();
            };
            _services.GetRequiredService<VaultAutoLocker>().Attach(desktop.MainWindow);
            var history = _services.GetRequiredService<ICommandHistory>();
            desktop.Exit += (_, _) =>
            {
                settings.FlushAsync().GetAwaiter().GetResult();
                history.FlushAsync().GetAwaiter().GetResult();
                _services.GetRequiredService<Sshelfy.Core.Activity.IActivityLog>().FlushAsync().GetAwaiter().GetResult();
                SessionLauncher.DeleteSecurityKeyFiles();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Unexpected errors go to the diagnostic log; UI-thread ones do not close the app.</summary>
    private void HookUnhandledExceptions(ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true;
            _services.GetRequiredService<StatusBarViewModel>().Set("Something went wrong. Details are in the diagnostic log.", StatusLevel.Error);
        };
    }
}
