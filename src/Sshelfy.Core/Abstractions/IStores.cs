using Sshelfy.Core.Activity;
using Sshelfy.Core.History;
using Sshelfy.Core.Snippets;

namespace Sshelfy.Core.Abstractions;

public interface ISnippetRepository
{
    Task<List<Snippet>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<Snippet> snippets, CancellationToken cancellationToken = default);
}

public interface IActivityStore
{
    Task<List<ActivityEntry>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<ActivityEntry> entries, CancellationToken cancellationToken = default);
}

public interface ICommandHistoryStore
{
    /// <summary>False while the history cannot be read or written (it is encrypted and the vault is locked).</summary>
    bool IsAvailable => true;

    /// <summary>Raised when <see cref="IsAvailable"/> may have changed.</summary>
    event EventHandler? AvailabilityChanged
    {
        add { }
        remove { }
    }

    Task<CommandHistoryData> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CommandHistoryData data, CancellationToken cancellationToken = default);
}
