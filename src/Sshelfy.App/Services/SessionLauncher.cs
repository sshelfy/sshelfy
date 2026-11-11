using Sshelfy.App.ViewModels;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Services;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.Services;

/// <summary>Opens terminal and SFTP tabs for hosts.</summary>
public interface ISessionLauncher
{
    Task ConnectAsync(HostProfile host);

    /// <param name="password">A password already known for the host (e.g. from its terminal), so it is not asked again.</param>
    /// <param name="startPath">Folder to show first (e.g. the terminal's current folder); the home folder when null.</param>
    Task OpenSftpAsync(HostProfile host, string? password = null, string? startPath = null);

    Task QuickConnectAsync(string? input = null);

    /// <summary>Opens a second session to the same host as a pane next to <paramref name="source"/>.</summary>
    Task SplitAsync(TerminalTabViewModel source, bool sideBySide);

    /// <summary>Opens a local shell (PowerShell, cmd, WSL…) in a terminal tab.</summary>
    Task OpenLocalAsync(Sshelfy.Core.Terminal.LocalShellProfile profile);

    /// <summary>A terminal for a saved host, not yet opened or connected (layouts place it themselves).</summary>
    TerminalTabViewModel CreateTerminalTab(HostProfile host);

    /// <summary>A local shell terminal, not yet opened or started.</summary>
    TerminalTabViewModel CreateLocalTab(Sshelfy.Core.Terminal.LocalShellProfile profile);
}

internal sealed class SessionLauncher : ISessionLauncher
{
    private readonly WorkspaceViewModel _workspace;
    private readonly ISshConnectionFactory _connections;
    private readonly IConnectionResolver _resolver;
    private readonly IHostCatalog _catalog;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IVaultGate _vaultGate;
    private readonly StatusBarViewModel _status;
    private readonly ISftpSessionFactory _sftp;
    private readonly TransferQueue _transfers;
    private readonly IFilePickerService _files;
    private readonly IClipboardService _clipboard;
    private readonly Sshelfy.Core.Terminal.ISessionLogStore _logs;
    private readonly Sshelfy.Core.Activity.IActivityLog _activity;
    private readonly ILocalFileSystem _localFiles;
    private readonly IShellIntegration _shell;
    private readonly Sshelfy.Core.Terminal.ILocalShellLauncher _localShells;
    private readonly IAttentionService? _attention;
    private readonly PreConnectGate? _preConnect;

    public SessionLauncher(
        WorkspaceViewModel workspace,
        ISshConnectionFactory connections,
        IConnectionResolver resolver,
        IHostCatalog catalog,
        ISettingsService settings,
        IDialogService dialogs,
        IVaultGate vaultGate,
        StatusBarViewModel status,
        ISftpSessionFactory sftp,
        TransferQueue transfers,
        IFilePickerService files,
        IClipboardService clipboard,
        Sshelfy.Core.Terminal.ISessionLogStore logs,
        Sshelfy.Core.Activity.IActivityLog activity,
        ILocalFileSystem localFiles,
        IShellIntegration shell,
        Sshelfy.Core.Terminal.ILocalShellLauncher localShells,
        IAttentionService? attention = null,
        PreConnectGate? preConnect = null)
    {
        _preConnect = preConnect;
        _attention = attention;
        _localShells = localShells;
        _activity = activity;
        _localFiles = localFiles;
        _shell = shell;
        _logs = logs;
        _sftp = sftp;
        _transfers = transfers;
        _files = files;
        _clipboard = clipboard;
        _workspace = workspace;
        _connections = connections;
        _resolver = resolver;
        _catalog = catalog;
        _settings = settings;
        _dialogs = dialogs;
        _vaultGate = vaultGate;
        _status = status;
    }

    public Task ConnectAsync(HostProfile host) => OpenTerminalAsync(host, password: null);

