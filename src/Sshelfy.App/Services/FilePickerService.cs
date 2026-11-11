using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Sshelfy.App.Services;

public interface IFilePickerService
{
    /// <returns>Local paths of the chosen files (empty when cancelled).</returns>
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool allowMultiple = false, string? startDirectory = null);

    Task<string?> PickFolderAsync(string title, string? startDirectory = null);

    Task<string?> SaveFileAsync(string title, string suggestedName, string? startDirectory = null);
}

internal sealed class FilePickerService : IFilePickerService
{
    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool allowMultiple = false, string? startDirectory = null)
    {
        if (StorageProvider is not { } storage)
            return [];

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await FolderAsync(storage, startDirectory),
        });

        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title, string? startDirectory = null)
    {
        if (StorageProvider is not { } storage)
            return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = await FolderAsync(storage, startDirectory),
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string? startDirectory = null)
    {
        if (StorageProvider is not { } storage)
            return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = await FolderAsync(storage, startDirectory),
        });

        return file?.TryGetLocalPath();
    }

    private static IStorageProvider? StorageProvider =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? TopLevel.GetTopLevel(window)?.StorageProvider
            : null;

    private static async Task<IStorageFolder?> FolderAsync(IStorageProvider storage, string? path) =>
        string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : await storage.TryGetFolderFromPathAsync(path);
}
