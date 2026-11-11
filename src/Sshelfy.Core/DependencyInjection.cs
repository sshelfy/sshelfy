using Microsoft.Extensions.DependencyInjection;
using Sshelfy.Core.History;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Services;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Snippets;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddSshelfyCore(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IHostCatalog, HostCatalog>();
        services.AddSingleton<IHostKeyVerifier, KnownHostsVerifier>();
        services.AddSingleton<TransferQueue>();
        services.AddSingleton<Import.SshConfigImporter>();
        services.AddSingleton<ISnippetLibrary, SnippetLibrary>();
        services.AddSingleton<ICommandHistory, CommandHistory>();
        services.AddSingleton<Activity.IActivityLog, Activity.ActivityLog>();
        services.AddSingleton<Backup.BackupService>();
        return services;
    }
}
