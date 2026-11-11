using System.Text;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

[Collection(SshServerCollection.Name)]
public sealed class FilePreviewTests
{
    [SshFact]
    public async Task Shows_text_the_end_of_large_files_and_skips_binaries()
    {
        await using var session = await ConnectAsync();
        var folder = RemotePath.Combine(session.HomeDirectory, "preview-" + Guid.NewGuid().ToString("N")[..8]);
        await session.CreateDirectoryAsync(folder);
        try
        {
            var small = await PutAsync(session, folder, "notes.md", Encoding.UTF8.GetBytes("# Zażółć\nline 2\n"));
            var preview = await FilePreview.LoadAsync(session, small);
            Assert.Equal(PreviewKind.Text, preview.Kind);
            Assert.Equal("# Zażółć\nline 2\n", preview.Text);
            Assert.False(preview.IsPartial);

            var lines = string.Concat(Enumerable.Range(1, 30_000).Select(i => $"log line {i:D6}\n"));
            var large = await PutAsync(session, folder, "app.log", Encoding.UTF8.GetBytes(lines));
            preview = await FilePreview.LoadAsync(session, large);
            Assert.True(preview.IsPartial);
            Assert.EndsWith("log line 030000\n", preview.Text);
            Assert.StartsWith("log line ", preview.Text); // the cut first line was dropped
            Assert.Equal(large.Size, preview.EndOffset);

            var binary = await PutAsync(session, folder, "data.bin", [1, 2, 0, 3]);
            Assert.Equal(PreviewKind.None, (await FilePreview.LoadAsync(session, binary)).Kind);

            var png = await PutAsync(session, folder, "pixel.png", [0x89, 0x50, 0x4E, 0x47]);
            preview = await FilePreview.LoadAsync(session, png);
            Assert.Equal(PreviewKind.Image, preview.Kind);
            Assert.Equal(4, preview.Image!.Length);
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(folder))!);
        }
    }

    [SshFact]
    public async Task A_file_without_end_is_read_only_up_to_the_limit()
    {
        await using var session = await ConnectAsync();
        var zero = (await session.StatAsync("/dev/zero"))!;   // listed as 0 bytes, never ends
        var random = (await session.StatAsync("/dev/urandom"))! with { Name = "random.png" };

        var text = await FilePreview.LoadAsync(session, zero).WaitAsync(TimeSpan.FromSeconds(20));
        var image = await FilePreview.LoadAsync(session, random).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(PreviewKind.None, text.Kind);  // NUL bytes: binary
        Assert.Equal(PreviewKind.None, image.Kind); // more than the image limit
    }

    [SshFact]
    public async Task Following_a_file_reads_only_what_was_appended()
    {
        await using var session = await ConnectAsync();
        var path = RemotePath.Combine(session.HomeDirectory, $"tail-{Guid.NewGuid():N}.log");
        var entry = await PutAsync(session, session.HomeDirectory, RemotePath.Name(path), "start\n"u8.ToArray());
        try
        {
            var preview = await FilePreview.LoadAsync(session, entry);
            var tail = new PreviewTail(session, path, preview.EndOffset);
            Assert.Equal(string.Empty, await tail.ReadAsync());

            // "ż" split across two appends still decodes once complete.
            var bytes = Encoding.UTF8.GetBytes("next ż\n");
            await AppendAsync(session, path, bytes[..6]);
            Assert.Equal("next ", await tail.ReadAsync());
            await AppendAsync(session, path, bytes[6..]);
            Assert.Equal("ż\n", await tail.ReadAsync());

            await PutAsync(session, session.HomeDirectory, RemotePath.Name(path), "rotated\n"u8.ToArray());
            Assert.Null(await tail.ReadAsync());
        }
        finally
        {
            await session.DeleteAsync((await session.StatAsync(path))!);
        }
    }

    private static async Task<RemoteFileEntry> PutAsync(ISftpSession session, string folder, string name, byte[] content)
    {
        var path = RemotePath.Combine(folder, name);
        using (var stream = new MemoryStream(content))
            await session.UploadAsync(stream, path, null);
        return (await session.StatAsync(path))!;
    }

    private static async Task AppendAsync(ISftpSession session, string path, byte[] content)
    {
        using var stream = new MemoryStream(content);
        await session.AppendAsync(stream, path, null);
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

    private sealed class AlwaysTrust : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
