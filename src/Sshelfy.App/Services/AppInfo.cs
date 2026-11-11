using System.Reflection;

namespace Sshelfy.App.Services;

/// <summary>Version and project links shown in About, update checks and bug reports.</summary>
public static class AppInfo
{
    /// <summary>GitHub "owner/name": source, issues and the releases the update check reads.</summary>
    public const string Repository = "sshelfy/sshelfy";

    public const string RepositoryUrl = "https://github.com/" + Repository;

    public const string IssuesUrl = RepositoryUrl + "/issues";

    public const string ReleasesUrl = RepositoryUrl + "/releases";

    /// <summary>Donations (also in .github/FUNDING.yml and the README).</summary>
    public const string SupportUrl = "https://ko-fi.com/szymczakovv";

    /// <summary>"1.2.0" (the build adds "+commit" to the informational version; that part is dropped).</summary>
    public static string Version { get; } = ReadVersion();

    public static string Copyright { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
