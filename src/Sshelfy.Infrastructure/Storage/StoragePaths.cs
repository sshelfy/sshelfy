namespace Sshelfy.Infrastructure.Storage;

/// <summary>
/// Resolves where sshelfy keeps its data. On Windows this is %AppData%\sshelfy,
/// on macOS/Linux the equivalent per-user configuration directory.
/// </summary>
public sealed class StoragePaths
{
    public StoragePaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "sshelfy");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);
}
