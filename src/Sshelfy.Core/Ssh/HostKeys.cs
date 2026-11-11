using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Activity;

namespace Sshelfy.Core.Ssh;

public sealed record KnownHost(string Host, int Port, string Algorithm, string Fingerprint, DateTimeOffset AddedAt);

public enum HostKeyStatus
{
    /// <summary>First connection to this host.</summary>
    Unknown,

    /// <summary>The host presented a different key than the one remembered. Possible MITM.</summary>
    Changed,

    /// <summary>
    /// Keys of other types are known for this host, but none of the presented type. An attacker who
    /// cannot forge the known key may offer another type, so this is not a plain first connection.
    /// </summary>
    OtherTypeKnown,
}

public sealed record HostKeyChallenge(
    string Host,
    int Port,
    string Algorithm,
    string Fingerprint,
    HostKeyStatus Status,
    string? PreviousFingerprint);

/// <summary>Asks the user whether to trust a host key that is not (or no longer) known.</summary>
public interface IHostKeyPrompt
{
    Task<bool> ConfirmAsync(HostKeyChallenge challenge);
}

/// <summary>Decides whether a host key presented during the handshake is trusted.</summary>
public interface IHostKeyVerifier
{
    Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint);
}

/// <summary>Trust-on-first-use against the known hosts store, asking the user for anything new or changed.</summary>
public sealed class KnownHostsVerifier : IHostKeyVerifier
{
    private readonly IKnownHostsStore _store;
    private readonly IHostKeyPrompt _prompt;
    private readonly IActivityLog? _activity;

    public KnownHostsVerifier(IKnownHostsStore store, IHostKeyPrompt prompt, IActivityLog? activity = null)
    {
        _store = store;
        _prompt = prompt;
        _activity = activity;
    }

    public async Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint)
    {
        var known = await _store.FindAsync(host, port).ConfigureAwait(false);
        if (known.Any(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.Ordinal)))
            return true;

        // A server may offer another algorithm than the key we know (e.g. after importing known_hosts):
        // only a different key of the *same* algorithm, or no known key at all, is suspicious.
        var sameAlgorithm = known.FirstOrDefault(k => string.Equals(k.Algorithm, algorithm, StringComparison.Ordinal));
        var status = sameAlgorithm is not null ? HostKeyStatus.Changed
            : known.Count > 0 ? HostKeyStatus.OtherTypeKnown
            : HostKeyStatus.Unknown;
        var previous = sameAlgorithm?.Fingerprint ?? (known.Count > 0 ? $"{known[0].Algorithm} {known[0].Fingerprint}" : null);
        var challenge = new HostKeyChallenge(host, port, algorithm, fingerprint, status, previous);

        var endpoint = port == 22 ? host : $"{host}:{port}";
        if (!await _prompt.ConfirmAsync(challenge).ConfigureAwait(false))
        {
            _activity?.Add(ActivityKind.HostKeyRejected, endpoint, $"Rejected {(status == HostKeyStatus.Changed ? "changed" : "new")} {algorithm} key {fingerprint}");
            return false;
        }

        _activity?.Add(status == HostKeyStatus.Changed ? ActivityKind.HostKeyChanged : ActivityKind.HostKeyTrusted, endpoint,
            status == HostKeyStatus.Changed
                ? $"Accepted a changed {algorithm} key {fingerprint} (was {sameAlgorithm?.Fingerprint})"
                : $"Trusted {algorithm} key {fingerprint}");

        var entry = new KnownHost(host, port, algorithm, fingerprint, DateTimeOffset.UtcNow);
        if (status == HostKeyStatus.Changed)
            await _store.ReplaceAllAsync(entry).ConfigureAwait(false);
        else
            await _store.AddAsync(entry).ConfigureAwait(false);
        return true;
    }
}
