using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;
using Sshelfy.Infrastructure.Logging;
using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Security;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Security;
using Sshelfy.Infrastructure.Ssh;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSshelfyInfrastructure(this IServiceCollection services, string? dataDirectory = null)
    {
        var paths = new StoragePaths(dataDirectory);
        services.AddSingleton(paths);

        // Diagnostic log; the level follows the setting at runtime.
        services.AddSingleton<ILoggerProvider>(sp => new FileLoggerProvider(
            Path.Combine(paths.Root, "logs"),
            () => sp.GetRequiredService<ISettingsService>().Current.DiagnosticLevel switch
            {
                DiagnosticLevel.Errors => LogLevel.Warning,
                DiagnosticLevel.Verbose => LogLevel.Debug,
                _ => LogLevel.Information,
            }));
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IHostRepository, JsonHostRepository>();
        services.AddSingleton<Core.Terminal.ISessionLogStore, SessionLogStore>();
        services.AddSingleton<ISnippetRepository, JsonSnippetRepository>();
        services.AddSingleton<ICommandHistoryStore, VaultedCommandHistoryStore>();
        services.AddSingleton<IActivityStore, JsonActivityStore>();

        services.AddSingleton<IDeviceKeyProtector, DeviceKeyProtector>();
        services.TryAddSingleton<IHelloSigner, NoHelloSigner>();
        services.TryAddSingleton<Core.Terminal.ILocalShellLauncher, Core.Terminal.NoLocalShells>();
        services.AddSingleton<ISecretVault, SecretVault>();
        services.AddSingleton<ISshKeyInspector, SshKeyInspector>();
        services.AddSingleton<ISshKeyGenerator, SshKeyGenerator>();

        services.AddSingleton<IKnownHostsStore, JsonKnownHostsStore>();
        services.AddSingleton<Core.Import.ILocalSshFiles, LocalSshFiles>();
        services.AddSingleton<Core.Sftp.ILocalFileSystem, LocalFileSystem>();
        services.AddSingleton<Core.Backup.IBackupFile, EncryptedBackupFile>();
        services.AddSingleton<ISshConnectionFactory, SshNetConnectionFactory>();
        services.AddSingleton<ISftpSessionFactory, SshNetSftpSessionFactory>();
        services.AddSingleton<ISshAgentClient, Ssh.Agent.SshAgentClient>();
        services.AddSingleton<Core.Hosts.IPreConnectRunner, PreConnectRunner>();
        return services;
    }
}
