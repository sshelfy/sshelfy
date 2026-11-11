using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh;

/// <summary>
/// Connects SSH.NET clients, optionally through a chain of jump hosts (ProxyJump).
/// Each hop is reached through a local port forward on the previous hop; host keys are
/// always verified against the hop's real address, never the local forward.
/// </summary>
internal static class SshNetConnector
{
    /// <summary>Which login methods will be tried, for the log (never the secrets themselves).</summary>
    private static string Methods(ConnectionTarget target)
    {
        var methods = new List<string>();
        if (target.UseAgent)
            methods.Add("agent");
        if (!string.IsNullOrEmpty(target.PrivateKey))
            methods.Add("key");
        if (!string.IsNullOrEmpty(target.Password))
            methods.Add("password");
        return methods.Count == 0 ? "none" : string.Join(", ", methods);
    }

    public static async Task<(TClient Client, JumpChain? Chain)> ConnectAsync<TClient>(
        ConnectionTarget target,
        IHostKeyVerifier hostKeys,
        Func<ConnectionInfo, TClient> createClient,
        CancellationToken cancellationToken,
        ILogger? logger = null)
        where TClient : BaseClient
    {
        logger?.LogInformation("Connecting to {Host}:{Port} as {User}{Via} ({Methods})",
            target.Address, target.Port, target.Username,
            target.Via is null ? string.Empty : " via " + target.Via.Route,
            Methods(target));

        JumpChain? chain = null;
        var host = target.Address;
        var port = target.Port;
        if (target.Via is { } via)
        {
            chain = await JumpChain.OpenAsync(via, target, hostKeys, cancellationToken, logger).ConfigureAwait(false);
            host = IPAddress.Loopback.ToString();
            port = chain.LocalPort;
        }

        var connectionInfo = SshNetConnectionFactory.CreateConnectionInfo(target, host, port);
        var client = createClient(connectionInfo);
        var hostKeyRejected = false;

        // Raised on SSH.NET's handshake thread, which may block while the user decides.
        client.HostKeyReceived += (_, e) =>
        {
            var trusted = hostKeys
                .VerifyAsync(target.Address, target.Port, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256)
                .GetAwaiter()
                .GetResult();
            e.CanTrust = trusted;
            hostKeyRejected = !trusted;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Connected to {Host}:{Port} ({Server}, {Cipher})",
                target.Address, target.Port, client.ConnectionInfo.ServerVersion, client.ConnectionInfo.CurrentServerEncryption);
            return (client, chain);
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Connection to {Host}:{Port} failed: {Error} ({Type})", target.Address, target.Port, ex.Message, ex.GetType().Name);
            client.Dispose();
            var translated = SshNetConnectionFactory.Translate(ex, target, hostKeyRejected);
            if (chain is null)
                throw translated;

            // A failing hop tunnel surfaces as a vague handshake error; its own error event may
            // arrive a moment later, so give it a short chance before reporting.
            var tunnelError = chain.LastError;
            for (var i = 0; tunnelError is null && i < 10 && translated is not (SshAuthenticationFailedException or HostKeyRejectedException); i++)
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                tunnelError = chain.LastError;
            }

            chain.Dispose();
            if (translated is SshAuthenticationFailedException or HostKeyRejectedException or OperationCanceledException)
                throw translated;

            var reason = tunnelError ?? translated.Message;
            throw new SshConnectionFailedException($"Could not reach {target.Address}:{target.Port} through {target.Via!.DisplayName}: {reason}", ex);
        }
        finally
        {
            SshNetConnectionFactory.DisposeAuthMethods(connectionInfo);
        }
    }
}

/// <summary>An open jump host connection with a local forward to the next hop.</summary>
internal sealed class JumpChain : IDisposable
{
    private readonly SshClient _jump;
    private readonly ForwardedPortLocal _forward;
    private readonly JumpChain? _inner;
    private int _disposed;

    private JumpChain(SshClient jump, ForwardedPortLocal forward, JumpChain? inner, int localPort)
    {
        _jump = jump;
        _forward = forward;
        _inner = inner;
        LocalPort = localPort;
        _forward.Exception += (_, e) => LastError = SshNetPortForward.Describe(e.Exception, new PortForwardRule
        {
            Kind = ForwardKind.Local,
            BindPort = localPort,
        });
    }

    public int LocalPort { get; }

    /// <summary>Last error reported by the hop tunnel (e.g. destination unreachable, forwarding disabled).</summary>
    public string? LastError { get; private set; }

    public static async Task<JumpChain> OpenAsync(ConnectionTarget via, ConnectionTarget destination, IHostKeyVerifier hostKeys, CancellationToken cancellationToken, ILogger? logger = null)
    {
        SshClient jump;
        JumpChain? inner;
        try
        {
            (jump, inner) = await SshNetConnector.ConnectAsync(
                via,
                hostKeys,
                info => new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) },
                cancellationToken,
                logger).ConfigureAwait(false);
        }
        catch (SshConnectionFailedException ex) when (ex is not HostKeyRejectedException)
        {
            // Not an authentication failure of the destination: don't re-prompt for its password.
            throw new SshConnectionFailedException($"Jump host {via.DisplayName}: {ex.Message}", ex);
        }

        var localPort = FreeLoopbackPort();
        var forward = new ForwardedPortLocal(IPAddress.Loopback.ToString(), (uint)localPort, destination.Address, (uint)destination.Port);
        try
        {
            jump.AddForwardedPort(forward);
            forward.Start();
            return new JumpChain(jump, forward, inner, localPort);
        }
        catch (Exception ex) when (ex is SocketException or SshException or InvalidOperationException)
        {
            forward.Dispose();
            jump.Dispose();
            inner?.Dispose();
            throw new SshConnectionFailedException($"Jump host {via.DisplayName} could not open a tunnel: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_forward.IsStarted)
                _forward.Stop();
            _jump.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }

        _forward.Dispose();
        _jump.Dispose();
        _inner?.Dispose();
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
