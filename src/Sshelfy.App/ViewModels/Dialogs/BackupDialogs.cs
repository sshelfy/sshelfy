using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Backup;
using Sshelfy.Core.Security;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>Writes an encrypted backup of hosts, keys, snippets and (optionally) settings.</summary>
public sealed partial class ExportDataDialogViewModel : DialogViewModel<string>
{
    private const int MinimumLength = 8;

    private readonly BackupService _backup;
    private readonly IBackupFile _file;
    private readonly IFilePickerService _files;
    private readonly IVaultGate _vaultGate;
    private readonly ISecretVault _vault;

    public ExportDataDialogViewModel(BackupService backup, IBackupFile file, IFilePickerService files, IVaultGate vaultGate, ISecretVault vault)
    {
        _backup = backup;
        _file = file;
        _files = files;
        _vaultGate = vaultGate;
        _vault = vault;
    }

    public override string Title => "Export data";

    public override double Width => 460;

    public bool HasVault => _vault.State != VaultState.NotCreated;

    [ObservableProperty]
    public partial bool IncludeSecrets { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeSettings { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial string Confirmation { get; set; } = string.Empty;

    private bool CanExport => !IsBusy && Password.Length > 0 && Confirmation.Length > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export()
    {
        Error = null;
        if (Password.Length < MinimumLength)
        {
            Error = $"Use at least {MinimumLength} characters for the backup password.";
            return;
        }

        if (Password != Confirmation)
        {
            Error = "The passwords don't match.";
            return;
        }

        var secrets = IncludeSecrets && HasVault;
        if (secrets && !await _vaultGate.EnsureUnlockedAsync("Unlock the vault to include passwords and keys in the backup."))
            return;

        var name = $"sshelfy-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.sshelfy-backup";
        var path = await _files.SaveFileAsync("Export data", name);
        if (path is null)
            return;

        IsBusy = true;
        ExportCommand.NotifyCanExecuteChanged();
        try
        {
            var content = await _backup.CreateAsync(secrets, IncludeSettings);
            await _file.WriteAsync(path, content, Password);
            Close(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultLockedException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }
}

public sealed record ImportChoiceOption(ImportChoice Choice, string Name);

/// <summary>A row of the import preview.</summary>
public sealed partial class BackupItemViewModel : ViewModelBase
{
    public BackupItemViewModel(ImportItem item)
    {
        Item = item;
        Include = item.Choice != ImportChoice.Skip;
        SelectedChoice = ConflictChoices.First(c => c.Choice == (item.State == ImportItemState.Conflict ? item.Choice : ImportChoice.Skip));
    }

    public ImportItem Item { get; }

    public string Kind => Item.Kind switch
    {
        ImportItemKind.Group => "Group",
        ImportItemKind.Host => "Host",
        ImportItemKind.Key => "Key",
        _ => "Snippet",
    };

    public AppIcon Icon => Item.Kind switch
    {
        ImportItemKind.Group => AppIcon.Folder,
        ImportItemKind.Host => AppIcon.Server,
        ImportItemKind.Key => AppIcon.KeyRound,
        _ => AppIcon.CodeXml,
    };

    public string Name => Item.Name;

    public string? Detail => Item.Detail;

    public bool IsNew => Item.State == ImportItemState.New;

    public bool IsIdentical => Item.State == ImportItemState.Identical;

    public bool IsConflict => Item.State == ImportItemState.Conflict;

    public string StateText => Item.State switch
    {
        ImportItemState.New => "New",
        ImportItemState.Identical => "Already here",
        _ => "Different version here",
    };

    public IReadOnlyList<ImportChoiceOption> ConflictChoices { get; } =
    [
        new(ImportChoice.Skip, "Keep mine"),
        new(ImportChoice.Replace, "Use backup"),
        new(ImportChoice.KeepBoth, "Keep both"),
    ];

    /// <summary>New items: import or not.</summary>
    [ObservableProperty]
    public partial bool Include { get; set; }

    /// <summary>Conflicts: which version wins.</summary>
    [ObservableProperty]
    public partial ImportChoiceOption SelectedChoice { get; set; }

    partial void OnIncludeChanged(bool value)
    {
        if (IsNew)
            Item.Choice = value ? ImportChoice.Import : ImportChoice.Skip;
    }

    partial void OnSelectedChoiceChanged(ImportChoiceOption value)
    {
        if (IsConflict)
            Item.Choice = value.Choice;
    }
}

/// <summary>Opens a backup with its password, previews what would change, then imports the chosen items.</summary>
public sealed partial class ImportDataDialogViewModel : DialogViewModel<ImportResult>
{
    private readonly BackupService _backup;
    private readonly IBackupFile _file;
    private readonly IFilePickerService _files;
    private readonly IVaultGate _vaultGate;
    private ImportPlan? _plan;

    public ImportDataDialogViewModel(BackupService backup, IBackupFile file, IFilePickerService files, IVaultGate vaultGate)
    {
        _backup = backup;
        _file = file;
        _files = files;
        _vaultGate = vaultGate;
    }

    public override string Title => IsPreview ? "Import data: review" : "Import data";

    public override double Width => IsPreview ? 620 : 460;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string? FilePath { get; private set; }

    public bool HasFile => FilePath is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Width))]
    public partial bool IsPreview { get; private set; }

    public ObservableCollection<BackupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial bool HasSettings { get; private set; }

    [ObservableProperty]
    public partial bool RestoreSettings { get; set; }

    [ObservableProperty]
    public partial bool IncludesSecrets { get; private set; }

    [RelayCommand]
    private async Task Browse()
    {
        var paths = await _files.OpenFilesAsync("Choose a sshelfy backup");
        if (paths.Count > 0)
            FilePath = paths[0];
    }

    private bool CanOpen => HasFile && Password.Length > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task Open()
    {
        Error = null;
        IsBusy = true;
        OpenCommand.NotifyCanExecuteChanged();
        try
        {
            var content = await _file.ReadAsync(FilePath!, Password);
            _plan = _backup.Plan(content);
            Items.Clear();
            foreach (var item in _plan.Items.OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
                Items.Add(new BackupItemViewModel(item));

            HasSettings = _plan.HasSettings;
            IncludesSecrets = content.IncludesSecrets;
            var conflicts = Items.Count(i => i.IsConflict);
            Summary = $"Made {content.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}" +
                      (content.MachineName is { } machine ? $" on {machine}" : string.Empty) +
                      $"  ·  {Items.Count(i => i.IsNew)} new, {Items.Count(i => i.IsIdentical)} already here" +
                      (conflicts > 0 ? $", {conflicts} different" : string.Empty);
            IsPreview = true;
        }
        catch (BackupPasswordException ex)
        {
            Error = ex.Message;
        }
        catch (BackupFormatException ex)
        {
            Error = ex.Message;
        }
        catch (IOException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OpenCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (_plan is null || IsBusy)
            return;

        _plan.RestoreSettings = RestoreSettings;
        var withSecrets = IncludesSecrets && await _vaultGate.EnsureUnlockedAsync("Passwords and keys from the backup go into the vault.");

        IsBusy = true;
        try
        {
            Close(await _backup.ApplyAsync(_plan, withSecrets));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Sshelfy.Core.Hosts.SshKeyImportException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
