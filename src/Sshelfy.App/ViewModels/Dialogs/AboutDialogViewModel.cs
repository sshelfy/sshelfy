using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>Version, license, project links, the update check and problem reports.</summary>
public sealed partial class AboutDialogViewModel(IShellIntegration shell, string dataDirectory, UpdateService? updates = null) : DialogViewModel<bool>
{
    public override string Title => "About sshelfy";

    public override double Width => 420;

    public string Version => $"Version {AppInfo.Version}";

    public string Copyright => AppInfo.Copyright;

    public string Runtime => $".NET {Environment.Version}  ·  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    public bool CanCheckForUpdates => updates is not null;

    /// <summary>Result of the last check, e.g. "sshelfy 1.2.0 is available".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatus))]
    public partial string? UpdateStatus { get; private set; } = updates?.UpdateText;

    public bool HasUpdateStatus => UpdateStatus is not null;

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; private set; } = updates?.IsUpdateAvailable == true;

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (updates is null || IsChecking)
            return;
        IsChecking = true;
        UpdateStatus = "Checking…";
        try
        {
            var result = await updates.CheckNowAsync();
            IsUpdateAvailable = result == UpdateCheckResult.Available;
            UpdateStatus = result switch
            {
                UpdateCheckResult.Available => updates.UpdateText,
                UpdateCheckResult.UpToDate => "You have the latest version.",
                _ => "No release found (offline, or none published yet).",
            };
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (updates?.Available is { } release)
            shell.OpenUrl(release.Url);
    }

    [RelayCommand]
    private void ReportProblem() => shell.OpenUrl(UpdateService.ReportProblemUrl());

    [RelayCommand]
    private void OpenRepository() => shell.OpenUrl(AppInfo.RepositoryUrl);

    [RelayCommand]
    private void OpenSupport() => shell.OpenUrl(AppInfo.SupportUrl);

    [RelayCommand]
    private void OpenLicense() => shell.OpenUrl(AppInfo.RepositoryUrl + "/blob/main/LICENSE");

    [RelayCommand]
    private void OpenNotices() => shell.OpenUrl(AppInfo.RepositoryUrl + "/blob/main/THIRD-PARTY-NOTICES.md");

    [RelayCommand]
    private void OpenChangelog() => shell.OpenUrl(AppInfo.RepositoryUrl + "/blob/main/CHANGELOG.md");

    [RelayCommand]
    private void OpenDataFolder() => shell.OpenFolder(dataDirectory);

    [RelayCommand]
    private void Done() => Close(true);
}
