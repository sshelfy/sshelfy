using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;
using Sshelfy.Infrastructure.Ssh.Agent;

namespace Sshelfy.Core.Tests;

/// <summary>
/// Agent authentication against an in-process agent served on a named pipe (SSH_AUTH_SOCK).
/// Runs in the shared server collection: sequential, which also protects the SSH_AUTH_SOCK change.
/// </summary>
[Collection(SshServerCollection.Name)]
public sealed class SshAgentIntegrationTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public async Task Status_lists_keys_from_the_agent()
    {
        using var agent = new FakeAgent(Fixture("test_ed25519"));
        using var _ = agent.Activate();

        var status = await new SshAgentClient().GetStatusAsync();

        Assert.True(status.IsAvailable, status.Error);
        var key = Assert.Single(status.Keys);
        Assert.Equal("ssh-ed25519", key.Algorithm);
        Assert.Equal("SHA256:cSUiYh3kThi6xbKeuWnRG4/P8W80a+pOH3J5jeKWa7A", key.Fingerprint);
        Assert.Equal("fake-agent-key", key.Comment);
    }

    [SshFact]
    public async Task Login_with_agent_key_succeeds()
    {
        using var agent = new FakeAgent(Fixture("test_ed25519"));
        using var _ = agent.Activate();

        await using var connection = await new SshNetConnectionFactory(new TrustAll()).ConnectAsync(Target());

        Assert.True(connection.IsConnected);
        Assert.True(agent.SignCount > 0);
    }

    [SshFact]
    public async Task Agent_without_an_authorized_key_fails_authentication()
    {
        using var agent = new FakeAgent(Fixture("test_rsa_pem"));
        using var _ = agent.Activate();

        await Assert.ThrowsAsync<SshAuthenticationFailedException>(() => new SshNetConnectionFactory(new TrustAll()).ConnectAsync(Target()));
    }

    /// <summary>Real Pageant with tests/Fixtures/test_ed25519 loaded (as .ppk); opt-in via SSHELFY_TEST_PAGEANT=1.</summary>
    [SshFact]
    public async Task Pageant_lists_keys_and_signs_logins()
    {
        if (Environment.GetEnvironmentVariable("SSHELFY_TEST_PAGEANT") != "1")
            return;

        using var _ = new EnvironmentScope("SSH_AUTH_SOCK", null);

        var status = await new SshAgentClient().GetStatusAsync();
        Assert.True(status.IsAvailable, status.Error);
        Assert.Equal("Pageant", status.Source);
        Assert.Contains(status.Keys, k => k.Fingerprint == "SHA256:cSUiYh3kThi6xbKeuWnRG4/P8W80a+pOH3J5jeKWa7A");

        await using var connection = await new SshNetConnectionFactory(new TrustAll()).ConnectAsync(Target());
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task Missing_agent_is_reported()
    {
        using var _ = new EnvironmentScope("SSH_AUTH_SOCK", @"\\.\pipe\sshelfy-no-such-agent-" + Guid.NewGuid().ToString("N"));

        var status = await new SshAgentClient().GetStatusAsync();

        Assert.False(status.IsAvailable);
        Assert.NotNull(status.Error);
    }

    private static ConnectionTarget Target()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var endpoint = spec[(at + 1)..].Split(':');
        return new ConnectionTarget
        {
            Address = endpoint[0],
            Port = int.Parse(endpoint[1]),
            Username = spec[..at].Split(':')[0],
            UseAgent = true,
        };
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    /// <summary>Minimal SSH agent: answers identity and sign requests with one key.</summary>
    private sealed class FakeAgent : IDisposable
    {
        private readonly string _pipeName = "sshelfy-test-agent-" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource _cts = new();
        private readonly PrivateKeyFile _keyFile;
        private readonly HostAlgorithm _algorithm;
        private readonly Task _loop;
        private int _signCount;

        public FakeAgent(string keyPath)
        {
            _keyFile = new PrivateKeyFile(keyPath);
            _algorithm = _keyFile.HostKeyAlgorithms.First();
            _loop = Task.Run(ServeAsync);
        }

        public int SignCount => Volatile.Read(ref _signCount);

        public IDisposable Activate() => new EnvironmentScope("SSH_AUTH_SOCK", @"\\.\pipe\" + _pipeName);

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(2000);
            }
            catch (AggregateException)
            {
            }

            _keyFile.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await server.WaitForConnectionAsync(_cts.Token);
                    var header = new byte[4];
                    await server.ReadExactlyAsync(header, _cts.Token);
                    var request = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                    await server.ReadExactlyAsync(request, _cts.Token);
                    await server.WriteAsync(AgentProtocol.Frame(Handle(request)), _cts.Token);
                    await server.FlushAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
            }
        }

        private byte[] Handle(byte[] request)
        {
            var w = new WireWriter();
            if (request[0] == AgentProtocol.RequestIdentities)
            {
                w.WriteByte(AgentProtocol.IdentitiesAnswer);
                w.WriteUInt32(1);
                w.WriteString(_algorithm.Data);
                w.WriteString(Encoding.UTF8.GetBytes("fake-agent-key"));
                return w.ToArray();
            }

            if (request[0] == AgentProtocol.SignRequest)
            {
                var r = new WireReader(request.AsSpan(1));
                r.ReadString(); // key blob
                var data = r.ReadString();
                Interlocked.Increment(ref _signCount);
                w.WriteByte(AgentProtocol.SignResponse);
                w.WriteString(_algorithm.Sign(data));
                return w.ToArray();
            }

            return [AgentProtocol.Failure];
        }
    }
}
