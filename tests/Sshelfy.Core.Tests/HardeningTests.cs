using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;
using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

/// <summary>Input from servers and files that must not reach the local machine unchanged.</summary>
public sealed class HardeningTests
{
    [Theory]
    [InlineData(@"..\..\AppData\Startup\x.bat", ".._.._AppData_Startup_x.bat")]
    [InlineData(@"C:\Windows\evil.dll", "C__Windows_evil.dll")]
    [InlineData("notes.txt:hidden", "notes.txt_hidden")]
    [InlineData("..", "_")]
    [InlineData("trailing. . ", "trailing")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("report<1>?.pdf", "report_1__.pdf")]
    [InlineData("zażółć.log", "zażółć.log")]
    public void Remote_names_become_safe_local_names(string remote, string expected) =>
        Assert.Equal(expected, LocalNames.Safe(remote));

    [Fact]
    public void Local_children_never_leave_their_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "sshelfy-root");

        var child = LocalNames.Child(root, @"..\..\escape.txt");

        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, child);
        Assert.Equal(".._.._escape.txt", Path.GetFileName(child));
    }

    [Theory]
    [InlineData("-oProxyCommand=calc", "deploy")]
    [InlineData("web.example.com", "-oProxyCommand=calc")]
    [InlineData("web example", "deploy")]
    [InlineData("web.example.com", "dep loy")]
    [InlineData("a@b", "deploy")]
    [InlineData("", "deploy")]
    public void Addresses_and_users_that_look_like_options_are_rejected(string address, string user)
    {
        Assert.NotNull(HostValidation.Problem(address, user));
        var host = new HostProfile { Address = address, Username = user };
        Assert.Throws<ArgumentException>(() => OpenSshCommandLine.Build(host, [], forwardAgent: true, forwardX11: false));
    }

    [Fact]
    public void A_bad_jump_host_is_rejected_too()
    {
        var host = new HostProfile { Address = "web", Username = "deploy" };
        Assert.Throws<ArgumentException>(() => OpenSshCommandLine.Build(host, [new HostProfile { Address = "-oX=y", Username = "ops" }], true, false));
        Assert.Null(HostValidation.Problem("10.0.0.5", "deploy"));
        Assert.Null(HostValidation.Problem("fe80::1", ""));
    }

    [Theory]
    [InlineData("localhost:0.0", true)]
    [InlineData("127.0.0.1:1", true)]
    [InlineData("evil\0PATH=x:0", false)]
    [InlineData("nonsense", false)]
    public void X11_display_values_are_checked(string display, bool valid) =>
        Assert.Equal(valid, OpenSshCommandLine.IsValidDisplay(display));

    [Fact]
    public void Huge_inline_images_are_refused()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x40, 0, 0, 0, 0x40, 0];

        Assert.Null(ImageHeader.ReadSize(png)); // 16384 × 16384 would decode to 1 GiB
    }
}
