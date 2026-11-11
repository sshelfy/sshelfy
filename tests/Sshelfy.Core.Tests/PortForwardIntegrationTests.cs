using System.Net;
using System.Net.Sockets;
using System.Text;
using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

/// <summary>Needs SSHELFY_TEST_SSH and a server with AllowTcpForwarding enabled.</summary>
[Collection(SshServerCollection.Name)]
public sealed class PortForwardIntegrationTests
{
    // The test server's own sshd, as seen from inside the server.
    private const int ServerSshPort = 2222;

    private static async Task<ISshConnection> ConnectAsync()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        var target = new ConnectionTarget
        {
            Address = endpoint[0],
            Port = int.Parse(endpoint[1]),
            Username = credentials[0],
            Password = credentials[1],
        };

        return await new SshNetConnectionFactory(new TrustAll()).ConnectAsync(target);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> ReadBannerAsync(NetworkStream stream)
    {
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    [SshFact]
    public async Task Local_forward_reaches_service_on_the_server()
    {
        await using var connection = await ConnectAsync();
        var port = FreePort();
        await using var forward = await connection.StartForwardAsync(new PortForwardRule
        {
            Kind = ForwardKind.Local,
            BindPort = port,
            DestinationHost = "127.0.0.1",
            DestinationPort = ServerSshPort,
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        Assert.StartsWith("SSH-2.0", await ReadBannerAsync(client.GetStream()));
        Assert.Equal(1, forward.ConnectionCount);
    }

    [SshFact]
    public async Task Busy_local_port_reports_a_readable_error()
    {
        await using var connection = await ConnectAsync();
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            var ex = await Assert.ThrowsAsync<PortForwardException>(() => connection.StartForwardAsync(new PortForwardRule
            {
                Kind = ForwardKind.Local,
                BindPort = port,
                DestinationHost = "127.0.0.1",
                DestinationPort = ServerSshPort,
            }));
            Assert.Contains($"Port {port} is already in use", ex.Message);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [SshFact]
    public async Task Dynamic_forward_works_as_socks5_proxy()
    {
        await using var connection = await ConnectAsync();
        var port = FreePort();
        await using var forward = await connection.StartForwardAsync(new PortForwardRule { Kind = ForwardKind.Dynamic, BindPort = port });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        // SOCKS5: no auth, then CONNECT 127.0.0.1:2222 (as seen from the server).
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply);
        Assert.Equal(new byte[] { 5, 0 }, reply);

        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, ServerSshPort >> 8, ServerSshPort & 0xFF });
        var connectReply = new byte[10];
        await stream.ReadExactlyAsync(connectReply);
        Assert.Equal(0, connectReply[1]);

        Assert.StartsWith("SSH-2.0", await ReadBannerAsync(stream));
    }

    [SshFact]
    public async Task Remote_forward_reaches_a_service_on_this_computer()
    {
        await using var connection = await ConnectAsync();

        var local = new TcpListener(IPAddress.Loopback, 0);
        local.Start();
        var localPort = ((IPEndPoint)local.LocalEndpoint).Port;
        var remotePort = 20000 + Random.Shared.Next(20000);
        try
        {
            await using var forward = await connection.StartForwardAsync(new PortForwardRule
            {
                Kind = ForwardKind.Remote,
                BindAddress = "127.0.0.1",
                BindPort = remotePort,
                DestinationHost = "127.0.0.1",
                DestinationPort = localPort,
            });

            // Ask the server to connect to its forwarded port.
            await using var shell = await connection.OpenShellAsync(80, 24);
            var accept = local.AcceptTcpClientAsync();
            shell.Write(Encoding.ASCII.GetBytes($"printf sshelfy-remote | nc 127.0.0.1 {remotePort}\r"));

            using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(15));
            var buffer = new byte[64];
            var read = await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("sshelfy-remote", Encoding.ASCII.GetString(buffer, 0, read));
        }
        finally
        {
            local.Stop();
        }
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
