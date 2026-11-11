namespace Sshelfy.Core.Ssh;

/// <summary>A key held by an SSH agent (OpenSSH agent, Pageant…). Only public data.</summary>
public sealed record AgentKey(string Algorithm, string Fingerprint, string Comment, byte[] PublicKeyBlob);

/// <param name="Source">Human readable agent name, e.g. "Pageant" or "OpenSSH agent".</param>
public sealed record SshAgentStatus(bool IsAvailable, string? Source, IReadOnlyList<AgentKey> Keys, string? Error)
{
    public static SshAgentStatus Unavailable(string message) => new(false, null, [], message);
}

/// <summary>Talks to the user's SSH agent; private keys never leave it.</summary>
public interface ISshAgentClient
{
    Task<SshAgentStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
