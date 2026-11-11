namespace Sshelfy.Core.Sftp;

public sealed record LocalFileEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTimeOffset Modified, bool IsHidden);

/// <summary>
/// The local side of the two-panel file browser. An empty path stands for the list of drives
/// (Windows) or the file system root's parent, so "up" from a drive root keeps working.
/// </summary>
public interface ILocalFileSystem
{
    string HomeDirectory { get; }

    /// <exception cref="LocalFileException">The folder cannot be read.</exception>
    Task<IReadOnlyList<LocalFileEntry>> ListAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Parent folder, the drive list ("") for a drive root, or null when already at the top.</summary>
    string? Parent(string path);

    string Combine(string directory, string name);

    Task CreateDirectoryAsync(string path);

    Task RenameAsync(string from, string to);

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    Task DeleteAsync(LocalFileEntry entry);
}

public sealed class LocalFileException(string message, Exception? inner = null) : Exception(message, inner);
