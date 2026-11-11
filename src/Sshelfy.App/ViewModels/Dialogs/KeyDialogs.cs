using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed record KeyTypeOption(SshKeyType Type, string Name, string Hint);

/// <summary>Creates a new key pair in the vault and shows its public key to copy.</summary>
public sealed partial class GenerateKeyDialogViewModel : DialogViewModel<SshKeyProfile>
{
    private readonly ISshKeyGenerator _generator;
    private readonly IHostCatalog _catalog;
    private readonly IClipboardService _clipboard;
    private SshKeyProfile? _created;

    public GenerateKeyDialogViewModel(ISshKeyGenerator generator, IHostCatalog catalog, IClipboardService clipboard)
    {
        _generator = generator;
        _catalog = catalog;
        _clipboard = clipboard;
        SelectedType = Types[0];
        Comment = $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant();
        Name = $"{Environment.MachineName} (sshelfy)";
    }

    public override string Title => IsGenerated ? "Key created" : "Generate SSH key";

    public override double Width => 500;

    public IReadOnlyList<KeyTypeOption> Types { get; } =
    [
        new(SshKeyType.Ed25519, "Ed25519", "Small and fast; supported by OpenSSH 6.5+ (recommended)."),
        new(SshKeyType.Rsa4096, "RSA 4096", "For old servers or devices that do not accept Ed25519."),
    ];

    [ObservableProperty]
    public partial KeyTypeOption SelectedType { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Comment { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool IsGenerated { get; private set; }

    [ObservableProperty]
    public partial string? PublicKey { get; private set; }

    [ObservableProperty]
    public partial string? Fingerprint { get; private set; }

    private bool CanGenerate => !IsBusy && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task Generate()
    {
        IsBusy = true;
        GenerateCommand.NotifyCanExecuteChanged();
        try
        {
            var type = SelectedType.Type;
            var comment = Comment;
            var key = await Task.Run(() => _generator.Generate(type, comment));
            _created = await _catalog.ImportKeyAsync(Name.Trim(), key.PrivateKey, null);
            PublicKey = key.PublicKey;
            Fingerprint = _created.Fingerprint;
            IsGenerated = true;
        }
        catch (Exception ex) when (ex is SshKeyImportException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private Task Copy() => PublicKey is null ? Task.CompletedTask : _clipboard.SetTextAsync(PublicKey);

    [RelayCommand]
    private void Done()
    {
        if (_created is not null)
            Close(_created);
    }
}

/// <summary>Adds a key's public half to a host's authorized_keys, optionally switching the host to that key.</summary>
public sealed partial class InstallKeyDialogViewModel : DialogViewModel<SshKeyProfile>
{
    private readonly HostProfile _host;
    private readonly IHostCatalog _catalog;
    private readonly IKeyService _keys;

    public InstallKeyDialogViewModel(HostProfile host, IHostCatalog catalog, IKeyService keys)
    {
        _host = host;
        _catalog = catalog;
        _keys = keys;
        Keys = new ObservableCollection<SshKeyProfile>(catalog.Keys.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase));
        SelectedKey = Keys.FirstOrDefault(k => k.Id == host.KeyId) ?? Keys.FirstOrDefault();
    }

    public override string Title => $"Install a key on {_host.DisplayName}";

    public override double Width => 480;

    public string Description =>
        $"sshelfy logs in to {_host.Endpoint} the usual way once and adds the public key to ~/.ssh/authorized_keys. " +
        "After that the key alone is enough.";

    public ObservableCollection<SshKeyProfile> Keys { get; }

    public bool HasKeys => Keys.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial SshKeyProfile? SelectedKey { get; set; }

    [ObservableProperty]
    public partial bool SwitchHost { get; set; } = true;

    [RelayCommand]
    private async Task NewKey()
    {
        if (await _keys.GenerateAsync() is { } key)
        {
            Keys.Add(key);
            SelectedKey = key;
            OnPropertyChanged(nameof(HasKeys));
        }
    }

    private bool CanInstall => SelectedKey is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        if (SelectedKey is not { } key)
            return;

        Error = null;
        IsBusy = true;
        InstallCommand.NotifyCanExecuteChanged();
        try
        {
            if (await _keys.InstallAsync(_host, key) is { } problem)
            {
                Error = problem;
                return;
            }

            if (SwitchHost && _catalog.FindHost(_host.Id) is { } saved)
                await _catalog.SaveHostAsync(saved with { AuthMethod = AuthMethod.PrivateKey, KeyId = key.Id });
            Close(key);
        }
        finally
        {
            IsBusy = false;
            InstallCommand.NotifyCanExecuteChanged();
        }
    }
}
