using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>Imports a private key (from a file or pasted text) into the vault.</summary>
public sealed partial class KeyImportDialogViewModel : DialogViewModel<SshKeyProfile>
{
    private const long MaxKeyFileSize = 64 * 1024;

    private readonly IHostCatalog _catalog;
    private readonly IFilePickerService _files;

    public KeyImportDialogViewModel(IHostCatalog catalog, IFilePickerService files)
    {
        _catalog = catalog;
        _files = files;
    }

    public override string Title => "Import private key";

    public override double Width => 520;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial string KeyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Passphrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NeedsPassphrase { get; set; }

    [RelayCommand]
    private async Task Browse()
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var paths = await _files.OpenFilesAsync("Choose a private key", startDirectory: sshDir);
        if (paths.Count == 0)
            return;

        var info = new FileInfo(paths[0]);
        if (info.Length > MaxKeyFileSize)
        {
            Error = "That file is too large to be a private key.";
            return;
        }

        KeyText = await File.ReadAllTextAsync(info.FullName);
        if (string.IsNullOrWhiteSpace(Name))
            Name = Path.GetFileNameWithoutExtension(info.Name);
        Error = null;
    }

    private bool CanImport => !IsBusy && !string.IsNullOrWhiteSpace(KeyText);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var key = await _catalog.ImportKeyAsync(Name, KeyText, Passphrase);
            Close(key);
        }
        catch (SshKeyImportException ex)
        {
            Error = ex.Message;
            NeedsPassphrase |= ex.Problem is SshKeyProblem.PassphraseRequired or SshKeyProblem.WrongPassphrase;
        }
        catch (VaultLockedException)
        {
            Error = "Unlock the vault first.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
