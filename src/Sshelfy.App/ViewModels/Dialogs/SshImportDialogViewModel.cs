using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Import;
using Sshelfy.Core.Security;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed partial class ImportItemViewModel : ViewModelBase
{
    public ImportItemViewModel(ImportCandidate candidate)
    {
        Candidate = candidate;
        IsSelected = !candidate.AlreadyExists;
    }

    public ImportCandidate Candidate { get; }

    public string Alias => Candidate.Source.Alias;

    public string Endpoint
    {
        get
        {
            var s = Candidate.Source;
            var user = s.User is null ? string.Empty : s.User + "@";
            var port = s.Port == 22 ? string.Empty : ":" + s.Port;
            var via = s.ProxyJump is null ? string.Empty : $" via {s.ProxyJump}";
            return $"{user}{s.HostName}{port}{via}";
        }
    }

    public string? KeyName => Candidate.KeyPath is { } path ? Path.GetFileName(path) : null;

    public bool CanImport => !Candidate.AlreadyExists;

    public bool AlreadyExists => Candidate.AlreadyExists;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed record ImportGroupOption(Guid? Id, string Name, bool IsNew);

/// <summary>Imports hosts, keys and known hosts from ~/.ssh.</summary>
public sealed partial class SshImportDialogViewModel : DialogViewModel<SshImportResult>
{
    private const string NewGroupName = "SSH config";

    private readonly SshConfigImporter _importer;
    private readonly IHostCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly IVaultGate _vaultGate;

    public SshImportDialogViewModel(SshConfigImporter importer, IHostCatalog catalog, IDialogService dialogs, IVaultGate vaultGate, string configPath)
    {
        _importer = importer;
        _catalog = catalog;
        _dialogs = dialogs;
        _vaultGate = vaultGate;
        ConfigPath = configPath;

        var scan = importer.Scan();
        ConfigFound = scan.ConfigFound;
        Warnings = scan.Warnings;
        KnownHostsEntries = scan.KnownHostsEntries;
        ImportKnownHosts = scan.KnownHostsEntries > 0;
        Items = new ObservableCollection<ImportItemViewModel>(scan.Candidates.Select(c => new ImportItemViewModel(c)));
        foreach (var item in Items)
            item.PropertyChanged += (_, _) => RefreshCounts();

        Groups = new ObservableCollection<ImportGroupOption>(
            catalog.Groups
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new ImportGroupOption(g.Id, g.Name, false))
                .Prepend(new ImportGroupOption(null, "No group", false))
                .Prepend(new ImportGroupOption(null, $"New group “{NewGroupName}”", true)));
        SelectedGroup = Groups[0];
    }

    public override string Title => "Import from SSH config";

    public override double Width => 560;

    public string ConfigPath { get; }

    public bool ConfigFound { get; }

    public bool HasCandidates => Items.Count > 0;

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningsText => string.Join("\n", Warnings);

    public int KnownHostsEntries { get; }

    public bool HasKnownHosts => KnownHostsEntries > 0;

    public string KnownHostsText => $"Also trust the {KnownHostsEntries} host key(s) from known_hosts";

    public ObservableCollection<ImportItemViewModel> Items { get; }

    public ObservableCollection<ImportGroupOption> Groups { get; }

    [ObservableProperty]
    public partial ImportGroupOption SelectedGroup { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool ImportKnownHosts { get; set; }

    public int SelectedCount => Items.Count(i => i.IsSelected && i.CanImport);

    public string ImportButtonText => SelectedCount switch
    {
        0 => ImportKnownHosts ? "Import host keys" : "Import",
        1 => "Import 1 host",
        var n => $"Import {n} hosts",
    };

    [RelayCommand]
    private void SelectAll()
    {
        var select = Items.Any(i => i.CanImport && !i.IsSelected);
        foreach (var item in Items.Where(i => i.CanImport))
            item.IsSelected = select;
    }

    private bool CanImport => !IsBusy && (SelectedCount > 0 || ImportKnownHosts);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        var selected = Items.Where(i => i.IsSelected && i.CanImport).Select(i => i.Candidate).ToList();
        if (selected.Any(c => c.KeyPath is not null) && !await _vaultGate.EnsureUnlockedAsync("Imported keys are stored in the encrypted vault."))
            return;

        IsBusy = true;
        try
        {
            Guid? groupId = SelectedGroup.Id;
            if (SelectedGroup.IsNew && selected.Count > 0)
            {
                var group = _catalog.Groups.FirstOrDefault(g => g.Name == NewGroupName && g.ParentId is null)
                            ?? new HostGroup { Name = NewGroupName };
                if (_catalog.Groups.All(g => g.Id != group.Id))
                    await _catalog.SaveGroupAsync(group);
                groupId = group.Id;
            }

            var result = await _importer.ImportAsync(selected, groupId, ImportKnownHosts, AskPassphraseAsync);
            Close(result);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or VaultLockedException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string?> AskPassphraseAsync(string path, SshKeyProblem problem)
    {
        var result = await _dialogs.ShowAsync(new CredentialPromptDialogViewModel(
            Path.GetFileName(path),
            canSave: false,
            problem == SshKeyProblem.WrongPassphrase ? "The passphrase is incorrect." : "This key is encrypted.",
            label: $"Passphrase for {Path.GetFileName(path)}",
            confirmText: "Unlock key"));
        return result?.Password;
    }

    partial void OnImportKnownHostsChanged(bool value) => RefreshCounts();

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ImportButtonText));
        ImportCommand.NotifyCanExecuteChanged();
    }
}
