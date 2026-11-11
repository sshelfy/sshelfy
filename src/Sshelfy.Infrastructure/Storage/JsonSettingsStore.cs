using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Settings;

namespace Sshelfy.Infrastructure.Storage;

internal sealed class JsonSettingsStore : ISettingsStore
{
    private readonly JsonFileStore<AppSettings> _store;

    public JsonSettingsStore(StoragePaths paths)
    {
        _store = new JsonFileStore<AppSettings>(paths.File("settings.json"), () => new AppSettings());
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => _store.ReadAsync(cancellationToken);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _store.WriteAsync(settings, cancellationToken);
}
