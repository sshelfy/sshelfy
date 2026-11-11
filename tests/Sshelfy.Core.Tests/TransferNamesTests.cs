using Sshelfy.Core.Sftp;

namespace Sshelfy.Core.Tests;

public sealed class TransferNamesTests
{
    [Theory]
    [InlineData("report.pdf", new string[0], "report (1).pdf")]
    [InlineData("report.pdf", new[] { "report (1).pdf", "report (2).pdf" }, "report (3).pdf")]
    [InlineData("Makefile", new string[0], "Makefile (1)")]
    [InlineData(".bashrc", new string[0], ".bashrc (1)")]
    [InlineData("archive.tar.gz", new string[0], "archive.tar (1).gz")]
    public async Task Picks_the_first_free_numbered_name(string name, string[] taken, string expected)
    {
        var free = await TransferNames.NextFreeAsync(name, candidate => Task.FromResult(taken.Contains(candidate)));

        Assert.Equal(expected, free);
    }
}
