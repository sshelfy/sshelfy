namespace Sshelfy.Core.Sftp;

/// <summary>
/// Turns names that come from a server into safe local file names. On Linux "\", ":" and "C:" are
/// ordinary characters in a name, so a remote "..\..\Startup\x.bat" or "a:stream" must never reach
/// Path.Combine unchanged.
/// </summary>
public static class LocalNames
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A single path component: separators, drive and stream colons, wildcards and controls become "_".</summary>
    public static string Safe(string name)
    {
        var chars = name.Select(c => c < 32 || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray();
        var safe = new string(chars).TrimEnd('.', ' ');
        if (safe.Length == 0 || safe is "." or "..")
            return "_";

        var stem = safe.Split('.')[0];
        return ReservedNames.Contains(stem.TrimEnd(' ')) ? "_" + safe : safe;
    }

    /// <summary><paramref name="remoteName"/> made safe and combined with <paramref name="directory"/>, guaranteed to stay inside it.</summary>
    /// <exception cref="InvalidOperationException">The result would leave the directory (should not happen after <see cref="Safe"/>).</exception>
    public static string Child(string directory, string remoteName)
    {
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, Safe(remoteName)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new InvalidOperationException($"\"{remoteName}\" is not a valid local file name.");
    }

    /// <summary>
    /// Marks a downloaded file as coming from another computer (Windows "Mark of the Web"), so Windows
    /// warns before running it, as it does for browser downloads. Ignored where streams are unsupported.
    /// </summary>
    public static void MarkAsDownloaded(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // FAT drives and network shares without alternate streams.
        }
    }
}
