using System.IO.Pipes;
using Renci.SshNet;
using Renci.SshNet.Security;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh.Agent;

/// <summary>
/// Finds the user's agent: SSH_AUTH_SOCK first (a Unix socket or a Windows pipe path), then on
/// Windows the OpenSSH agent pipe, then Pageant.
/// </summary>
internal sealed class SshAgentClient : ISshAgentClient
{
    private const string OpenSshPipe = @"\\.\pipe\openssh-ssh-agent";

    public Task<SshAgentStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var transport = FindTransport();
            if (transport is null)
                return SshAgentStatus.Unavailable(OperatingSystem.IsWindows()
                    ? "No SSH agent is running. Start Pageant or the OpenSSH Authentication Agent service."
                    : "No SSH agent found (SSH_AUTH_SOCK is not set).");

            try
            {
                var keys = AgentProtocol.ParseIdentities(transport.Request(AgentProtocol.BuildRequestIdentities()));
                return new SshAgentStatus(true, transport.Name, keys, null);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or AgentException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                return SshAgentStatus.Unavailable($"{transport.Name} did not respond: {ex.Message}");
            }
        }, cancellationToken);

    /// <summary>Builds SSH.NET key sources for agent authentication (called from the connect path).</summary>
    /// <exception cref="SshConnectionFailedException">No agent or no matching key.</exception>
    internal static IPrivateKeySource[] CreateKeySources(string? fingerprint)
    {
        var transport = FindTransport()
                        ?? throw new SshConnectionFailedException("No SSH agent is running. Start Pageant or the OpenSSH Authentication Agent, or choose another login method.");

        IReadOnlyList<AgentKey> keys;
        try
        {
            keys = AgentProtocol.ParseIdentities(transport.Request(AgentProtocol.BuildRequestIdentities()));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or AgentException or System.Net.Sockets.SocketException)
        {
            throw new SshConnectionFailedException($"{transport.Name} did not respond: {ex.Message}", ex);
        }

        var selected = fingerprint is null ? keys : keys.Where(k => k.Fingerprint == fingerprint).ToList();
        if (selected.Count == 0)
            throw new SshConnectionFailedException(fingerprint is null
                ? $"{transport.Name} has no keys loaded."
                : $"The selected key is not loaded in {transport.Name}.");

        return selected.Select(k => (IPrivateKeySource)new AgentKeySource(transport, k)).ToArray();
    }

    private static IAgentTransport? FindTransport()
    {
        var socket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(socket))
        {
            if (socket.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
                return new NamedPipeAgentTransport(socket, "SSH agent");
            if (File.Exists(socket) || !OperatingSystem.IsWindows())
                return new UnixSocketAgentTransport(socket);
        }

        if (!OperatingSystem.IsWindows())
            return null;

        if (PipeExists(OpenSshPipe))
            return new NamedPipeAgentTransport(OpenSshPipe, "OpenSSH agent");

        return PageantAgentTransport.IsRunning ? new PageantAgentTransport() : null;
    }

    private static bool PipeExists(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", path[9..], PipeDirection.InOut);
            pipe.Connect(100);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One agent key, offered under each signature algorithm it supports.</summary>
    private sealed class AgentKeySource(IAgentTransport transport, AgentKey key) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = key.Algorithm == "ssh-rsa"
            ?
            [
                new AgentHostAlgorithm("rsa-sha2-512", transport, key, AgentProtocol.RsaSha2_512),
                new AgentHostAlgorithm("rsa-sha2-256", transport, key, AgentProtocol.RsaSha2_256),
                new AgentHostAlgorithm("ssh-rsa", transport, key, 0),
            ]
            : [new AgentHostAlgorithm(key.Algorithm, transport, key, 0)];
    }

    /// <summary>Signs SSH authentication data by asking the agent; the private key never leaves it.</summary>
    private sealed class AgentHostAlgorithm(string name, IAgentTransport transport, AgentKey key, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => key.PublicKeyBlob;

        public override byte[] Sign(byte[] data) =>
            AgentProtocol.ParseSignature(transport.Request(AgentProtocol.BuildSignRequest(key.PublicKeyBlob, data, flags)));

        public override bool VerifySignature(byte[] data, byte[] signature) => false;
    }
}
