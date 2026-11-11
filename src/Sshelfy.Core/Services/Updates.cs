namespace Sshelfy.Core.Services;

/// <summary>A published release.</summary>
/// <param name="Version">"1.2.0" (without the "v" of the tag).</param>
/// <param name="Url">Release page with the downloads and notes.</param>
public sealed record ReleaseInfo(string Version, string Url, string? Notes);

/// <summary>Asks where releases are published whether there is a newer one.</summary>
public interface IUpdateChecker
{
    /// <returns>The latest release, or null when it cannot be found (offline, no releases yet).</returns>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default);
}

public static class ReleaseVersions
{
    /// <summary>"v1.2.0", "1.2", "1.2.0-beta.1" → comparable version; null for anything else.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!System.Version.TryParse(core, out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        Parse(candidate) is { } c && Parse(current) is { } v && c > v;
}
