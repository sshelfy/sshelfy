using Sshelfy.Core.Security;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Hosts;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed record PortForwardItem(Sshelfy.Core.Ssh.PortForwardRule Rule)
{
    public string Description => Rule.Describe();

    public string Kind => Rule.Kind switch
    {
        Sshelfy.Core.Ssh.ForwardKind.Local => "Local",
        Sshelfy.Core.Ssh.ForwardKind.Remote => "Remote",
        _ => "SOCKS",
    };

    public bool AutoStart => Rule.AutoStart;
}

public sealed record JumpHostOption(Guid? Id, string Name, string Detail);

public sealed record AgentKeyOption(string? Fingerprint, string Name, string Detail);

public sealed record GroupOption(Guid? Id, string Name, int Depth)
{
    public string Display => new string(' ', Depth * 3) + Name;
}

/// <summary>Creates or edits a saved host. Saving writes metadata and secrets through the catalog.</summary>
public sealed partial class HostEditorDialogViewModel : DialogViewModel<HostProfile>
{
    private readonly IHostCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly IVaultGate _vaultGate;
    private readonly IFilePickerService _files;
    private readonly Sshelfy.Core.Ssh.ISshAgentClient _agent;
    private readonly HostProfile? _original;

    public HostEditorDialogViewModel(
        IHostCatalog catalog,
        IDialogService dialogs,
        IVaultGate vaultGate,
        IFilePickerService files,
        Sshelfy.Core.Ssh.ISshAgentClient agent,
        HostProfile? original,
        Guid? defaultGroupId,
        bool hasSavedPassword,
        IKeyService? keys = null)
    {
        _keys = keys;
        _catalog = catalog;
        _dialogs = dialogs;
        _vaultGate = vaultGate;
        _files = files;
        _agent = agent;
        _original = original;
        HasSavedPassword = hasSavedPassword;

        Groups = new ObservableCollection<GroupOption>(BuildGroupOptions(catalog));
        Keys = new ObservableCollection<SshKeyProfile>(catalog.Keys.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase));

