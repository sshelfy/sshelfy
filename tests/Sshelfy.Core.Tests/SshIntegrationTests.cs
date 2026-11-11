using System.Text;
using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

/// <summary>Runs only when SSHELFY_TEST_SSH is set, e.g. "tester:sshelfy-test@127.0.0.1:2222".</summary>
public sealed class SshFactAttribute : FactAttribute
{
    public SshFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")))
            Skip = "Set SSHELFY_TEST_SSH=user:password@host:port to run SSH integration tests.";
    }
}

[Collection(SshServerCollection.Name)]
public sealed class SshIntegrationTests
{
    private static ConnectionTarget Target(bool useKey)
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');

        return new ConnectionTarget
        {
            Address = endpoint[0],
            Port = int.Parse(endpoint[1]),
            Username = credentials[0],
            Password = useKey ? null : credentials[1],
            PrivateKey = useKey ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test_ed25519")) : null,
        };
    }

    private static SshNetConnectionFactory Factory(bool trust = true) =>
        new(new KnownHostsVerifier(new MemoryKnownHosts(), new FixedPrompt(trust)));

    [SshFact]
    public async Task Password_login_opens_shell_and_echoes_command()
    {
        await using var connection = await Factory().ConnectAsync(Target(useKey: false));
        await using var shell = await connection.OpenShellAsync(80, 24);

        var output = new StringBuilder();
        var seen = new TaskCompletionSource();
        shell.DataReceived += data =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data));
                if (output.ToString().Contains("sshelfy-ok-42"))
                    seen.TrySetResult();
            }
        };

        shell.Write(Encoding.UTF8.GetBytes("echo sshelfy-ok-$((40+2))\r"));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [SshFact]
    public async Task Key_login_succeeds()
    {
        await using var connection = await Factory().ConnectAsync(Target(useKey: true));
        Assert.True(connection.IsConnected);
    }

    [SshFact]
    public async Task Wrong_password_is_reported_as_authentication_failure()
    {
        var target = Target(useKey: false) with { Password = "definitely-wrong" };
        await Assert.ThrowsAsync<SshAuthenticationFailedException>(() => Factory().ConnectAsync(target));
    }

    [SshFact]
    public async Task Rejected_host_key_aborts_connection()
    {
        await Assert.ThrowsAsync<HostKeyRejectedException>(() => Factory(trust: false).ConnectAsync(Target(useKey: false)));
    }

    private sealed class FixedPrompt(bool answer) : IHostKeyPrompt
    {
        public Task<bool> ConfirmAsync(HostKeyChallenge challenge) => Task.FromResult(answer);
    }

    private sealed class MemoryKnownHosts : IKnownHostsStore
    {
        private readonly List<KnownHost> _hosts = [];

        public Task<IReadOnlyList<KnownHost>> FindAsync(string host, int port, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnownHost>>(_hosts.Where(h => h.Host == host && h.Port == port).ToList());

        public Task AddAsync(KnownHost knownHost, CancellationToken cancellationToken = default)
        {
            _hosts.Add(knownHost);
            return Task.CompletedTask;
        }

        public Task ReplaceAllAsync(KnownHost knownHost, CancellationToken cancellationToken = default)
        {
            _hosts.RemoveAll(h => h.Host == knownHost.Host && h.Port == knownHost.Port);
            _hosts.Add(knownHost);
            return Task.CompletedTask;
        }
    }
}
