using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class PreConnectTests
{
    private static readonly PreConnectRunner Runner = new(NullLogger<PreConnectRunner>.Instance);

    [Fact]
    public void Parses_knock_sequences()
    {
        var knocks = PortKnock.ParseSequence("7000, 8000/udp 9000/TCP;10000", out var error);

        Assert.Null(error);
        Assert.Equal([new PortKnock(7000, false), new PortKnock(8000, true), new PortKnock(9000, false), new PortKnock(10000, false)], knocks);
        Assert.Equal("7000, 8000/udp, 9000, 10000", PortKnock.Format(knocks!));
        Assert.Empty(PortKnock.ParseSequence("  ", out _)!);
        Assert.Null(PortKnock.ParseSequence("7000, 70000", out error));
        Assert.Contains("70000", error);
        Assert.Null(PortKnock.ParseSequence("22/sctp", out _));
    }

    [Fact]
    public async Task The_command_sees_the_host_and_must_succeed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var output = Path.Combine(Path.GetTempPath(), $"sshelfy-pre-{Guid.NewGuid():N}.txt");
        try
        {
            var host = new HostProfile { Address = "db.example", Port = 2200, Username = "ops", PreConnectCommand = $"echo %SSHELFY_USER%@%SSHELFY_HOST%:%SSHELFY_PORT%> \"{output}\"" };
            await Runner.RunAsync(host, knock: false, CancellationToken.None);
            Assert.Equal("ops@db.example:2200", (await File.ReadAllTextAsync(output, CancellationToken.None)).Trim());

            var failing = host with { PreConnectCommand = "echo VPN is down 1>&2 & exit /b 3" };
            var error = await Assert.ThrowsAsync<SshConnectionFailedException>(() => Runner.RunAsync(failing, knock: false, CancellationToken.None));
            Assert.Equal("The command before connecting failed (exit code 3): VPN is down", error.Message);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task A_command_that_hangs_is_stopped()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var previous = PreConnectRunner.CommandTimeout;
        PreConnectRunner.CommandTimeout = TimeSpan.FromSeconds(1);
        try
        {
            var host = new HostProfile { Address = "h", Username = "u", PreConnectCommand = "ping -n 30 127.0.0.1 >nul" };
            var error = await Assert.ThrowsAsync<SshConnectionFailedException>(() => Runner.RunAsync(host, knock: false, CancellationToken.None));
            Assert.Contains("did not finish within 1 seconds", error.Message);
        }
        finally
        {
            PreConnectRunner.CommandTimeout = previous;
        }
    }

    [Fact]
    public async Task Knocks_tcp_and_udp_ports_in_order()
    {
        var token = CancellationToken.None;
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var third = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        third.Start();
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var ports = new[] { ((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.Client.LocalEndPoint!).Port, ((IPEndPoint)third.LocalEndpoint).Port };
        var order = new List<int>();
        var knocked = Task.WhenAll(
            first.AcceptTcpClientAsync(token).AsTask().ContinueWith(_ => { lock (order) order.Add(1); }, token),
            second.ReceiveAsync(token).AsTask().ContinueWith(_ => { lock (order) order.Add(2); }, token),
            third.AcceptTcpClientAsync(token).AsTask().ContinueWith(_ => { lock (order) order.Add(3); }, token));

        var host = new HostProfile
        {
            Address = "127.0.0.1",
            Username = "u",
            KnockSequence = [new(ports[0], false), new(ports[1], true), new(ports[2], false)],
            KnockDelayMilliseconds = 50,
        };
        await Runner.RunAsync(host, knock: true, token);
        await knocked.WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task Hosts_behind_a_jump_host_are_not_knocked()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var host = new HostProfile { Address = "127.0.0.1", Username = "u", KnockSequence = [new(((IPEndPoint)listener.LocalEndpoint).Port, false)] };

        await Runner.RunAsync(host, knock: false, CancellationToken.None);

        Assert.False(listener.Pending());
    }
}
