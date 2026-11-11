using System.Text;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

[Collection(SshServerCollection.Name)]
public sealed class ExternalEditSyncTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sshelfy-edit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    [SshFact]
    public async Task Saves_in_the_local_editor_reach_the_server_once_per_change()
    {
        await using var session = await ConnectAsync();
        var remotePath = RemotePath.Combine(session.HomeDirectory, $"edit-{Guid.NewGuid():N}.conf");
        await UploadAsync(session, remotePath, "port = 80\n");
        var callbacks = new Callbacks();

        try
        {
            var entry = (await session.StatAsync(remotePath))!;
            var sync = await ExternalEditSync.StartAsync(session, entry, _temp, callbacks);
            Assert.Equal("port = 80\n", await File.ReadAllTextAsync(sync.LocalPath));
            Assert.EndsWith(".conf", sync.LocalPath);

            // An editor that saves by writing a temp file and renaming it over the original.
            var temp = sync.LocalPath + ".tmp";
            await File.WriteAllTextAsync(temp, "port = 8080\n");
            File.Move(temp, sync.LocalPath, overwrite: true);
            await WaitForAsync(() => callbacks.Uploads == 1);
            await sync.FlushAsync();
            Assert.Equal("port = 8080\n", await DownloadAsync(session, remotePath));

            // Saving without changes uploads nothing.
            File.SetLastWriteTimeUtc(sync.LocalPath, DateTime.UtcNow.AddSeconds(5));
            await Task.Delay(800);
            await sync.FlushAsync();
            Assert.Equal(1, callbacks.Uploads);

            await sync.DisposeAsync();
            Assert.False(Directory.Exists(Path.GetDirectoryName(sync.LocalPath)));
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(remotePath))!);
        }
    }

    [SshFact]
    public async Task A_file_changed_on_the_server_is_not_overwritten_without_asking()
    {
        await using var session = await ConnectAsync();
        var remotePath = RemotePath.Combine(session.HomeDirectory, $"edit-{Guid.NewGuid():N}.txt");
        await UploadAsync(session, remotePath, "original\n");
        var callbacks = new Callbacks { AllowOverwrite = false };

        try
        {
            var sync = await ExternalEditSync.StartAsync(session, (await session.StatAsync(remotePath))!, _temp, callbacks);
            await Task.Delay(1100); // SFTP times have one-second resolution
            await UploadAsync(session, remotePath, "changed by a colleague\n");

            await File.WriteAllTextAsync(sync.LocalPath, "my version\n");
            await WaitForAsync(() => callbacks.Confirms == 1);
            await sync.FlushAsync();

            Assert.Equal(0, callbacks.Uploads);
            Assert.Equal("changed by a colleague\n", await DownloadAsync(session, remotePath));
            await sync.DisposeAsync();
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(remotePath))!);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(100);
        Assert.True(condition(), "Timed out waiting for the upload.");
    }

    private static async Task UploadAsync(ISftpSession session, string path, string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await session.UploadAsync(stream, path, null);
    }

    private static async Task<string> DownloadAsync(ISftpSession session, string path)
    {
        using var stream = new MemoryStream();
        await session.DownloadAsync(path, stream, null);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<ISftpSession> ConnectAsync()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        var target = new ConnectionTarget { Address = endpoint[0], Port = int.Parse(endpoint[1]), Username = credentials[0], Password = credentials[1] };
        return await new SshNetSftpSessionFactory(new AlwaysTrust()).ConnectAsync(target);
    }

    private sealed class Callbacks : IExternalEditCallbacks
    {
        public bool AllowOverwrite { get; init; } = true;

        public int Uploads;

        public int Confirms;

        public Task<bool> ConfirmOverwriteAsync(string remotePath)
        {
            Interlocked.Increment(ref Confirms);
            return Task.FromResult(AllowOverwrite);
        }

        public void Uploaded(string remotePath) => Interlocked.Increment(ref Uploads);

        public void Failed(string remotePath, string message) => throw new Xunit.Sdk.XunitException(message);
    }

    private sealed class AlwaysTrust : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
