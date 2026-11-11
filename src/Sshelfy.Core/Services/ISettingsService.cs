using Sshelfy.Core.Settings;

namespace Sshelfy.Core.Services;

/// <summary>
/// In-memory view of <see cref="AppSettings"/> with debounced persistence.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    void Update(Func<AppSettings, AppSettings> mutate);

    Task FlushAsync();
}
