using Sshelfy.Core.Import;

namespace Sshelfy.Infrastructure.Storage;

/// <summary>Reads ~/.ssh/config, its includes, known_hosts and identity files.</summary>
internal sealed class LocalSshFiles : ILocalSshFiles
{
    private const long MaxFileSize = 1024 * 1024;

    private readonly string _sshDirectory;

    public LocalSshFiles()
    {
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LocalUser = Environment.UserName;
        // SSHELFY_SSH_DIR points the import at another folder (testing without touching ~/.ssh).
        _sshDirectory = Environment.GetEnvironmentVariable("SSHELFY_SSH_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(HomeDirectory, ".ssh");
    }

    public string HomeDirectory { get; }

    public string LocalUser { get; }

    public string ConfigPath => Path.Combine(_sshDirectory, "config");

    public string? ReadConfig() => ReadSmall(ConfigPath);

    public string? ReadKnownHosts() => ReadSmall(Path.Combine(_sshDirectory, "known_hosts"));

    public string? ReadKeyFile(string path) => ReadSmall(path);

    public IEnumerable<string> ResolveInclude(string pattern)
    {
        var expanded = pattern.StartsWith('~') ? HomeDirectory + pattern[1..] : pattern;
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(_sshDirectory, expanded);

        var directory = Path.GetDirectoryName(expanded);
        var filePattern = Path.GetFileName(expanded);
        if (directory is null || !Directory.Exists(directory) || string.IsNullOrEmpty(filePattern))
            yield break;

        foreach (var file in Directory.EnumerateFiles(directory, filePattern).Order(StringComparer.Ordinal))
        {
            if (ReadSmall(file) is { } text)
                yield return text;
        }
    }

    private static string? ReadSmall(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileSize ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
