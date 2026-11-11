using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Core.Tests;

[Collection(SshServerCollection.Name)]
public sealed class FolderComparisonTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _local = Path.Combine(Path.GetTempPath(), "sshelfy-compare-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_local))
            Directory.Delete(_local, recursive: true);
    }

    [Fact]
    public void Pairs_files_by_path_and_time()
    {
        var local = new Dictionary<string, LocalFileEntry>
        {
            ["same.txt"] = Local("same.txt", 10, Noon.AddSeconds(1)), // within the 2 s tolerance
            ["newer-here.txt"] = Local("newer-here.txt", 10, Noon.AddMinutes(5)),
            ["size.txt"] = Local("size.txt", 11, Noon),
            ["only-here.txt"] = Local("only-here.txt", 1, Noon),
            ["new-dir"] = Local("new-dir", 0, Noon, directory: true),
            ["new-dir/inner.txt"] = Local("inner.txt", 1, Noon),
            ["shared"] = Local("shared", 0, Noon, directory: true),
            ["clash"] = Local("clash", 0, Noon, directory: true),
        };
        var remote = new Dictionary<string, RemoteFileEntry>
        {
            ["same.txt"] = Remote("same.txt", 10, Noon),
            ["newer-here.txt"] = Remote("newer-here.txt", 10, Noon),
            ["size.txt"] = Remote("size.txt", 10, Noon),
            ["shared"] = Remote("shared", 0, Noon, directory: true),
            ["shared/newer-there.txt"] = Remote("newer-there.txt", 5, Noon.AddDays(1)),
            ["clash"] = Remote("clash", 3, Noon),
        };
        local["shared/newer-there.txt"] = Local("newer-there.txt", 5, Noon);

        var items = FolderComparison.Merge(local, remote).ToDictionary(i => i.RelativePath, i => i.Status);

        Assert.Equal(new Dictionary<string, CompareStatus>
        {
            ["clash"] = CompareStatus.TypeMismatch,
            ["new-dir"] = CompareStatus.OnlyLocal, // its files are not listed separately
            ["newer-here.txt"] = CompareStatus.LocalNewer,
            ["only-here.txt"] = CompareStatus.OnlyLocal,
            ["same.txt"] = CompareStatus.Same,
            ["shared/newer-there.txt"] = CompareStatus.RemoteNewer,
            ["size.txt"] = CompareStatus.Different,
        }, items);
    }

    [SshFact]
    public async Task Copies_what_differs_until_both_sides_match()
    {
        await using var session = await ConnectAsync();
        var remoteRoot = RemotePath.Combine(session.HomeDirectory, "compare-" + Guid.NewGuid().ToString("N")[..8]);
        await session.CreateDirectoryAsync(remoteRoot);
        try
        {
            Directory.CreateDirectory(Path.Combine(_local, "docs"));
            await File.WriteAllTextAsync(Path.Combine(_local, "docs", "local.md"), "from the PC");
            await File.WriteAllTextAsync(Path.Combine(_local, "top.txt"), "top");
            await session.CreateDirectoryAsync(RemotePath.Combine(remoteRoot, "logs"));
            using (var stream = new MemoryStream("server log"u8.ToArray()))
                await session.UploadAsync(stream, RemotePath.Combine(remoteRoot, "logs/app.log"), null);

            var files = new LocalFileSystem();
            var before = await FolderComparison.CompareAsync(files, _local, session, remoteRoot);
            Assert.Equal(["docs", "logs", "top.txt"], before.Select(i => i.RelativePath));

            var queue = new TransferQueue();
            var finished = new TaskCompletionSource();
            var remaining = before.Count;
            queue.Finished += (_, _) =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                    finished.TrySetResult();
            };
            var transfers = await FolderSync.EnqueueAsync(before.Select(i => (i, i.SuggestedDirection!.Value)), queue, session, "test", _local, remoteRoot);
            Assert.Equal(3, transfers.Count);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(transfers, t => Assert.Equal(TransferState.Completed, t.State));

            Assert.Equal("server log", await File.ReadAllTextAsync(Path.Combine(_local, "logs", "app.log")));
            var after = await FolderComparison.CompareAsync(files, _local, session, remoteRoot);
            Assert.All(after, i => Assert.Equal(CompareStatus.Same, i.Status)); // times travel with the copies
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(remoteRoot))!);
        }
    }

    private static LocalFileEntry Local(string name, long size, DateTimeOffset modified, bool directory = false) =>
        new(name, name, directory, size, modified, false);

    private static RemoteFileEntry Remote(string name, long size, DateTimeOffset modified, bool directory = false) =>
        new(name, "/r/" + name, directory, false, size, modified, "rw-r--r--");

    private static async Task<ISftpSession> ConnectAsync()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        var target = new ConnectionTarget { Address = endpoint[0], Port = int.Parse(endpoint[1]), Username = credentials[0], Password = credentials[1] };
        return await new SshNetSftpSessionFactory(new AlwaysTrust()).ConnectAsync(target);
    }

    private sealed class AlwaysTrust : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
