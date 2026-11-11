using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

[Collection(SshServerCollection.Name)]
public sealed class SftpIntegrationTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "sshelfy-sftp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_local))
            Directory.Delete(_local, recursive: true);
    }

    private static async Task<ISftpSession> ConnectAsync()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        var target = new ConnectionTarget
        {
            Address = endpoint[0],
            Port = int.Parse(endpoint[1]),
            Username = credentials[0],
            Password = credentials[1],
        };

        var verifier = new AlwaysTrust();
        return await new SshNetSftpSessionFactory(verifier).ConnectAsync(target);
    }

    private static Task<TransferItem> RunAsync(TransferQueue queue, Func<TransferItem> enqueue)
    {
        var tcs = new TaskCompletionSource<TransferItem>();
        TransferItem? item = null;
        queue.Finished += (_, finished) =>
        {
            if (finished == item)
                tcs.TrySetResult(finished);
        };
        item = enqueue();
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [SshFact]
    public async Task Upload_list_download_and_delete_directory_tree()
    {
        await using var session = await ConnectAsync();
        var remoteRoot = RemotePath.Combine(session.HomeDirectory, "sf-test-" + Guid.NewGuid().ToString("N")[..8]);
        await session.CreateDirectoryAsync(remoteRoot);

        // Local tree: dir/a.txt, dir/sub/b.bin
        var source = Path.Combine(_local, "up", "dir");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "hello sftp");
        var blob = new byte[300_000];
        Random.Shared.NextBytes(blob);
        await File.WriteAllBytesAsync(Path.Combine(source, "sub", "b.bin"), blob);

        var queue = new TransferQueue();
        var upload = await RunAsync(queue, () => queue.EnqueueUpload(session, "test", source, remoteRoot));
        Assert.Equal(TransferState.Completed, upload.State);
        Assert.Equal(blob.Length + 10, upload.TotalBytes);

        var listing = await session.ListAsync(RemotePath.Combine(remoteRoot, "dir"));
        Assert.Contains(listing, e => e.Name == "a.txt" && !e.IsDirectory && e.Size == 10);
        Assert.Contains(listing, e => e.Name == "sub" && e.IsDirectory);

        var downloadTarget = Path.Combine(_local, "down");
        Directory.CreateDirectory(downloadTarget);
        var dirEntry = (await session.StatAsync(RemotePath.Combine(remoteRoot, "dir")))!;
        var download = await RunAsync(queue, () => queue.EnqueueDownload(session, "test", dirEntry, downloadTarget));
        Assert.Equal(TransferState.Completed, download.State);
        Assert.Equal("hello sftp", await File.ReadAllTextAsync(Path.Combine(downloadTarget, "dir", "a.txt")));
        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(downloadTarget, "dir", "sub", "b.bin")));

        await session.RenameAsync(RemotePath.Combine(remoteRoot, "dir"), RemotePath.Combine(remoteRoot, "renamed"));
        Assert.NotNull(await session.StatAsync(RemotePath.Combine(remoteRoot, "renamed")));

        await session.DeleteAsync((await session.StatAsync(remoteRoot))!);
        Assert.Null(await session.StatAsync(remoteRoot));
    }

    [SshFact]
    public async Task Changes_permissions_including_special_bits_and_keeps_the_owner()
    {
        await using var session = await ConnectAsync();
        var path = RemotePath.Combine(session.HomeDirectory, "sf-perm-" + Guid.NewGuid().ToString("N")[..8]);
        await session.UploadAsync(new MemoryStream("x"u8.ToArray()), path, null);
        try
        {
            var before = await session.GetDetailsAsync(path);

            await session.SetPermissionsAsync(path, UnixPermissions.ParseOctal("640")!.Value);
            Assert.Equal("640", UnixPermissions.ToOctal((await session.GetDetailsAsync(path)).Mode));

            await session.SetPermissionsAsync(path, UnixPermissions.ParseOctal("2750")!.Value);
            var after = await session.GetDetailsAsync(path);
            Assert.Equal("2750", UnixPermissions.ToOctal(after.Mode));
            Assert.Equal("-rwxr-s---", (await session.StatAsync(path))!.Permissions);

            // Setting the same owner is allowed for a normal user and must not disturb the mode.
            await session.SetOwnerAsync(path, before.UserId, before.GroupId);
            Assert.Equal((before.UserId, before.GroupId), ((await session.GetDetailsAsync(path)).UserId, (await session.GetDetailsAsync(path)).GroupId));
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(path))!);
        }
    }

    [SshFact]
    public async Task Interrupted_download_continues_from_the_part_file()
    {
        await using var session = await ConnectAsync();
        var blob = new byte[300_000];
        Random.Shared.NextBytes(blob);
        var remote = RemotePath.Combine(session.HomeDirectory, "sf-resume-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        await session.UploadAsync(new MemoryStream(blob), remote, null);
        try
        {
            Directory.CreateDirectory(_local);
            var target = Path.Combine(_local, RemotePath.Name(remote));

            // Pretend the first 100 kB arrived before the connection broke. Zeros instead of the real bytes
            // prove the rest was appended rather than everything downloaded again.
            await File.WriteAllBytesAsync(target + ".part", new byte[100_000]);

            var queue = new TransferQueue();
            var entry = (await session.StatAsync(remote))!;
            var item = await RunAsync(queue, () => queue.EnqueueDownload(session, "test", entry, _local));

            Assert.Equal(TransferState.Completed, item.State);
            var result = await File.ReadAllBytesAsync(target);
            Assert.Equal(blob.Length, result.Length);
            Assert.All(result[..100_000], b => Assert.Equal(0, b));
            Assert.Equal(blob[100_000..], result[100_000..]);
            Assert.False(File.Exists(target + ".part"));
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(remote))!);
        }
    }

    [SshFact]
    public async Task Upload_conflicts_resume_keep_both_and_skip()
    {
        await using var session = await ConnectAsync();
        var folder = RemotePath.Combine(session.HomeDirectory, "sf-conflict-" + Guid.NewGuid().ToString("N")[..8]);
        await session.CreateDirectoryAsync(folder);
        try
        {
            var blob = new byte[200_000];
            Random.Shared.NextBytes(blob);
            Directory.CreateDirectory(_local);
            var source = Path.Combine(_local, "data.bin");
            await File.WriteAllBytesAsync(source, blob);
            var remote = RemotePath.Combine(folder, "data.bin");

            async Task<byte[]> ReadRemote(string path)
            {
                using var buffer = new MemoryStream();
                await session.DownloadAsync(path, buffer, null);
                return buffer.ToArray();
            }

            // Resume: the server has the first half.
            await session.UploadAsync(new MemoryStream(blob[..80_000]), remote, null);
            var resolver = new ScriptedResolver(ConflictChoice.Resume);
            var queue = new TransferQueue(conflicts: resolver);
            Assert.Equal(TransferState.Completed, (await RunAsync(queue, () => queue.EnqueueUpload(session, "test", source, folder))).State);
            Assert.True(resolver.Seen.Single().CanResume);
            Assert.Equal(blob, await ReadRemote(remote));

            // Keep both: a numbered copy appears next to it.
            resolver.Next = ConflictChoice.KeepBoth;
            await RunAsync(queue, () => queue.EnqueueUpload(session, "test", source, folder));
            Assert.Equal(blob, await ReadRemote(RemotePath.Combine(folder, "data (1).bin")));

            // Skip: the existing file stays as it was.
            await session.UploadAsync(new MemoryStream("old"u8.ToArray()), remote, null);
            resolver.Next = ConflictChoice.Skip;
            await RunAsync(queue, () => queue.EnqueueUpload(session, "test", source, folder));
            Assert.Equal("old"u8.ToArray(), await ReadRemote(remote));
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(folder))!);
        }
    }

    [SshFact]
    public async Task Retry_resumes_a_cancelled_download_without_asking_again()
    {
        await using var session = await ConnectAsync();
        var blob = new byte[150_000];
        Random.Shared.NextBytes(blob);
        var remote = RemotePath.Combine(session.HomeDirectory, "sf-retry-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        await session.UploadAsync(new MemoryStream(blob), remote, null);
        try
        {
            Directory.CreateDirectory(_local);
            var target = Path.Combine(_local, RemotePath.Name(remote));
            await File.WriteAllBytesAsync(target, blob[..50_000]);

            var resolver = new ScriptedResolver(ConflictChoice.Cancel);
            var queue = new TransferQueue(conflicts: resolver);
            var entry = (await session.StatAsync(remote))!;
            var first = await RunAsync(queue, () => queue.EnqueueDownload(session, "test", entry, _local));
            Assert.Equal(TransferState.Cancelled, first.State);
            Assert.True(first.CanRetry);

            var retry = await RunAsync(queue, () => queue.Retry(first));

            Assert.Equal(TransferState.Completed, retry.State);
            Assert.Single(resolver.Seen); // the retry did not ask again
            Assert.Equal(blob, await File.ReadAllBytesAsync(target));
            Assert.DoesNotContain(first, queue.Items);
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(remote))!);
        }
    }

    private sealed class ScriptedResolver(ConflictChoice first) : ITransferConflictResolver
    {
        public ConflictChoice Next { get; set; } = first;

        public List<TransferConflict> Seen { get; } = [];

        public Task<ConflictDecision> ResolveAsync(TransferConflict conflict, CancellationToken cancellationToken)
        {
            Seen.Add(conflict);
            return Task.FromResult(new ConflictDecision(Next));
        }
    }

    private sealed class AlwaysTrust : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
