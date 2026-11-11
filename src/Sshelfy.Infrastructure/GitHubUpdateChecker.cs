using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sshelfy.Core.Services;

namespace Sshelfy.Infrastructure;

/// <summary>Reads the latest published (non-draft, non-prerelease) release of a GitHub repository.</summary>
/// <param name="repository">"owner/name".</param>
public sealed class GitHubUpdateChecker(HttpClient http, string repository, ILogger<GitHubUpdateChecker>? logger = null) : IUpdateChecker
{
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("sshelfy", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null; // 404 before the first release, rate limits…

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            if (ReleaseVersions.Parse(tag) is not { } version || url is null || !url.StartsWith("https://github.com/", StringComparison.Ordinal))
                return null;

            return new ReleaseInfo(version.ToString(3), url, notes);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            if (!cancellationToken.IsCancellationRequested)
                logger?.LogInformation("Update check failed: {Error}", ex.Message);
            return null;
        }
    }
}
