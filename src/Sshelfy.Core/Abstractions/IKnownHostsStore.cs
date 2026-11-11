using Sshelfy.Core.Ssh;

namespace Sshelfy.Core.Abstractions;

/// <summary>Trusted host keys. A server can have several keys (one per algorithm).</summary>
public interface IKnownHostsStore
{
    Task<IReadOnlyList<KnownHost>> FindAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>Adds a key; an existing key of the same algorithm for that host and port is replaced.</summary>
    Task AddAsync(KnownHost knownHost, CancellationToken cancellationToken = default);

    /// <summary>Forgets every key of the host and port and stores <paramref name="knownHost"/> instead (key rotation).</summary>
    Task ReplaceAllAsync(KnownHost knownHost, CancellationToken cancellationToken = default);
}
