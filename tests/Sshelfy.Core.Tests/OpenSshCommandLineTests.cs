using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class OpenSshCommandLineTests
{
    [Fact]
    public void Plain_host_with_agent_forwarding()
    {
        var host = new HostProfile { Address = "web.example.com", Username = "deploy" };

        Assert.Equal("-o ServerAliveInterval=30 -A -l deploy -- web.example.com", OpenSshCommandLine.Build(host, [], forwardAgent: true, forwardX11: false));
    }

    [Fact]
    public void Port_jump_chain_x11_and_tunnels()
    {
        var host = new HostProfile
        {
            Address = "10.0.0.5",
            Port = 2222,
            Username = "app",
            PortForwards =
            [
                new PortForwardRule { Kind = ForwardKind.Local, BindPort = 5432, DestinationHost = "db", DestinationPort = 5432 },
                new PortForwardRule { Kind = ForwardKind.Remote, BindPort = 8080, DestinationHost = "localhost", DestinationPort = 3000 },
                new PortForwardRule { Kind = ForwardKind.Dynamic, BindPort = 1080 },
                new PortForwardRule { Kind = ForwardKind.Local, BindPort = 9999, DestinationPort = 1, AutoStart = false },
            ],
        };
        HostProfile[] jumps =
        [
            new() { Address = "bastion.example.com", Username = "ops" },
            new() { Address = "inner", Port = 2200, Username = "ops" },
        ];

        var line = OpenSshCommandLine.Build(host, jumps, forwardAgent: true, forwardX11: true);

        Assert.Equal(
            "-o ServerAliveInterval=30 -A -Y -p 2222 -J ops@bastion.example.com,ops@inner:2200 " +
            "-L 127.0.0.1:5432:db:5432 -R 127.0.0.1:8080:localhost:3000 -D 127.0.0.1:1080 -l app -- 10.0.0.5",
            line);
    }

    [Theory]
    [InlineData("localhost:0.0", "localhost", 6000)]
    [InlineData("127.0.0.1:1", "127.0.0.1", 6001)]
    [InlineData(":0", "localhost", 6000)]
    public void X11_display_maps_to_a_tcp_port(string display, string host, int port) =>
        Assert.Equal((host, port), OpenSshCommandLine.X11Endpoint(display));

    [Theory]
    [InlineData("nonsense")]
    [InlineData("host:x")]
    public void Unusable_x11_displays_have_no_endpoint(string display) => Assert.Null(OpenSshCommandLine.X11Endpoint(display));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void Quotes_like_command_line_to_argv(string argument, string expected) =>
        Assert.Equal(expected, OpenSshCommandLine.Quote(argument));
}
