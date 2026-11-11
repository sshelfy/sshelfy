using System.Net;
using System.Text;
using Sshelfy.Core.Services;
using Sshelfy.Infrastructure;

namespace Sshelfy.Core.Tests;

public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("v1.2", "1.2.0", false)]
    [InlineData("1.2.0-beta.1", "1.2.0", false)]
    [InlineData("v2.0.0", "1.9.9+abc123", true)]
    [InlineData("nightly", "1.0.0", false)]
    [InlineData(null, "1.0.0", false)]
    public void Compares_release_versions(string? candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersions.IsNewer(candidate, current));

    [Fact]
    public async Task Reads_the_latest_github_release()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{ "tag_name": "v1.3.0", "html_url": "https://github.com/owner/app/releases/tag/v1.3.0", "body": "Faster SFTP" }""");

        var release = await new GitHubUpdateChecker(new HttpClient(handler), "owner/app").GetLatestAsync();

        Assert.Equal(new ReleaseInfo("1.3.0", "https://github.com/owner/app/releases/tag/v1.3.0", "Faster SFTP"), release);
        Assert.Equal("https://api.github.com/repos/owner/app/releases/latest", handler.LastUri);
        Assert.Contains("sshelfy", handler.LastUserAgent);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]                 // no release yet
    [InlineData(HttpStatusCode.Forbidden, "{}")]                // rate limited
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{ "tag_name": "v9.0.0", "html_url": "https://evil.example/download" }""")]
    public async Task Anything_unexpected_means_no_update(HttpStatusCode status, string body) =>
        Assert.Null(await new GitHubUpdateChecker(new HttpClient(new FakeHandler(status, body)), "owner/app").GetLatestAsync());

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }

        public string LastUserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri?.ToString();
            LastUserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
