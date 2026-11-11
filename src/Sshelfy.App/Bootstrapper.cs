using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.App.ViewModels.Sidebar;
using Sshelfy.Core;
using Sshelfy.Core.History;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;
using Sshelfy.Core.Snippets;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure;

namespace Sshelfy.App;

/// <summary>Composition root.</summary>
internal static class Bootstrapper
{
    public static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
#if WINDOWS
        // Registered first: the infrastructure layer only adds a no-op signer when none is present.
        services.AddSingleton<IHelloSigner, Sshelfy.Infrastructure.Windows.WindowsHelloSigner>();
        services.AddSingleton<Sshelfy.Core.Terminal.ILocalShellLauncher, Sshelfy.Infrastructure.Windows.ConPtyShellLauncher>();
#endif
        services
            .AddSshelfyCore()
            .AddSshelfyInfrastructure(Environment.GetEnvironmentVariable("SSHELFY_DATA_DIR"));

        // Providers (the diagnostic file log) are registered by the infrastructure layer.
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));

        // UI services
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<TerminalThemeService>();
        services.AddSingleton<IAttentionService, AttentionService>();
        services.AddSingleton<LayoutService>();
        services.AddSingleton<PreConnectGate>();
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton<Sshelfy.Core.Services.IUpdateChecker>(sp => new Sshelfy.Infrastructure.GitHubUpdateChecker(
            sp.GetRequiredService<HttpClient>(), AppInfo.Repository,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Sshelfy.Infrastructure.GitHubUpdateChecker>>()));
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IShellIntegration, ShellIntegration>();
        services.AddSingleton<DialogHostViewModel>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogHostViewModel>());
        services.AddSingleton<IVaultGate, VaultGate>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<ISessionLauncher, SessionLauncher>();
        services.AddSingleton<IConnectionResolver, ConnectionResolver>();
        services.AddSingleton<IHostKeyPrompt, HostKeyPrompt>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ITerminalCommandSender, TerminalCommandSender>();
        services.AddSingleton<VaultAutoLocker>();
        services.AddSingleton<IKeyService, KeyService>();
        services.AddSingleton<Sshelfy.Core.Sftp.ITransferConflictResolver, TransferConflictResolver>();
        services.AddSingleton<CommandHistoryRecorder>();

        // View models
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<BottomPanelViewModel>();
        services.AddSingleton<ActivityPanelViewModel>();
        services.AddSingleton<Sshelfy.App.ViewModels.Tunnels.TunnelsPanelViewModel>();
        services.AddSingleton<HostsSidebarViewModel>();
        services.AddSingleton<SnippetsSidebarViewModel>();
        services.AddSingleton<HistorySidebarViewModel>();
        services.AddSingleton<RecordingsSidebarViewModel>();

        var provider = services.BuildServiceProvider();

        // Local state is small and needed before the first window is shown (theme, layout, hosts).
        Task.Run(async () =>
        {
            await provider.GetRequiredService<ISettingsService>().InitializeAsync();
            await provider.GetRequiredService<ISecretVault>().InitializeAsync();
            await provider.GetRequiredService<IHostCatalog>().InitializeAsync();
            await provider.GetRequiredService<ISnippetLibrary>().InitializeAsync();
            await provider.GetRequiredService<ICommandHistory>().InitializeAsync();
            await provider.GetRequiredService<Sshelfy.Core.Activity.IActivityLog>().InitializeAsync();
        }).GetAwaiter().GetResult();

        // Starts observing terminal tabs so their commands land in the history.
        provider.GetRequiredService<CommandHistoryRecorder>();
        return provider;
    }
}
