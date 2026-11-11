using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh;

internal sealed class SshNetConnectionFactory : ISshConnectionFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly IHostKeyVerifier _hostKeys;
    private readonly ILogger? _logger;

    public SshNetConnectionFactory(IHostKeyVerifier hostKeys, ILogger<SshNetConnectionFactory>? logger = null)
    {
        _hostKeys = hostKeys;
        _logger = logger;
    }

    public async Task<ISshConnection> ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default)
    {
        var (client, chain) = await SshNetConnector.ConnectAsync(
            target,
            _hostKeys,
            info => new SshClient(info) { KeepAliveInterval = KeepAlive },
            cancellationToken,
            _logger).ConfigureAwait(false);
        return new SshNetConnection(client, chain, _logger);
    }

    /// <summary>Builds connection settings for the client; also used by the SFTP client factory.</summary>
    internal static ConnectionInfo CreateConnectionInfo(ConnectionTarget target) =>
        CreateConnectionInfo(target, target.Address, target.Port);

    /// <param name="host">Address to open the TCP connection to (a local forward when jumping).</param>
    internal static ConnectionInfo CreateConnectionInfo(ConnectionTarget target, string host, int port)
    {
        var methods = new List<AuthenticationMethod>();

        if (target.UseAgent)
            methods.Add(new PrivateKeyAuthenticationMethod(target.Username, Agent.SshAgentClient.CreateKeySources(target.AgentKeyFingerprint)));

        if (!string.IsNullOrEmpty(target.PrivateKey))
        {
            if (Sshelfy.Core.Security.OpenSshKeyFile.ReadPublicKey(target.PrivateKey) is { } publicKey && Sshelfy.Core.Security.OpenSshKeyFile.IsSecurityKey(publicKey.Type))
                throw new SshAuthenticationFailedException("Security keys (FIDO2) work in terminal sessions, which run in the Windows OpenSSH client. File browsing cannot use them yet.");

            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(target.PrivateKey.Trim() + "\n"));
            using var certificateStream = string.IsNullOrEmpty(target.Certificate) ? null : new MemoryStream(Encoding.UTF8.GetBytes(target.Certificate.Trim() + "\n"));
            var keyFile = certificateStream is not null ? new PrivateKeyFile(keyStream, string.IsNullOrEmpty(target.Passphrase) ? null : target.Passphrase, certificateStream)
                : string.IsNullOrEmpty(target.Passphrase) ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, target.Passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(target.Username, keyFile));
        }

        if (!string.IsNullOrEmpty(target.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(target.Username, target.Password));

            // Many servers (PAM) only offer keyboard-interactive; answer password prompts with the same secret.
            var interactive = new KeyboardInteractiveAuthenticationMethod(target.Username);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (!prompt.IsEchoed)
                        prompt.Response = target.Password;
                }
            };
            methods.Add(interactive);
        }

        if (methods.Count == 0)
            methods.Add(new NoneAuthenticationMethod(target.Username));

        return new ConnectionInfo(host, port, target.Username, methods.ToArray())
        {
            Timeout = ConnectTimeout,
            Encoding = Encoding.UTF8,
        };
    }

    internal static void DisposeAuthMethods(ConnectionInfo info)
    {
        foreach (var method in info.AuthenticationMethods)
            (method as IDisposable)?.Dispose();
    }

    internal static Exception Translate(Exception ex, ConnectionTarget target, bool hostKeyRejected) => ex switch
    {
        _ when hostKeyRejected => new HostKeyRejectedException(),
        OperationCanceledException => ex,
        SshAuthenticationException => new SshAuthenticationFailedException(
            $"Authentication failed for {target.Username}@{target.Address}. Check the username, password or key.", ex),
        SshOperationTimeoutException => new SshConnectionFailedException($"Timed out connecting to {target.Address}:{target.Port}.", ex),
        SocketException socket => new SshConnectionFailedException(socket.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData => $"Host \"{target.Address}\" could not be resolved.",
            SocketError.ConnectionRefused => $"Connection refused by {target.Address}:{target.Port}.",
            SocketError.TimedOut => $"Timed out connecting to {target.Address}:{target.Port}.",
            _ => $"Could not reach {target.Address}:{target.Port} ({socket.Message}).",
        }, ex),
        SshConnectionException => new SshConnectionFailedException($"The SSH handshake failed: {ex.Message}", ex),
        SshException => new SshConnectionFailedException(ex.Message, ex),
        _ => new SshConnectionFailedException(ex.Message, ex),
    };
}
