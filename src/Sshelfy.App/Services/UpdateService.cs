using CommunityToolkit.Mvvm.ComponentModel;
using Sshelfy.Core.Services;

namespace Sshelfy.App.Services;

public enum UpdateCheckResult
{
    /// <summary>Offline, GitHub unreachable or no release published yet.</summary>
    Failed,
    UpToDate,
    Available,
}

/// <summary>Finds out whether a newer release exists: once a day in the background (if enabled) or on request.</summary>
public sealed partial class UpdateService(IUpdateChecker checker, ISettingsService settings) : ObservableObject
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>A release newer than this build, once one was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable), nameof(UpdateText))]
    public partial ReleaseInfo? Available { get; private set; }

    public bool IsUpdateAvailable => Available is not null;

    public string? UpdateText => Available is { } release ? $"sshelfy {release.Version} is available" : null;

    /// <summary>Background check shortly after start-up, at most once a day.</summary>
    public async Task CheckInBackgroundAsync()
    {
        var current = settings.Current;
        if (!current.CheckForUpdates || current.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < CheckInterval)
            return;

        await Task.Delay(TimeSpan.FromSeconds(10));
        await CheckNowAsync();
    }

    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        var latest = await checker.GetLatestAsync();
        if (latest is null)
            return UpdateCheckResult.Failed;

        settings.Update(s => s with { LastUpdateCheck = DateTimeOffset.UtcNow });
        Available = ReleaseVersions.IsNewer(latest.Version, AppInfo.Version) ? latest : null;
        return Available is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available;
    }

    /// <summary>A new issue on GitHub with the version and Windows version already filled in (issue form field ids).</summary>
    public static string ReportProblemUrl() =>
        $"{AppInfo.IssuesUrl}/new?template=bug_report.yml&version={Uri.EscapeDataString(AppInfo.Version)}" +
        $"&os={Uri.EscapeDataString(System.Runtime.InteropServices.RuntimeInformation.OSDescription)}";
}
