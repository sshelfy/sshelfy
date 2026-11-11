using Sshelfy.Core.Hosts;

namespace Sshelfy.Core.Tests;

public sealed class QuickConnectParserTests
{
    [Theory]
    [InlineData("root@example.com", "root", "example.com", 22)]
    [InlineData("deploy@10.0.0.5:2222", "deploy", "10.0.0.5", 2222)]
    [InlineData("ssh admin@host -p 2200", "admin", "host", 2200)]
    [InlineData("  me@[::1]:2022 ", "me", "::1", 2022)]
    [InlineData("user@name@host", "user@name", "host", 22)]
    public void Parses_valid_input(string input, string user, string address, int port)
    {
        Assert.True(QuickConnectParser.TryParse(input, out var u, out var a, out var p));
        Assert.Equal(user, u);
        Assert.Equal(address, a);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("@host")]
    [InlineData("user@")]
    [InlineData("user@host:notaport")]
    [InlineData("user@host:70000")]
    public void Rejects_invalid_input(string input)
    {
        Assert.False(QuickConnectParser.TryParse(input, out _, out _, out _));
    }
}
