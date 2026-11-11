using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh;

/// <summary>Wraps an SSH.NET forwarded port started on a client.</summary>
internal sealed class SshNetPortForward : IPortForward
{
    private readonly SshClient _client;
    private readonly ForwardedPort _port;
    private int _connections;
    private int _disposed;

    private SshNetPortForward(SshClient client, ForwardedPort port, PortForwardRule rule)
    {
        _client = client;
        _port = port;
        Rule = rule;
        _port.RequestReceived += (_, _) =>
        {
            Interlocked.Increment(ref _connections);
            ConnectionOpened?.Invoke(this, EventArgs.Empty);
        };
        _port.Exception += (_, e) => Failed?.Invoke(this, Describe(e.Exception, rule));
    }

    public PortForwardRule Rule { get; }

    public int ConnectionCount => Volatile.Read(ref _connections);

    public event EventHandler? ConnectionOpened;

    public event EventHandler<string>? Failed;

    public static IPortForward Start(SshClient client, PortForwardRule rule)
    {
        ForwardedPort port = rule.Kind switch
        {
            ForwardKind.Local => new ForwardedPortLocal(rule.BindAddress, (uint)rule.BindPort, rule.DestinationHost, (uint)rule.DestinationPort),
            ForwardKind.Remote => new ForwardedPortRemote(rule.BindAddress, (uint)rule.BindPort, rule.DestinationHost, (uint)rule.DestinationPort),
            _ => new ForwardedPortDynamic(rule.BindAddress, (uint)rule.BindPort),
        };

        var forward = new SshNetPortForward(client, port, rule);
        try
        {
            client.AddForwardedPort(port);
            port.Start();
            return forward;
        }
        catch (Exception ex) when (ex is SocketException or SshException or InvalidOperationException or ArgumentException)
        {
            forward.Release();
            throw new PortForwardException(Describe(ex, rule), ex);
        }
    }

    public ValueTask DisposeAsync() => new(Task.Run(Release));

    private void Release()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_port.IsStarted)
                _port.Stop();
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection is already gone; nothing left to stop.
        }

        try
        {
            _client.RemoveForwardedPort(_port);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
        }

        _port.Dispose();
    }

    internal static string Describe(Exception ex, PortForwardRule rule)
    {
        var listener = rule.Kind == ForwardKind.Remote ? "the server" : "this computer";
        return ex switch
        {
            SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
                $"Port {rule.BindPort} is already in use on {listener}.",
            SocketException { SocketErrorCode: SocketError.AccessDenied } =>
                $"Not allowed to listen on port {rule.BindPort} on {listener}.",
            SocketException { SocketErrorCode: SocketError.AddressNotAvailable } =>
                $"The address {rule.BindAddress} is not available on {listener}.",
            SocketException socket => socket.Message,
            SshException when rule.Kind == ForwardKind.Remote =>
                $"The server refused to listen on {rule.BindAddress}:{rule.BindPort}. Remote forwarding may be disabled (AllowTcpForwarding).",
            SshException ssh when ssh.Message.Contains("prohibited", StringComparison.OrdinalIgnoreCase) =>
                "The server does not allow port forwarding (AllowTcpForwarding).",
            _ => ex.Message,
        };
    }
}
