using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Controls;
using Sshelfy.App.Services;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed record LogLevelOption(DiagnosticLevel Level, string Name);

public sealed record AutoLockOption(int Minutes, string Name);

public sealed record SessionLogFormatOption(Sshelfy.Core.Terminal.SessionLogFormat Format, string Name);

/// <summary>Preferences. Changes apply immediately, like macOS settings windows.</summary>
public sealed partial class SettingsDialogViewModel : DialogViewModel<bool>
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly ISecretVault _vault;
    private readonly IDialogService _dialogs;
    private bool _loading;

    private readonly IShellIntegration _shell;
    private readonly string _logDirectory;
    private readonly Sshelfy.Core.Terminal.ISessionLogStore _sessionLogs;
    private readonly IFilePickerService _files;
    private readonly TerminalThemeService? _terminalThemes;

    public SettingsDialogViewModel(
        ISettingsService settings,
        IThemeService theme,
        ISecretVault vault,
        IDialogService dialogs,
        IShellIntegration shell,
        string logDirectory,
        Sshelfy.Core.Terminal.ISessionLogStore sessionLogs,
        IFilePickerService files,
        TerminalThemeService? terminalThemes = null)
    {
        _terminalThemes = terminalThemes;
        _sessionLogs = sessionLogs;
        _files = files;
        _shell = shell;
        _logDirectory = logDirectory;
        _settings = settings;
        _theme = theme;
        _vault = vault;
        _dialogs = dialogs;

        _loading = true;
        var s = settings.Current;
        IsSystemTheme = s.Theme == AppTheme.System;
        IsLightTheme = s.Theme == AppTheme.Light;
        IsDarkTheme = s.Theme == AppTheme.Dark;
        TerminalFontFamily = s.TerminalFontFamily;
        TerminalFontSize = (decimal)s.TerminalFontSize;
        ScrollbackLines = s.TerminalScrollbackLines;
        ThemePreview = BuildThemePreview();
        LoadTerminalThemes(s.TerminalThemeName ?? TerminalThemes.Automatic);
        foreach (var rule in s.HighlightRules)
            HighlightRules.Add(new HighlightRuleViewModel(rule, SaveHighlightRules));
        NotifyLongCommandSeconds = s.NotifyLongCommandSeconds;
        X11Display = s.X11Display;
        ExternalEditor = s.ExternalEditor ?? string.Empty;
        RememberHistory = s.RememberCommandHistory;
        RestoreTabs = s.RestoreTabsOnStartup;
        AutoReconnect = s.AutoReconnect;
        ShowServerStats = s.ShowServerStats;
        CheckForUpdates = s.CheckForUpdates;
        SelectedAutoLock = AutoLockOptions.FirstOrDefault(o => o.Minutes == s.VaultAutoLockMinutes) ?? AutoLockOptions[0];
        LockWithScreen = s.LockVaultWithScreen;
        SelectedLogLevel = LogLevels.First(l => l.Level == s.DiagnosticLevel);
        SelectedSessionLogFormat = SessionLogFormats.First(f => f.Format == s.SessionLogFormat);
        SessionLogTimestamps = s.SessionLogTimestamps;
        EncryptSessionLogs = s.EncryptSessionLogs;
        SessionLogRetentionDays = s.SessionLogRetentionDays;
        SessionLogMaxMegabytes = s.SessionLogMaxMegabytes;
        UnlockOnDevice = vault.IsRememberedOnDevice;
        UnlockWithHello = vault.IsHelloEnabled;
        _loading = false;
        _ = CheckHelloAsync();
    }

    // ---------------------------------------------------------------- highlights and notifications

    public ObservableCollection<HighlightRuleViewModel> HighlightRules { get; } = [];

    public IReadOnlyList<HighlightColor> HighlightColors { get; } = Enum.GetValues<HighlightColor>();

    [ObservableProperty]
    public partial int NotifyLongCommandSeconds { get; set; }

    [ObservableProperty]
    public partial string X11Display { get; set; } = string.Empty;

    /// <summary>Command for "Edit in external editor"; empty uses the system default app.</summary>
    [ObservableProperty]
    public partial string ExternalEditor { get; set; } = string.Empty;

    partial void OnExternalEditorChanged(string value)
    {
        if (!_loading)
            _settings.Update(s => s with { ExternalEditor = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }

    partial void OnX11DisplayChanged(string value)
    {
        if (!_loading && Sshelfy.Core.Ssh.OpenSshCommandLine.IsValidDisplay(value.Trim()))
            _settings.Update(s => s with { X11Display = value.Trim() });
    }

    partial void OnNotifyLongCommandSecondsChanged(int value)
    {
        if (!_loading)
            _settings.Update(s => s with { NotifyLongCommandSeconds = Math.Max(0, value) });
    }

    [RelayCommand]
    private void AddHighlightRule()
    {
        HighlightRules.Add(new HighlightRuleViewModel(new HighlightRule(string.Empty, HighlightColor.Cyan), SaveHighlightRules));
        SaveHighlightRules();
    }

    [RelayCommand]
    private void RemoveHighlightRule(HighlightRuleViewModel rule)
    {
        HighlightRules.Remove(rule);
        SaveHighlightRules();
    }

    private void SaveHighlightRules()
    {
        if (!_loading)
            _settings.Update(s => s with { HighlightRules = HighlightRules.Select(r => r.ToRule()).ToList() });
    }

    // ---------------------------------------------------------------- terminal colors

    /// <summary>The automatic entry, imported schemes, then the built-in ones.</summary>
    public ObservableCollection<string> TerminalThemeNames { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTerminalThemeCommand))]
    [NotifyPropertyChangedFor(nameof(IsCustomTerminalTheme))]
    public partial string? SelectedTerminalTheme { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBackground))]
    public partial TerminalPalette? PreviewPalette { get; private set; }

    public Avalonia.Media.IBrush? PreviewBackground => PreviewPalette is { } palette ? new Avalonia.Media.SolidColorBrush(palette.Background) : null;

    /// <summary>The selected scheme was imported, so it can be removed.</summary>
    public bool IsCustomTerminalTheme => CanRemoveTerminalTheme();

    /// <summary>Sample output shown with the selected colors.</summary>
    public TerminalEmulator ThemePreview { get; }

    [ObservableProperty]
    public partial string? ThemeImportMessage { get; private set; }

    partial void OnSelectedTerminalThemeChanged(string? value)
    {
        var current = _settings.Current;
        PreviewPalette = _terminalThemes?.PaletteFor(TerminalThemes.Find(value, current.CustomTerminalThemes));
        if (!_loading && value is not null)
            _settings.Update(s => s with { TerminalThemeName = value == TerminalThemes.Automatic ? null : value });
    }

    private void LoadTerminalThemes(string selected)
    {
        var wasLoading = _loading;
        _loading = true;
        TerminalThemeNames.Clear();
        TerminalThemeNames.Add(TerminalThemes.Automatic);
        foreach (var name in _settings.Current.CustomTerminalThemes.Select(t => t.Name)
                     .Concat(TerminalThemes.BuiltIn.Select(t => t.Name)).Distinct())
            TerminalThemeNames.Add(name);
        _loading = wasLoading;
        SelectedTerminalTheme = TerminalThemeNames.Contains(selected) ? selected : TerminalThemes.Automatic;
        OnSelectedTerminalThemeChanged(SelectedTerminalTheme);
    }

    [RelayCommand]
    private async Task ImportTerminalTheme()
    {
        var files = await _files.OpenFilesAsync("Import color scheme (Windows Terminal, iTerm2, base16)");
        if (files.Count == 0)
            return;

        try
        {
            var imported = TerminalThemeImporter.Import(await File.ReadAllTextAsync(files[0]), files[0]);
            var names = imported.Select(t => t.Name).ToHashSet();
            _settings.Update(s => s with
            {
                CustomTerminalThemes = [.. imported, .. s.CustomTerminalThemes.Where(t => !names.Contains(t.Name))],
                TerminalThemeName = imported[0].Name,
            });
            LoadTerminalThemes(imported[0].Name);
            ThemeImportMessage = imported.Count == 1 ? $"Imported {imported[0].Name}." : $"Imported {imported.Count} color schemes.";
        }
        catch (Exception ex) when (ex is TerminalThemeImportException or IOException or UnauthorizedAccessException)
        {
            ThemeImportMessage = ex.Message;
        }
    }

    private bool CanRemoveTerminalTheme() =>
        _settings.Current.CustomTerminalThemes.Any(t => t.Name == SelectedTerminalTheme);

    /// <summary>Removes an imported scheme (built-in ones stay).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveTerminalTheme))]
    private void RemoveTerminalTheme()
    {
        var name = SelectedTerminalTheme;
        _settings.Update(s => s with
        {
            CustomTerminalThemes = s.CustomTerminalThemes.Where(t => t.Name != name).ToList(),
            TerminalThemeName = s.TerminalThemeName == name ? null : s.TerminalThemeName,
        });
        ThemeImportMessage = null;
        LoadTerminalThemes(TerminalThemes.BuiltIn.Any(t => t.Name == name) ? name! : TerminalThemes.Automatic);
    }

    private static TerminalEmulator BuildThemePreview()
    {
        var preview = new TerminalEmulator(64, 6, 0);
        preview.Feed(
            "\x1b[1;32mdeploy@web\x1b[0m:\x1b[1;34m~/app\x1b[0m$ git status\r\n" +
            "On branch \x1b[36mmain\x1b[0m, \x1b[33m2 files changed\x1b[0m\r\n" +
            "\x1b[31mERROR\x1b[0m connection refused   \x1b[35mtrace\x1b[0m  \x1b[90mdebug\x1b[0m\r\n" +
            "\x1b[40m  \x1b[41m  \x1b[42m  \x1b[43m  \x1b[44m  \x1b[45m  \x1b[46m  \x1b[47m  \x1b[0m\r\n" +
            "\x1b[100m  \x1b[101m  \x1b[102m  \x1b[103m  \x1b[104m  \x1b[105m  \x1b[106m  \x1b[107m  \x1b[0m\r\n" +
            "\x1b[1;32mdeploy@web\x1b[0m:\x1b[1;34m~/app\x1b[0m$ ");
        return preview;
    }

    // ---------------------------------------------------------------- Windows Hello

    /// <summary>Shown only when Hello is set up in Windows and a vault exists.</summary>
    [ObservableProperty]
    public partial bool IsHelloAvailable { get; private set; }

    [ObservableProperty]
    public partial bool UnlockWithHello { get; set; }

    [ObservableProperty]
    public partial string? HelloMessage { get; private set; }

    private async Task CheckHelloAsync() =>
        IsHelloAvailable = _vault.State != VaultState.NotCreated && await _vault.IsHelloAvailableAsync();

    partial void OnUnlockWithHelloChanged(bool value)
    {
        if (!_loading)
            _ = UpdateHelloAsync(value);
    }

    private async Task UpdateHelloAsync(bool enable)
    {
        HelloMessage = null;
        if (!enable)
        {
            await _vault.DisableHelloAsync();
            return;
        }

        if (_vault.State != VaultState.Unlocked &&
            !await _dialogs.ShowAsync(new VaultDialogViewModel(_vault, "Enter the master password to set up Windows Hello.")))
        {
            SyncVaultToggles();
            return;
        }

        var status = await _vault.EnableHelloAsync();
        HelloMessage = status switch
        {
            HelloStatus.Success => null,
            HelloStatus.Cancelled => "Windows Hello was cancelled.",
            HelloStatus.Unavailable => "Windows Hello is not set up on this computer.",
            _ => "Windows Hello could not be set up.",
        };
        SyncVaultToggles();
    }

    /// <summary>Hello and automatic unlock exclude each other; show what the vault actually has.</summary>
    private void SyncVaultToggles()
    {
        _loading = true;
        UnlockWithHello = _vault.IsHelloEnabled;
        UnlockOnDevice = _vault.IsRememberedOnDevice;
        _loading = false;
    }

    public override string Title => "Settings";

    public override double Width => 520;

    [ObservableProperty]
    public partial bool IsSystemTheme { get; set; }

    [ObservableProperty]
    public partial bool IsLightTheme { get; set; }

    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; }

    [ObservableProperty]
    public partial string TerminalFontFamily { get; set; }

    [ObservableProperty]
    public partial decimal? TerminalFontSize { get; set; }

    [ObservableProperty]
    public partial decimal? ScrollbackLines { get; set; }

    [ObservableProperty]
    public partial bool RememberHistory { get; set; }

    [ObservableProperty]
    public partial bool RestoreTabs { get; set; }

    public IReadOnlyList<LogLevelOption> LogLevels { get; } =
    [
        new(DiagnosticLevel.Errors, "Errors only"),
        new(DiagnosticLevel.Normal, "Normal"),
        new(DiagnosticLevel.Verbose, "Verbose"),
    ];

    [ObservableProperty]
    public partial LogLevelOption SelectedLogLevel { get; set; }

    partial void OnSelectedLogLevelChanged(LogLevelOption value)
    {
        if (!_loading)
            _settings.Update(s => s with { DiagnosticLevel = value.Level });
    }

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_logDirectory);

    public IReadOnlyList<SessionLogFormatOption> SessionLogFormats { get; } =
    [
        new(Sshelfy.Core.Terminal.SessionLogFormat.PlainText, "Plain text (.log)"),
        new(Sshelfy.Core.Terminal.SessionLogFormat.Asciicast, "Replayable (.cast)"),
    ];

    [ObservableProperty]
    public partial SessionLogFormatOption SelectedSessionLogFormat { get; set; }

    [ObservableProperty]
    public partial bool SessionLogTimestamps { get; set; }

    [ObservableProperty]
    public partial bool EncryptSessionLogs { get; set; }

    [ObservableProperty]
    public partial decimal? SessionLogRetentionDays { get; set; }

    [ObservableProperty]
    public partial decimal? SessionLogMaxMegabytes { get; set; }

    public string SessionLogDirectory => _sessionLogs.Directory;

    partial void OnSelectedSessionLogFormatChanged(SessionLogFormatOption value)
    {
        if (!_loading)
            _settings.Update(s => s with { SessionLogFormat = value.Format });
    }

    partial void OnSessionLogTimestampsChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { SessionLogTimestamps = value });
    }

    partial void OnEncryptSessionLogsChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { EncryptSessionLogs = value });
    }

    partial void OnSessionLogRetentionDaysChanged(decimal? value)
    {
        if (!_loading && value is { } days)
            _settings.Update(s => s with { SessionLogRetentionDays = (int)Math.Clamp(days, 0, 3650) });
    }

    partial void OnSessionLogMaxMegabytesChanged(decimal? value)
    {
        if (!_loading && value is { } mb)
            _settings.Update(s => s with { SessionLogMaxMegabytes = (int)Math.Clamp(mb, 1, 10_000) });
    }

    [RelayCommand]
    private async Task ChangeSessionLogFolder()
    {
        var folder = await _files.PickFolderAsync("Folder for session recordings", SessionLogDirectory);
        if (folder is null)
            return;
        _settings.Update(s => s with { SessionLogDirectory = folder });
        OnPropertyChanged(nameof(SessionLogDirectory));
    }

    [RelayCommand]
    private void OpenSessionLogFolder() => _shell.OpenFolder(SessionLogDirectory);

    public IReadOnlyList<AutoLockOption> AutoLockOptions { get; } =
    [
        new(0, "Never"),
        new(5, "After 5 minutes"),
        new(15, "After 15 minutes"),
        new(30, "After 30 minutes"),
        new(60, "After 1 hour"),
    ];

    [ObservableProperty]
    public partial AutoLockOption SelectedAutoLock { get; set; }

    partial void OnSelectedAutoLockChanged(AutoLockOption value)
    {
        if (!_loading)
            _settings.Update(s => s with { VaultAutoLockMinutes = value.Minutes });
    }

    [ObservableProperty]
    public partial bool LockWithScreen { get; set; }

    partial void OnLockWithScreenChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { LockVaultWithScreen = value });
    }

    [ObservableProperty]
    public partial bool AutoReconnect { get; set; }

    partial void OnAutoReconnectChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { AutoReconnect = value });
    }

    [ObservableProperty]
    public partial bool ShowServerStats { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { CheckForUpdates = value });
    }

    partial void OnShowServerStatsChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { ShowServerStats = value });
    }

    partial void OnRestoreTabsChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { RestoreTabsOnStartup = value });
    }

    [ObservableProperty]
    public partial bool UnlockOnDevice { get; set; }

    public bool CanRememberOnDevice => _vault.CanRememberOnDevice && _vault.State != VaultState.NotCreated;

    public string VaultStatus => _vault.State switch
    {
        VaultState.Unlocked => "Unlocked",
        VaultState.Locked => "Locked",
        _ => "Not set up. You will be asked for a master password the first time you save a secret",
    };

    partial void OnIsSystemThemeChanged(bool value) => ApplyTheme(value, AppTheme.System);

    partial void OnIsLightThemeChanged(bool value) => ApplyTheme(value, AppTheme.Light);

    partial void OnIsDarkThemeChanged(bool value) => ApplyTheme(value, AppTheme.Dark);

    partial void OnTerminalFontFamilyChanged(string value)
    {
        if (!_loading && !string.IsNullOrWhiteSpace(value))
            _settings.Update(s => s with { TerminalFontFamily = value.Trim() });
    }

    partial void OnTerminalFontSizeChanged(decimal? value)
    {
        if (!_loading && value is { } size)
            _settings.Update(s => s with { TerminalFontSize = (double)Math.Clamp(size, 8, 32) });
    }

    partial void OnScrollbackLinesChanged(decimal? value)
    {
        if (!_loading && value is { } lines)
            _settings.Update(s => s with { TerminalScrollbackLines = (int)Math.Clamp(lines, 500, 100_000) });
    }

    partial void OnRememberHistoryChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s with { RememberCommandHistory = value });
    }

    partial void OnUnlockOnDeviceChanged(bool value)
    {
        if (_loading)
            return;
        _ = UpdateDeviceUnlockAsync(value);
    }

    [RelayCommand]
    private void Done() => Close(true);

    private void ApplyTheme(bool selected, AppTheme theme)
    {
        if (selected && !_loading)
            _theme.Apply(theme);
    }

    private async Task UpdateDeviceUnlockAsync(bool enable)
    {
        if (!enable)
        {
            _vault.ForgetDevice();
            return;
        }

        // Remembering needs the key in memory, so the vault has to be unlocked once more.
        if (_vault.State != VaultState.Unlocked || !await _vault.RememberOnDeviceAsync())
        {
            var unlocked = await _dialogs.ShowAsync(new VaultDialogViewModel(_vault, "Enter the master password to remember it on this device."));
            if (!unlocked || !await _vault.RememberOnDeviceAsync())
            {
                _loading = true;
                UnlockOnDevice = _vault.IsRememberedOnDevice;
                _loading = false;
            }
        }

        SyncVaultToggles();
    }
}