        var host = original ?? new HostProfile { GroupId = defaultGroupId };
        Name = host.Name;
        Address = host.Address;
        Port = host.Port.ToString();
        Username = host.Username;
        Notes = host.Notes ?? string.Empty;
        StartupCommand = host.StartupCommand ?? string.Empty;
        EnvironmentText = StartupScript.Format(host.Environment);
        SftpStartPath = host.SftpStartPath ?? string.Empty;
        PreConnectCommand = host.PreConnectCommand ?? string.Empty;
        KnockText = PortKnock.Format(host.KnockSequence);
        KnockDelay = host.KnockDelayMilliseconds;
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == host.GroupId) ?? Groups[0];
        _agentFingerprint = host.AgentKeyFingerprint;
        SelectedAuth = host.AuthMethod;
        SelectedKey = Keys.FirstOrDefault(k => k.Id == host.KeyId) ?? Keys.FirstOrDefault();
        SavePassword = original is null || hasSavedPassword;
        AlwaysRecord = host.AlwaysRecordSession;
        ForwardAgent = host.ForwardAgent;
        ForwardX11 = host.ForwardX11;
        PortForwards = new ObservableCollection<PortForwardItem>(host.PortForwards.Select(r => new PortForwardItem(r)));

        // Any saved host except this one and hosts that already jump through it.
        JumpHosts = new ObservableCollection<JumpHostOption>(
            catalog.Hosts
                .Where(h => h.Id != host.Id && !catalog.CreatesJumpLoop(host with { JumpHostId = h.Id }))
                .OrderBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(h => new JumpHostOption(h.Id, h.DisplayName, h.Endpoint))
                .Prepend(new JumpHostOption(null, "Direct connection", string.Empty)));
        SelectedJumpHost = JumpHosts.FirstOrDefault(j => j.Id == host.JumpHostId) ?? JumpHosts[0];
        IsGeneralPage = true;
    }

    public override string Title => _original is null ? "New host" : "Edit host";

    public override double Width => 500;

    public ObservableCollection<GroupOption> Groups { get; }

    public ObservableCollection<SshKeyProfile> Keys { get; }

    public bool HasSavedPassword { get; }

    public string PasswordPlaceholder => HasSavedPassword ? "Saved, leave empty to keep" : "Optional";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial string Port { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Username { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SavePassword { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    /// <summary>Run after login, e.g. "cd /var/www &amp;&amp; tmux attach".</summary>
    [ObservableProperty]
    public partial string StartupCommand { get; set; } = string.Empty;

    /// <summary>NAME=value lines exported in terminal sessions.</summary>
    [ObservableProperty]
    public partial string EnvironmentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SftpStartPath { get; set; } = string.Empty;

    /// <summary>Local command run before each connection (VPN, tunnel…).</summary>
    [ObservableProperty]
    public partial string PreConnectCommand { get; set; } = string.Empty;

    /// <summary>"7000, 8000/udp, 9000".</summary>
    [ObservableProperty]
    public partial string KnockText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int KnockDelay { get; set; } = 200;

    [ObservableProperty]
    public partial bool AlwaysRecord { get; set; }

    [ObservableProperty]
    public partial bool ForwardAgent { get; set; }

    [ObservableProperty]
    public partial bool ForwardX11 { get; set; }

    [ObservableProperty]
    public partial GroupOption SelectedGroup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordAuth), nameof(IsKeyAuth), nameof(IsAgentAuth))]
    public partial AuthMethod SelectedAuth { get; set; }

    // Radio buttons bind to these; only the checked one writes back.
    public bool IsPasswordAuth
    {
        get => SelectedAuth == AuthMethod.Password;
        set
        {
            if (value)
                SelectedAuth = AuthMethod.Password;
        }
    }

    public bool IsKeyAuth
    {
        get => SelectedAuth == AuthMethod.PrivateKey;
        set
        {
            if (value)
                SelectedAuth = AuthMethod.PrivateKey;
        }
    }

    public bool IsAgentAuth
    {
        get => SelectedAuth == AuthMethod.Agent;
        set
        {
            if (value)
                SelectedAuth = AuthMethod.Agent;
        }
    }

    private readonly string? _agentFingerprint;
    private bool _agentLoaded;

    public ObservableCollection<AgentKeyOption> AgentKeys { get; } = [];

    [ObservableProperty]
    public partial AgentKeyOption? SelectedAgentKey { get; set; }

    [ObservableProperty]
    public partial string AgentStatus { get; private set; } = "Looking for an SSH agent…";

    [ObservableProperty]
    public partial bool IsAgentAvailable { get; private set; }

    partial void OnSelectedAuthChanged(AuthMethod value)
    {
        if (value == AuthMethod.Agent && !_agentLoaded)
            _ = RefreshAgent();
    }

    [RelayCommand]
    private async Task RefreshAgent()
    {
        _agentLoaded = true;
        AgentStatus = "Looking for an SSH agent…";
        var status = await _agent.GetStatusAsync();
        IsAgentAvailable = status.IsAvailable;

        var previous = SelectedAgentKey?.Fingerprint ?? _agentFingerprint;
        AgentKeys.Clear();
        AgentKeys.Add(new AgentKeyOption(null, "Any key from the agent", string.Empty));
        foreach (var key in status.Keys)
            AgentKeys.Add(new AgentKeyOption(key.Fingerprint, string.IsNullOrWhiteSpace(key.Comment) ? key.Algorithm : key.Comment, key.Algorithm));

        // A pinned key that is not loaded right now stays selected, so saving keeps it.
        if (previous is not null && AgentKeys.All(k => k.Fingerprint != previous))
            AgentKeys.Add(new AgentKeyOption(previous, "Saved key (not loaded)", previous));
        SelectedAgentKey = AgentKeys.FirstOrDefault(k => k.Fingerprint == previous) ?? AgentKeys[0];

        AgentStatus = status.IsAvailable
            ? $"{status.Source} · {(status.Keys.Count == 1 ? "1 key" : $"{status.Keys.Count} keys")} loaded"
            : status.Error ?? "No SSH agent found.";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDetail), nameof(HasKeyDetail), nameof(HasCertificate))]
    [NotifyCanExecuteChangedFor(nameof(AttachCertificateCommand), nameof(RemoveCertificateCommand))]
    public partial SshKeyProfile? SelectedKey { get; set; }

    public bool HasCertificate => SelectedKey?.Certificate is not null;

    /// <summary>The key's certificate, or a note that a security key runs through OpenSSH.</summary>
    public string? KeyDetail
    {
        get
        {
            if (SelectedKey is not { } key)
                return null;
            var parts = new List<string>();
            if (key.IsSecurityKey)
                parts.Add("FIDO2 security key: terminal sessions run in the Windows OpenSSH client, which asks you to touch the key.");
            if (key.Certificate is { } certificate)
            {
                try
                {
                    parts.Add(SshCertificate.Parse(certificate).Summary(DateTimeOffset.Now));
                }
                catch (SshCertificateException ex)
                {
                    parts.Add(ex.Message);
                }
            }

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
    }

    public bool HasKeyDetail => KeyDetail is not null;

    private bool CanAttachCertificate => SelectedKey is not null;

    /// <summary>Adds the "-cert.pub" file ssh-keygen -s signed for this key; it is sent when logging in.</summary>
    [RelayCommand(CanExecute = nameof(CanAttachCertificate))]
    private async Task AttachCertificate()
    {
        if (SelectedKey is not { } key)
            return;
        var files = await _files.OpenFilesAsync("Certificate for " + key.Name + " (*-cert.pub)");
        if (files.Count == 0)
            return;

        try
        {
            await _catalog.AttachCertificateAsync(key.Id, await File.ReadAllTextAsync(files[0]));
            ReplaceKey(key);
            Error = null;
        }
        catch (Exception ex) when (ex is SshCertificateException or IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasCertificate))]
    private async Task RemoveCertificate()
    {
        if (SelectedKey is not { } key)
            return;
        await _catalog.AttachCertificateAsync(key.Id, null);
        ReplaceKey(key);
    }

    /// <summary>Shows the catalog's updated copy of a key (records are replaced on change).</summary>
    private void ReplaceKey(SshKeyProfile key)
    {
        if (_catalog.FindKey(key.Id) is not { } updated)
            return;
        var index = Keys.IndexOf(key);
        if (index >= 0)
            Keys[index] = updated;
        SelectedKey = updated;
    }

    public bool HasKeys => Keys.Count > 0;

    public ObservableCollection<PortForwardItem> PortForwards { get; }

    public ObservableCollection<JumpHostOption> JumpHosts { get; }

    [ObservableProperty]
    public partial JumpHostOption SelectedJumpHost { get; set; }

    public bool HasPortForwards => PortForwards.Count > 0;

    [ObservableProperty]
    public partial bool IsGeneralPage { get; set; }

    [ObservableProperty]
    public partial bool IsTunnelsPage { get; set; }

    [ObservableProperty]
    public partial bool IsSessionPage { get; set; }

    [RelayCommand]
    private async Task AddTunnel()
    {
        var result = await _dialogs.ShowAsync(new PortForwardEditorDialogViewModel(null));
        if (result is null)
            return;
        PortForwards.Add(new PortForwardItem(result.Rule));
        OnPropertyChanged(nameof(HasPortForwards));
    }

    [RelayCommand]
    private async Task EditTunnel(PortForwardItem? item)
    {
        if (item is null)
            return;
        var result = await _dialogs.ShowAsync(new PortForwardEditorDialogViewModel(item.Rule));
        if (result is null)
            return;
        var index = PortForwards.IndexOf(item);
        if (index >= 0)
            PortForwards[index] = new PortForwardItem(result.Rule);
    }

    [RelayCommand]
    private void RemoveTunnel(PortForwardItem? item)
    {
        if (item is not null && PortForwards.Remove(item))
            OnPropertyChanged(nameof(HasPortForwards));
    }

    private bool CanSave => !IsBusy && !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(Username);

    private readonly IKeyService? _keys;

    public bool CanManageKeys => _keys is not null;

    [RelayCommand]
    private async Task GenerateKey()
    {
        if (_keys is null || await _keys.GenerateAsync() is not { } key)
            return;
        Keys.Add(key);
        SelectedKey = key;
        SelectedAuth = AuthMethod.PrivateKey;
        OnPropertyChanged(nameof(HasKeys));
    }

    [RelayCommand]
    private Task CopyPublicKey() =>
        _keys is not null && SelectedKey is { } key ? _keys.CopyPublicKeyAsync(key) : Task.CompletedTask;

    [RelayCommand]
    private async Task ImportKey()
    {
        if (!await _vaultGate.EnsureUnlockedAsync("Keys are stored in the encrypted vault."))
            return;

        var key = await _dialogs.ShowAsync(new KeyImportDialogViewModel(_catalog, _files));
        if (key is null)
            return;

        Keys.Add(key);
        SelectedKey = key;
        SelectedAuth = AuthMethod.PrivateKey;
        OnPropertyChanged(nameof(HasKeys));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        Error = null;
        if (!int.TryParse(Port, out var port) || port is < 1 or > 65535)
        {
            Error = "Port must be a number between 1 and 65535.";
            return;
        }

        if (HostValidation.Problem(Address.Trim(), Username.Trim()) is { } hostProblem)
        {
            Error = hostProblem;
            return;
        }

        if (IsKeyAuth && SelectedKey is null)
        {
            Error = "Choose or import a private key.";
            return;
        }

        if (StartupScript.Parse(EnvironmentText, out var environmentError) is not { } environment)
        {
            Error = "Environment variables: " + environmentError;
            return;
        }

        if (PortKnock.ParseSequence(KnockText, out var knockError) is not { } knocks)
        {
            Error = "Port knocking: " + knockError;
            return;
        }

        var host = (_original ?? new HostProfile()) with
        {
            Name = Name.Trim(),
            Address = Address.Trim(),
            Port = port,
            Username = Username.Trim(),
            GroupId = SelectedGroup.Id,
            AuthMethod = SelectedAuth,
            KeyId = IsKeyAuth ? SelectedKey?.Id : _original?.KeyId,
            AgentKeyFingerprint = IsAgentAuth ? SelectedAgentKey?.Fingerprint ?? _agentFingerprint : null,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            PortForwards = PortForwards.Select(p => p.Rule).ToList(),
            JumpHostId = SelectedJumpHost.Id,
            AlwaysRecordSession = AlwaysRecord,
            ForwardAgent = ForwardAgent,
            ForwardX11 = ForwardX11,
            StartupCommand = string.IsNullOrWhiteSpace(StartupCommand) ? null : StartupCommand.Trim(),
            Environment = environment,
            SftpStartPath = string.IsNullOrWhiteSpace(SftpStartPath) ? null : SftpStartPath.Trim(),
            PreConnectCommand = string.IsNullOrWhiteSpace(PreConnectCommand) ? null : PreConnectCommand.Trim(),
            KnockSequence = knocks,
            KnockDelayMilliseconds = Math.Clamp(KnockDelay, 0, 10_000),
        };

        // Decide what happens to the stored password. Only touch the vault when needed.
        SecretUpdate? passwordUpdate = null;
        if (IsPasswordAuth && SavePassword && Password.Length > 0)
            passwordUpdate = new SecretUpdate(Password);
        else if ((!SavePassword || !IsPasswordAuth) && HasSavedPassword)
            passwordUpdate = SecretUpdate.Clear;

        if (passwordUpdate is not null && !await _vaultGate.EnsureUnlockedAsync("Passwords are stored in the encrypted vault."))
            return;

        IsBusy = true;
        try
        {
            await _catalog.SaveHostAsync(host, passwordUpdate);
            Close(host);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static IEnumerable<GroupOption> BuildGroupOptions(IHostCatalog catalog)
    {
        yield return new GroupOption(null, "No group", 0);

        IEnumerable<GroupOption> Walk(Guid? parent, int depth) =>
            catalog.Groups
                .Where(g => g.ParentId == parent)
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(g => Walk(g.Id, depth + 1).Prepend(new GroupOption(g.Id, g.Name, depth)));

        foreach (var option in Walk(null, 0))
            yield return option;
    }
}