    public async Task OpenSftpAsync(HostProfile host, string? password = null, string? startPath = null)
    {
        var existing = _workspace.Find<SftpTabViewModel>(t => t.Host.Id == host.Id && t.IsReady);
        if (existing is not null)
        {
            _workspace.ActiveTab = existing;
            if (startPath is not null)
                await existing.NavigateToAsync(startPath);
            return;
        }

        startPath ??= string.IsNullOrWhiteSpace(host.SftpStartPath) ? null : host.SftpStartPath.Trim();
        var tab = new SftpTabViewModel(host, password, startPath, _sftp, _resolver, _transfers, _dialogs, _files, _settings, _clipboard, _workspace, _status, _activity, _localFiles, _shell);
        _workspace.Open(tab);
        await tab.ConnectAsync();
    }

    public async Task QuickConnectAsync(string? input = null)
    {
        var request = await _dialogs.ShowAsync(new QuickConnectDialogViewModel(input));
        if (request is null)
            return;

        var host = request.Host;
        if (request.SaveHost)
        {
            var password = request.Password is null ? (SecretUpdate?)null : new SecretUpdate(request.Password);
            if (password is not null && !await _vaultGate.EnsureUnlockedAsync("Passwords are stored in the encrypted vault."))
                password = null;
            await _catalog.SaveHostAsync(host, password);
        }

        await OpenTerminalAsync(host, request.Password);
    }

    public async Task SplitAsync(TerminalTabViewModel source, bool sideBySide)
    {
        var pane = source.IsExternalSsh ? CreateTerminal(source.Host, null)
            : source.LocalShell is { } local ? CreateLocal(local)
            : CreateTerminal(source.Host, source.KnownPassword);
        _workspace.Split(source, pane, sideBySide);
        await pane.ConnectAsync();
    }

    private async Task OpenTerminalAsync(HostProfile host, string? password)
    {
        var tab = CreateTerminal(host, password);
        _workspace.Open(tab);
        await tab.ConnectAsync();
    }

    public async Task OpenLocalAsync(Sshelfy.Core.Terminal.LocalShellProfile profile)
    {
        var tab = CreateLocal(profile);
        _workspace.Open(tab);
        await tab.ConnectAsync();
    }

    /// <summary>Before ssh.exe starts: the pre-connect command and knocks of every hop, then the security key file.</summary>
    private async Task PrepareExternalAsync(HostProfile host, IReadOnlyList<HostProfile> jumps, string? keyFile, CancellationToken token)
    {
        if (_preConnect is not null)
        {
            // jumps run from the first hop (reached directly) to the target.
            HostProfile[] chain = [.. jumps, host];
            for (var i = 0; i < chain.Length; i++)
            {
                try
                {
                    if (!await _preConnect.RunAsync(chain[i], knock: i == 0, token))
                        throw new Sshelfy.Core.Terminal.LocalShellException("Connection cancelled.");
                }
                catch (SshConnectionFailedException ex)
                {
                    throw new Sshelfy.Core.Terminal.LocalShellException(ex.Message, ex);
                }
            }
        }

        if (keyFile is not null)
            await WriteSecurityKeyAsync(host, keyFile);
    }

    /// <summary>Where a security key is written for ssh.exe; the file only holds a handle the authenticator needs to sign.</summary>
    private static string SecurityKeyFile(Guid keyId) => Path.Combine(SecurityKeyFolder, keyId.ToString("N"));

    private static string SecurityKeyFolder => Path.Combine(Path.GetTempPath(), "sshelfy", "keys");

