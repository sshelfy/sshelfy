using Sshelfy.Core.Sftp;

namespace Sshelfy.Infrastructure.Storage;

internal sealed class LocalFileSystem : ILocalFileSystem
{
    public string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public Task<IReadOnlyList<LocalFileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<LocalFileEntry>>(() =>
        {
            if (path.Length == 0)
                return Drives();

            try
            {
                var directory = new DirectoryInfo(path);
                var entries = new List<LocalFileEntry>();
                foreach (var info in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                    var hidden = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.Name.StartsWith('.');
                    entries.Add(new LocalFileEntry(
                        info.Name,
                        info.FullName,
                        isDirectory,
                        info is FileInfo file ? file.Length : 0,
                        info.LastWriteTime,
                        hidden));
                }

                return entries;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new LocalFileException(ex.Message, ex);
            }
        }, cancellationToken);

    public string? Parent(string path)
    {
        if (path.Length == 0)
            return null;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (parent is not null)
            return parent;

        // A drive root: on Windows go to the drive list; "/" has nothing above it.
        return OperatingSystem.IsWindows() ? string.Empty : null;
    }

    public string Combine(string directory, string name) => Path.Combine(directory, name);

    public Task CreateDirectoryAsync(string path) => Run(() => Directory.CreateDirectory(path));

    public Task RenameAsync(string from, string to) => Run(() =>
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    });

    public Task DeleteAsync(LocalFileEntry entry) => Run(() =>
    {
        if (entry.IsDirectory)
            Directory.Delete(entry.FullPath, recursive: true);
        else
            File.Delete(entry.FullPath);
    });

    private static List<LocalFileEntry> Drives()
    {
        var result = new List<LocalFileEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string label;
            try
            {
                label = drive.IsReady && drive.VolumeLabel.Length > 0 ? $"{drive.Name.TrimEnd('\\')}  {drive.VolumeLabel}" : drive.Name.TrimEnd('\\');
            }
            catch (IOException)
            {
                label = drive.Name;
            }

            result.Add(new LocalFileEntry(label, drive.RootDirectory.FullName, true, 0, DateTimeOffset.MinValue, false));
        }

        return result;
    }

    private static Task Run(Action action) => Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalFileException(ex.Message, ex);
        }
    });
}
