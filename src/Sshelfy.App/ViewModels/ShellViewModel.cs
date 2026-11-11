using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Behaviors;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.App.ViewModels.Sidebar;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;

namespace Sshelfy.App.ViewModels;

/// <summary>
/// Root view model of the main window: activity rail, sidebar, workspace tabs, bottom panel,
/// status bar and the dialog overlay.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, ICloseGuard
{
    private const double MinSidebarWidth = 220;
    private const double MaxSidebarWidth = 520;
    private const double MinBottomHeight = 120;
    private const double MaxBottomHeight = 600;

    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly ISecretVault _vault;
    private readonly ISessionLauncher _launcher;
    private readonly Sshelfy.Core.Hosts.IHostCatalog _catalog;
    private readonly IShellIntegration _shell;
    private readonly string _logDirectory;
    private readonly Sshelfy.Core.Terminal.ISessionLogStore _sessionLogs;
    private readonly IFilePickerService _files;
    private readonly TerminalThemeService _terminalThemes;
    private readonly LayoutService _layouts;
    private readonly string _dataDirectory;

    [RelayCommand]
    private Task ShowAbout() => Dialogs.ShowAsync(new AboutDialogViewModel(_shell, _dataDirectory, Updates));

    /// <summary>Newer releases (shown in the status bar when one is found).</summary>
    public UpdateService Updates { get; }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (Updates.Available is { } release)
            _shell.OpenUrl(release.Url);
    }

    [RelayCommand]
    private void ReportProblem() => _shell.OpenUrl(UpdateService.ReportProblemUrl());

    public ShellViewModel(
        ISettingsService settings,
        IThemeService theme,
        ISecretVault vault,
        ISessionLauncher launcher,
        Sshelfy.Core.Hosts.IHostCatalog catalog,
        IShellIntegration shell,
        Sshelfy.Infrastructure.Storage.StoragePaths paths,
        Sshelfy.Core.Terminal.ISessionLogStore sessionLogs,
        IFilePickerService files,
        WorkspaceViewModel workspace,
        DialogHostViewModel dialogs,
        StatusBarViewModel statusBar,
        BottomPanelViewModel bottomPanel,
        HostsSidebarViewModel hosts,
        SnippetsSidebarViewModel snippets,
        HistorySidebarViewModel history,
        RecordingsSidebarViewModel recordings,
        CommandPaletteViewModel commandPalette,
        Sshelfy.Core.Backup.BackupService backup,
        Sshelfy.Core.Backup.IBackupFile backupFile,
        IVaultGate vaultGate,
        Sshelfy.Core.Terminal.ILocalShellLauncher localShells,
        TerminalThemeService terminalThemes,
        LayoutService layouts,
        UpdateService updates)
    {
        Updates = updates;
        _layouts = layouts;
        _terminalThemes = terminalThemes;
        _localShells = localShells;
        CommandPalette = commandPalette;
        _backup = backup;
        _backupFile = backupFile;
        _vaultGate = vaultGate;
        _settings = settings;
        _theme = theme;
        _vault = vault;
        _launcher = launcher;
        _catalog = catalog;
        _shell = shell;
        _logDirectory = Path.Combine(paths.Root, "logs");
        _dataDirectory = paths.Root;
        _sessionLogs = sessionLogs;
        _files = files;
        Workspace = workspace;
        Dialogs = dialogs;
        StatusBar = statusBar;
        BottomPanel = bottomPanel;

        Hosts = hosts;
        Sections = [hosts, snippets, history, recordings];
        SelectedSection = hosts;

        var s = settings.Current;
        IsSidebarOpen = s.IsSidebarOpen;
        SidebarWidth = Math.Clamp(s.SidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        IsBottomPanelOpen = s.IsBottomPanelOpen;
        BottomPanelHeight = Math.Clamp(s.BottomPanelHeight, MinBottomHeight, MaxBottomHeight);
        UpdateSectionHighlight();

        _theme.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ThemeIcon));
            OnPropertyChanged(nameof(ThemeLabel));
        };
        _vault.StateChanged += (_, _) => OnVaultStateChanged();
        BottomPanel.TransferStarted += (_, _) => IsBottomPanelOpen = true;
        Workspace.ActiveTabChanged += (_, tab) =>
        {
            OpenSftpForActiveCommand.NotifyCanExecuteChanged();
            ToggleRecordingForActiveCommand.NotifyCanExecuteChanged();
            SplitRightCommand.NotifyCanExecuteChanged();
            SplitDownCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ActiveSplit));
            OnPropertyChanged(nameof(IsActiveRecording));
            if (_watchedTab is not null)
                _watchedTab.PropertyChanged -= OnActiveTabPropertyChanged;
            _watchedTab = tab;
            if (_watchedTab is not null)
                _watchedTab.PropertyChanged += OnActiveTabPropertyChanged;
            WindowTitle = tab is null or WelcomeTabViewModel ? "sshelfy" : $"{tab.Title} · sshelfy";
        };

        Workspace.Open(new WelcomeTabViewModel(hosts.NewHostCommand, QuickConnectCommand, ShowAboutCommand));
        _paletteActions = BuildPaletteActions(hosts, snippets, history, recordings);
        foreach (var layout in settings.Current.Layouts)
            Layouts.Add(layout);
        RefreshPaletteActions();
        _ = DetectLocalShellsAsync();
    }

    // ---------------------------------------------------------------- local terminals

    private readonly Sshelfy.Core.Terminal.ILocalShellLauncher _localShells;
    private readonly List<PaletteAction> _paletteActions;

    /// <summary>Shells found on this machine; the first is the default for Ctrl+Shift+T.</summary>
    public ObservableCollection<Sshelfy.Core.Terminal.LocalShellProfile> LocalShells { get; } = [];

    public bool HasLocalShells => LocalShells.Count > 0;

    private async Task DetectLocalShellsAsync()
    {
        if (!_localShells.IsSupported)
            return;
        foreach (var shell in await _localShells.DetectAsync())
            LocalShells.Add(shell);
        OnPropertyChanged(nameof(HasLocalShells));
        NewLocalTerminalCommand.NotifyCanExecuteChanged();
        RefreshPaletteActions();
    }

    /// <summary>The fixed actions plus one per local shell and per saved layout.</summary>
    private void RefreshPaletteActions()
    {
        var actions = new List<PaletteAction>(_paletteActions);
        actions.InsertRange(0, LocalShells.Select(s => new PaletteAction($"New terminal: {s.Name}", AppIcon.Terminal, () => _launcher.OpenLocalAsync(s))));
        actions.AddRange(Layouts.Select(l => new PaletteAction($"Open layout: {l.Name}", AppIcon.Columns2, () => OpenLayout(l))));
        actions.AddRange(Layouts.Select(l => new PaletteAction($"Delete layout: {l.Name}", AppIcon.Columns2, () => DeleteLayout(l))));
        CommandPalette.SetActions(actions);
    }

    // ---------------------------------------------------------------- layouts

    /// <summary>Named sets of tabs and splits (Settings.Layouts).</summary>
    public ObservableCollection<SavedLayout> Layouts { get; } = [];

    public bool HasLayouts => Layouts.Count > 0;

    [RelayCommand]
    private async Task SaveLayout()
    {
        var tabs = _layouts.Capture();
        if (tabs.Count == 0)
        {
            StatusBar.Set("Open saved hosts or local terminals first: other tabs are not kept in a layout.", StatusLevel.Error);
            return;
        }

        var name = await Dialogs.PromptAsync("Save layout", "Name", $"Layout {Layouts.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
            return;

        name = name.Trim();
        var layout = new SavedLayout(name, tabs);
        _settings.Update(s => s with { Layouts = [.. s.Layouts.Where(l => l.Name != name), layout] });
        ReloadLayouts();
        StatusBar.Set($"Saved layout {name} ({tabs.Count} {(tabs.Count == 1 ? "tab" : "tabs")})", StatusLevel.Ok);
    }

    [RelayCommand]
    private async Task OpenLayout(SavedLayout? layout)
    {
        if (layout is null)
            return;
        var opened = await _layouts.OpenAsync(layout.Tabs);
        if (opened == 0)
            StatusBar.Set($"Nothing to open in {layout.Name}: its hosts were deleted.", StatusLevel.Error);
    }

    [RelayCommand]
    private async Task DeleteLayout(SavedLayout? layout)
    {
        if (layout is null || !await Dialogs.ConfirmAsync("Delete layout", $"Delete the layout {layout.Name}? Hosts are not affected.", "Delete", isDestructive: true))
            return;
        _settings.Update(s => s with { Layouts = s.Layouts.Where(l => l.Name != layout.Name).ToList() });
        ReloadLayouts();
    }

    private void ReloadLayouts()
    {
        Layouts.Clear();
        foreach (var layout in _settings.Current.Layouts)
            Layouts.Add(layout);
        OnPropertyChanged(nameof(HasLayouts));
        RefreshPaletteActions();
    }

    private bool CanOpenLocal(Sshelfy.Core.Terminal.LocalShellProfile? shell) => shell is not null || LocalShells.Count > 0;

    /// <summary>Ctrl+Shift+T: a local terminal with the given shell, or the default one.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenLocal))]
    private Task NewLocalTerminal(Sshelfy.Core.Terminal.LocalShellProfile? shell) =>
        (shell ?? LocalShells.FirstOrDefault()) is { } chosen ? _launcher.OpenLocalAsync(chosen) : Task.CompletedTask;

    public CommandPaletteViewModel CommandPalette { get; }

    private readonly Sshelfy.Core.Backup.BackupService _backup;
    private readonly Sshelfy.Core.Backup.IBackupFile _backupFile;
    private readonly IVaultGate _vaultGate;

    [RelayCommand]
    private async Task ExportData()
    {
        var path = await Dialogs.ShowAsync(new ExportDataDialogViewModel(_backup, _backupFile, _files, _vaultGate, _vault));
        if (path is not null)
            StatusBar.Set($"Exported to {path}", StatusLevel.Ok);
    }

    [RelayCommand]
    private async Task ImportData()
    {
        var result = await Dialogs.ShowAsync(new ImportDataDialogViewModel(_backup, _backupFile, _files, _vaultGate));
        if (result is not null)
        {
            StatusBar.Set($"Imported {result.Imported}, replaced {result.Replaced}, skipped {result.Skipped}" +
                          (result.SecretsSkipped ? ". Passwords and keys were not imported (vault locked)" : string.Empty), StatusLevel.Ok);
        }
    }

    [RelayCommand]
    private void OpenCommandPalette() => CommandPalette.Open();

    private List<PaletteAction> BuildPaletteActions(HostsSidebarViewModel hosts, SnippetsSidebarViewModel snippets, HistorySidebarViewModel history, RecordingsSidebarViewModel recordings)
    {
        static PaletteAction Command(string name, AppIcon icon, System.Windows.Input.ICommand command, string? shortcut = null) =>
            new(name, icon, async () =>
            {
                if (command is IAsyncRelayCommand async)
                    await async.ExecuteAsync(null);
                else
                    command.Execute(null);
            }, shortcut, () => command.CanExecute(null));

        PaletteAction Show(string name, AppIcon icon, SidebarSectionViewModel section) => new(name, icon, () =>
        {
            SelectedSection = section;
            IsSidebarOpen = true;
            return Task.CompletedTask;
        });

        PaletteAction Panel(string name, AppIcon icon, Action show) => new(name, icon, () =>
        {
            show();
            IsBottomPanelOpen = true;
            return Task.CompletedTask;
        });

        return
        [
            Command("New host", AppIcon.Server, hosts.NewHostCommand, "Ctrl+Shift+N"),
            Command("New group", AppIcon.Folder, hosts.NewGroupCommand),
            Command("Quick connect", AppIcon.Zap, QuickConnectCommand, "Ctrl+Shift+K"),
            Command("Import hosts from ~/.ssh/config", AppIcon.FileText, hosts.ImportSshConfigCommand),
            Command("New snippet", AppIcon.CodeXml, snippets.NewCommand),
            Command("Find in terminal", AppIcon.SquareTerminal, FindInTerminalCommand, "Ctrl+Shift+F"),
            Command("Split terminal right", AppIcon.Columns2, SplitRightCommand, "Ctrl+Shift+D"),
            Command("Split terminal down", AppIcon.Rows2, SplitDownCommand, "Ctrl+Shift+E"),
            Command("Save open tabs as a layout", AppIcon.Columns2, SaveLayoutCommand),
            Command("Record session / stop recording", AppIcon.ScrollText, ToggleRecordingForActiveCommand),
            Command("Browse files on this host (SFTP)", AppIcon.FolderTree, OpenSftpForActiveCommand),
            Command("Close tab or pane", AppIcon.SquareTerminal, Workspace.CloseActiveCommand, "Ctrl+Shift+W"),
            Command("Next tab", AppIcon.SquareTerminal, Workspace.NextTabCommand, "Ctrl+Tab"),
            Command("Previous tab", AppIcon.SquareTerminal, Workspace.PreviousTabCommand, "Ctrl+Shift+Tab"),
            Show("Show hosts", AppIcon.Server, hosts),
            Show("Show snippets", AppIcon.CodeXml, snippets),
            Show("Show command history", AppIcon.History, history),
            Show("Show recordings", AppIcon.ScrollText, recordings),
            Panel("Show transfers", AppIcon.ArrowLeftRight, BottomPanel.ShowTransfers),
            Panel("Show tunnels", AppIcon.Zap, BottomPanel.ShowTunnels),
            Panel("Show activity log", AppIcon.Activity, BottomPanel.ShowActivity),
            Command("Toggle sidebar", AppIcon.Settings, ToggleSidebarCommand, "Ctrl+Shift+B"),
            Command("Toggle bottom panel", AppIcon.Settings, ToggleBottomPanelCommand, "Ctrl+Shift+J"),
            Command("Switch theme (system / light / dark)", AppIcon.SunMoon, CycleThemeCommand),
            Command("Lock or unlock the vault", AppIcon.Lock, ToggleVaultCommand),
            Command("Open settings", AppIcon.Settings, OpenSettingsCommand, "Ctrl+,"),
            Command("Export data (backup)", AppIcon.Download, ExportDataCommand),
            Command("Import data from a backup", AppIcon.Upload, ImportDataCommand),
            Command("About sshelfy", AppIcon.Settings, ShowAboutCommand),
            Command("Report a problem", AppIcon.Settings, ReportProblemCommand),
        ];
    }

    public WorkspaceViewModel Workspace { get; }

    public HostsSidebarViewModel Hosts { get; }

    public DialogHostViewModel Dialogs { get; }

    public StatusBarViewModel StatusBar { get; }

    public BottomPanelViewModel BottomPanel { get; }

    public ObservableCollection<SidebarSectionViewModel> Sections { get; }

    public double SidebarMinWidth => MinSidebarWidth;
    public double SidebarMaxWidth => MaxSidebarWidth;
    public double BottomPanelMinHeight => MinBottomHeight;
    public double BottomPanelMaxHeight => MaxBottomHeight;

    [ObservableProperty]
    public partial SidebarSectionViewModel? SelectedSection { get; set; }

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; }

    [ObservableProperty]
    public partial double SidebarWidth { get; set; }

    [ObservableProperty]
    public partial bool IsBottomPanelOpen { get; set; }

    [ObservableProperty]
    public partial double BottomPanelHeight { get; set; }

    public AppIcon ThemeIcon => _theme.Current switch
    {
        AppTheme.Light => AppIcon.Sun,
        AppTheme.Dark => AppIcon.Moon,
        _ => AppIcon.SunMoon,
    };

    public string ThemeLabel => _theme.Current switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => "System",
    };

    [ObservableProperty]
    public partial string WindowTitle { get; private set; } = "sshelfy";

    public bool IsVaultUnlocked => _vault.State == VaultState.Unlocked;

    public string VaultTooltip => _vault.State switch
    {
        VaultState.Unlocked => "Vault unlocked. Click to lock",
        VaultState.Locked => "Vault locked. Click to unlock",
        _ => "Set a master password to store secrets",
    };

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private void ToggleBottomPanel() => IsBottomPanelOpen = !IsBottomPanelOpen;

    [RelayCommand]
    private void FindInTerminal() => Workspace.ActiveTerminal?.Search.Open();

    /// <summary>Clicking the active rail item collapses the sidebar, like in most IDEs.</summary>
    [RelayCommand]
    private void ActivateSection(SidebarSectionViewModel section)
    {
        if (SelectedSection == section && IsSidebarOpen)
        {
            IsSidebarOpen = false;
            return;
        }

        SelectedSection = section;
        IsSidebarOpen = true;
    }

    [RelayCommand]
    private void CycleTheme()
    {
        var next = _theme.Current switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System,
        };
        _theme.Apply(next);
        StatusBar.Set($"Theme: {ThemeLabel}");
    }

    [RelayCommand]
    private async Task ToggleVault()
    {
        if (_vault.State == VaultState.Unlocked)
        {
            _vault.Lock();
            StatusBar.Set("Vault locked");
            return;
        }

        if (await Dialogs.ShowAsync(new VaultDialogViewModel(_vault)))
            StatusBar.Set("Vault unlocked", StatusLevel.Ok);
    }

    [RelayCommand]
    private Task QuickConnect() => _launcher.QuickConnectAsync();

    /// <summary>Asks before quitting while work would be lost, then remembers the open tabs.</summary>
    public async Task<bool> CanCloseAsync()
    {
        var sessions = Workspace.Terminals.Count(t => t.IsConnected) + Workspace.Tabs.Count(t => t is SftpTabViewModel { IsReady: true });
        var transfers = BottomPanel.ActiveCount;
        var unsaved = Workspace.Tabs.OfType<EditorTabViewModel>().Count(e => e.IsDirty);

        var parts = new List<string>();
        if (sessions > 0)
            parts.Add(sessions == 1 ? "1 session is connected" : $"{sessions} sessions are connected");
        if (transfers > 0)
            parts.Add(transfers == 1 ? "1 transfer is running" : $"{transfers} transfers are running");
        if (unsaved > 0)
            parts.Add(unsaved == 1 ? "1 file has unsaved changes" : $"{unsaved} files have unsaved changes");

        if (parts.Count > 0)
        {
            var message = string.Join(", ", parts) + ". Quit sshelfy anyway?";
            message = char.ToUpperInvariant(message[0]) + message[1..];
            if (!await Dialogs.ConfirmAsync("Quit sshelfy", message, "Quit", isDestructive: true))
                return false;
        }

        RememberOpenTabs();

        // Close sessions properly: recordings are finished and session ends reach the activity log.
        await Workspace.CloseAllAsync();
        return true;
    }

    /// <summary>Reopens the tabs of the last session when the user enabled it.</summary>
    public async Task RestoreTabsAsync()
    {
        if (!_settings.Current.RestoreTabsOnStartup)
            return;

        await _layouts.OpenAsync(_settings.Current.LastSessionTabs);
    }

    private void RememberOpenTabs()
    {
        // Split tabs keep their panes.
        _settings.Update(s => s with { LastSessionTabs = _layouts.Capture() });
    }

    [RelayCommand]
    private Task OpenSettings() => Dialogs.ShowAsync(new SettingsDialogViewModel(_settings, _theme, _vault, Dialogs, _shell, _logDirectory, _sessionLogs, _files, _terminalThemes));

    private TabViewModel? _watchedTab;

    public bool IsActiveRecording => Workspace.ActiveTab?.IsRecording == true;

    /// <summary>The active tab when it is split into panes (drives the broadcast toggle).</summary>
    public SplitTabViewModel? ActiveSplit => Workspace.ActiveTab as SplitTabViewModel;

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.IsRecording))
            OnPropertyChanged(nameof(IsActiveRecording));
    }

    private bool CanToggleRecordingForActive => Workspace.ActiveTerminal is not null;

    [RelayCommand(CanExecute = nameof(CanToggleRecordingForActive))]
    private Task ToggleRecordingForActive() =>
        Workspace.ActiveTerminal is { } terminal ? terminal.ToggleRecording() : Task.CompletedTask;

    private bool CanOpenSftpForActive => Workspace.ActiveTerminal is { IsLocal: false } or { IsExternalSsh: true };

    /// <summary>Opens a file browser for the host of the active terminal.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSftpForActive))]
    private Task OpenSftpForActive() =>
        Workspace.ActiveTerminal is { } terminal ? _launcher.OpenSftpAsync(terminal.Host, terminal.KnownPassword, terminal.WorkingDirectory) : Task.CompletedTask;

    private bool CanSplit(TerminalTabViewModel? pane) => (pane ?? Workspace.ActiveTerminal) is not null;

    /// <summary>Opens another session to the host of <paramref name="pane"/> (default: the active terminal) next to it.</summary>
    [RelayCommand(CanExecute = nameof(CanSplit))]
    private Task SplitRight(TerminalTabViewModel? pane) =>
        (pane ?? Workspace.ActiveTerminal) is { } terminal ? _launcher.SplitAsync(terminal, sideBySide: true) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSplit))]
    private Task SplitDown(TerminalTabViewModel? pane) =>
        (pane ?? Workspace.ActiveTerminal) is { } terminal ? _launcher.SplitAsync(terminal, sideBySide: false) : Task.CompletedTask;

    partial void OnSelectedSectionChanged(SidebarSectionViewModel? value) => UpdateSectionHighlight();

    partial void OnIsSidebarOpenChanged(bool value)
    {
        UpdateSectionHighlight();
        _settings.Update(s => s with { IsSidebarOpen = value });
    }

    partial void OnSidebarWidthChanged(double value) => _settings.Update(s => s with { SidebarWidth = value });

    partial void OnIsBottomPanelOpenChanged(bool value) => _settings.Update(s => s with { IsBottomPanelOpen = value });

    partial void OnBottomPanelHeightChanged(double value) => _settings.Update(s => s with { BottomPanelHeight = value });

    private void UpdateSectionHighlight()
    {
        // Called from property setters during construction, before Sections exists.
        if (Sections is null)
            return;
        foreach (var section in Sections)
            section.IsSelected = IsSidebarOpen && section == SelectedSection;
    }

    private void OnVaultStateChanged()
    {
        OnPropertyChanged(nameof(IsVaultUnlocked));
        OnPropertyChanged(nameof(VaultTooltip));
    }
}