    /// <summary>Removes security key files written for ssh.exe (called at start-up and exit; ssh.exe only reads them while logging in).</summary>
    public static void DeleteSecurityKeyFiles()
    {
        try
        {
            if (Directory.Exists(SecurityKeyFolder))
                Directory.Delete(SecurityKeyFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A session is logging in right now; the next start cleans up.
        }
    }

    /// <summary>Writes the key (and its certificate, which ssh.exe picks up next to it) from the vault before ssh.exe starts.</summary>
    private async Task WriteSecurityKeyAsync(HostProfile host, string path)
    {
        if (!await _vaultGate.EnsureUnlockedAsync("The security key is stored in the encrypted vault."))
            throw new Sshelfy.Core.Terminal.LocalShellException("The vault is locked, so the security key cannot be used.");

        var target = await _catalog.BuildTargetAsync(host);
        if (string.IsNullOrEmpty(target.PrivateKey))
            throw new Sshelfy.Core.Terminal.LocalShellException("The security key is missing from the vault. Import it again in the host settings.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, target.PrivateKey.Trim() + "\n");
        var certificate = path + "-cert.pub";
        if (target.Certificate is { } cert)
            await File.WriteAllTextAsync(certificate, cert + "\n");
        else
            File.Delete(certificate);
    }

    /// <summary>X11 windows only appear when an X server listens on the display; say so instead of failing silently.</summary>
    private async Task WarnWithoutXServerAsync(string display)
    {
        if (OpenSshCommandLine.X11Endpoint(display) is not { } endpoint)
            return;

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            _status.Set($"No X server on {display}: start VcXsrv, X410 or Xming to see X11 windows (display set in Settings).", StatusLevel.Error);
        }
    }

    public TerminalTabViewModel CreateTerminalTab(HostProfile host) => CreateTerminal(host, password: null);

    public TerminalTabViewModel CreateLocalTab(Sshelfy.Core.Terminal.LocalShellProfile profile) => CreateLocal(profile);

    private TerminalTabViewModel CreateLocal(Sshelfy.Core.Terminal.LocalShellProfile profile)
    {
        // A stand-in host so the tab, history and recordings have a name; it is never saved.
        var host = new HostProfile
        {
            Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("local:" + profile.Id))),
            Name = profile.Name,
            Address = "localhost",
            Username = Environment.UserName,
        };
        return new TerminalTabViewModel(host, null, _connections, _resolver, _catalog, _settings, _status, _logs, _dialogs, _vaultGate, _activity, profile, _localShells, _attention);
    }

    private TerminalTabViewModel CreateTerminal(HostProfile host, string? password)
    {
        var securityKey = host.AuthMethod == AuthMethod.PrivateKey && host.KeyId is { } keyId && _catalog.FindKey(keyId) is { IsSecurityKey: true } key ? key : null;
        if (host.ForwardAgent || host.ForwardX11 || securityKey is not null)
        {
            if (_localShells.FindOpenSsh() is { } ssh)
            {
                // ssh.exe handles agent forwarding; it asks for passwords and host keys in the terminal itself.
                var jumps = new List<HostProfile>();
                for (var hop = host; hop.JumpHostId is { } id && _catalog.FindHost(id) is { } jump && !jumps.Contains(jump);)
                {
                    jumps.Insert(0, jump);
                    hop = jump;
                }

                var display = OpenSshCommandLine.IsValidDisplay(_settings.Current.X11Display) ? _settings.Current.X11Display : "localhost:0.0";
                var keyFile = securityKey is null ? null : SecurityKeyFile(securityKey.Id);
                string arguments;
                try
                {
                    arguments = OpenSshCommandLine.Build(host, jumps, host.ForwardAgent, host.ForwardX11, keyFile);
                }
                catch (ArgumentException ex)
                {
                    // An address or user name that ssh.exe could take for an option: never hand it over.
                    _status.Set(ex.Message, StatusLevel.Error);
                    return new(host, password, _connections, _resolver, _catalog, _settings, _status, _logs, _dialogs, _vaultGate, _activity, attention: _attention);
                }

                var profile = new Sshelfy.Core.Terminal.LocalShellProfile(
                    "ssh:" + host.Id.ToString("N"), host.DisplayName, ssh, arguments,
                    host.ForwardX11 ? new Dictionary<string, string> { ["DISPLAY"] = display } : null,
                    token => PrepareExternalAsync(host, jumps, keyFile, token));
                if (host.ForwardX11)
                    _ = WarnWithoutXServerAsync(display);
                return new TerminalTabViewModel(host, null, _connections, _resolver, _catalog, _settings, _status, _logs, _dialogs, _vaultGate, _activity, profile, _localShells, _attention);
            }

            if (securityKey is not null)
            {
                _status.Set("Security keys (FIDO2) need the Windows OpenSSH client (ssh.exe).", StatusLevel.Error);
            }
            else
            {
                _status.Set($"{(host.ForwardX11 ? "X11" : "Agent")} forwarding needs the Windows OpenSSH client (ssh.exe); connecting without it.", StatusLevel.Error);
            }
        }

        return new(host, password, _connections, _resolver, _catalog, _settings, _status, _logs, _dialogs, _vaultGate, _activity, attention: _attention);
    }
}
