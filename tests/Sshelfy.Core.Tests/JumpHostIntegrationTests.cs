using System.Text;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

/// <summary>
/// Runs when SSHELFY_TEST_SSH (the jump host) and SSHELFY_TEST_JUMP (a server only reachable
/// from it, e.g. "inner:inner-test@inner:2222") are set.
/// </summary>
public sealed class JumpFactAttribute : FactAttribute
{
    public JumpFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSHELFY_TEST_JUMP")))
            Skip = "Set SSHELFY_TEST_SSH and SSHELFY_TEST_JUMP to run jump host tests.";
    }
}

[Collection(SshServerCollection.Name)]
public sealed class JumpHostIntegrationTests
{
    private static ConnectionTarget Parse(string variable, string name)
    {
        var spec = Environment.GetEnvironmentVariable(variable)!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        return new ConnectionTarget
        {
            Address = endpoint[0],
            Port = int.Parse(endpoint[1]),
            Username = credentials[0],
            Password = credentials[1],
            DisplayName = name,
        };
    }

    private static ConnectionTarget InnerViaBastion() =>
        Parse("SSHELFY_TEST_JUMP", "inner") with { Via = Parse("SSHELFY_TEST_SSH", "bastion") };

    [JumpFact]
    public async Task Shell_through_jump_host_reaches_inner_server()
    {
        var verifier = new RecordingVerifier();
        var target = InnerViaBastion();
        await using var connection = await new SshNetConnectionFactory(verifier).ConnectAsync(target);
        await using var shell = await connection.OpenShellAsync(80, 24);

        var output = new StringBuilder();
        var seen = new TaskCompletionSource();
        shell.DataReceived += data =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data));
                if (output.ToString().Contains("user-is-inner"))
                    seen.TrySetResult();
            }
        };
        shell.Write(Encoding.UTF8.GetBytes("echo user-is-$(whoami)\r"));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Keys are checked per hop, under each hop's real address.
        Assert.Contains((target.Via!.Address, target.Via.Port), verifier.Checked);
        Assert.Contains((target.Address, target.Port), verifier.Checked);
        Assert.DoesNotContain(verifier.Checked, c => c.Host == "127.0.0.1" && c.Port != target.Via.Port);
    }

    [JumpFact]
    public async Task Sftp_through_jump_host_lists_home()
    {
        await using var session = await new SshNetSftpSessionFactory(new RecordingVerifier()).ConnectAsync(InnerViaBastion());
        var entries = await session.ListAsync(session.HomeDirectory);
        Assert.NotNull(entries);
    }

    [JumpFact]
    public async Task Wrong_jump_host_password_names_the_jump_host_and_is_not_an_auth_failure_of_the_target()
    {
        var target = InnerViaBastion();
        target = target with { Via = target.Via! with { Password = "wrong" } };

        var ex = await Assert.ThrowsAsync<SshConnectionFailedException>(() => new SshNetConnectionFactory(new RecordingVerifier()).ConnectAsync(target));
        Assert.IsNotType<SshAuthenticationFailedException>(ex);
        Assert.StartsWith("Jump host bastion:", ex.Message);
    }

    [JumpFact]
    public async Task Unreachable_destination_reports_which_hop_failed()
    {
        var target = InnerViaBastion() with { Address = "no-such-host.invalid" };

        var ex = await Assert.ThrowsAsync<SshConnectionFailedException>(() => new SshNetConnectionFactory(new RecordingVerifier()).ConnectAsync(target));
        Assert.Contains("bastion", ex.Message);
    }

    private sealed class RecordingVerifier : IHostKeyVerifier
    {
        public List<(string Host, int Port)> Checked { get; } = [];

        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint)
        {
            lock (Checked)
                Checked.Add((host, port));
            return Task.FromResult(true);
        }
    }
}
