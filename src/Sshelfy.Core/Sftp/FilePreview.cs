using System.Text;

namespace Sshelfy.Core.Sftp;

public enum PreviewKind
{
    /// <summary>Nothing to show; <see cref="PreviewContent.Message"/> says why.</summary>
    None,
    Text,
    Image,
}

/// <param name="StartOffset">Where the shown text starts in the file (non-zero when only the end of a large file is shown).</param>
/// <param name="EndOffset">File size when read: where following the file continues.</param>
public sealed record PreviewContent(PreviewKind Kind, string? Text = null, byte[]? Image = null, string? Message = null, long StartOffset = 0, long EndOffset = 0)
{
    public bool IsPartial => StartOffset > 0;
}

/// <summary>Loads what the preview pane shows for a server file.</summary>
public static class FilePreview
{
    /// <summary>Text beyond this is not loaded; for longer files the end is shown (logs grow at the end).</summary>
    public const int MaxTextBytes = 256 * 1024;

    public const long MaxImageBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };

    public static bool IsImage(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    public static async Task<PreviewContent> LoadAsync(ISftpSession session, RemoteFileEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.IsDirectory)
            return new PreviewContent(PreviewKind.None, Message: "Folder");

        if (IsImage(entry.Name))
        {
            if (entry.Size > MaxImageBytes)
                return new PreviewContent(PreviewKind.None, Message: $"Image too large to preview ({entry.Size / 1024 / 1024} MB).");
            // The listed size can be wrong (a device file, a lying server): the read itself is capped.
            var (image, complete) = await ReadCappedAsync(session, entry.FullPath, 0, MaxImageBytes, cancellationToken);
            return complete
                ? new PreviewContent(PreviewKind.Image, Image: image, EndOffset: image.Length)
                : new PreviewContent(PreviewKind.None, Message: "Image too large to preview.");
        }

        var start = Math.Max(0, entry.Size - MaxTextBytes);
        var (bytes, _) = await ReadCappedAsync(session, entry.FullPath, start, MaxTextBytes, cancellationToken);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte)0) >= 0)
            return new PreviewContent(PreviewKind.None, Message: "Binary file: no preview.");

        var text = Encoding.UTF8.GetString(bytes);
        if (start > 0)
        {
            // Starting mid-file: drop the cut first line.
            var newline = text.IndexOf('\n');
            text = newline >= 0 ? text[(newline + 1)..] : text;
        }

        return new PreviewContent(PreviewKind.Text, Text: text, StartOffset: start, EndOffset: start + bytes.Length);
    }

    /// <summary>Reads from <paramref name="offset"/> but stops after <paramref name="limit"/> bytes.</summary>
    /// <returns>The bytes read, and whether the file ended within the limit.</returns>
    internal static async Task<(byte[] Bytes, bool Complete)> ReadCappedAsync(ISftpSession session, string path, long offset, long limit, CancellationToken cancellationToken)
    {
        using var buffer = new CappedStream(limit);
        try
        {
            await session.DownloadFromAsync(path, offset, buffer, null, cancellationToken);
            return (buffer.ToArray(), true);
        }
        catch (CapReachedException)
        {
            return (buffer.ToArray(), false);
        }
    }

    private sealed class CapReachedException : Exception;

    /// <summary>A memory stream that refuses to grow past its limit.</summary>
    private sealed class CappedStream(long limit) : MemoryStream
    {
        // Every write path ends here (MemoryStream's span overloads call back into this one in subclasses).
        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = (int)Math.Max(0, Math.Min(count, limit - Length));
            base.Write(buffer, offset, room);
            if (room < count)
                throw new CapReachedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Reads what was appended to a file since the last read (like tail -f), keeping UTF-8 sequences split across reads intact.</summary>
public sealed class PreviewTail(ISftpSession session, string remotePath, long offset)
{
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();

    private const long MaxReadBytes = 4 * 1024 * 1024;

    public long Offset { get; private set; } = offset;

    /// <returns>New text (empty when nothing changed), or null when the file shrank (rotated or truncated) and should be reloaded.</returns>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var entry = await session.StatAsync(remotePath, cancellationToken);
        if (entry is null || entry.Size < Offset)
            return null;
        if (entry.Size == Offset)
            return string.Empty;

        // A burst larger than the cap is read over several polls.
        var (bytes, _) = await FilePreview.ReadCappedAsync(session, remotePath, Offset, MaxReadBytes, cancellationToken);
        Offset += bytes.Length;

        var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
        _decoder.GetChars(bytes, chars, flush: false);
        return new string(chars);
    }
}
