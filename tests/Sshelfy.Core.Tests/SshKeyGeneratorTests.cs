using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class SshKeyGeneratorTests
{
    [Theory]
    [InlineData(SshKeyType.Ed25519, "ssh-ed25519")]
    [InlineData(SshKeyType.Rsa4096, "ssh-rsa")]
    public void Generated_keys_read_back_like_ssh_keygen_keys(SshKeyType type, string expectedType)
    {
        var key = new SshKeyGenerator().Generate(type, "user@laptop");
        var inspector = new SshKeyInspector();

        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", key.PrivateKey);
        Assert.StartsWith(expectedType + " ", key.PublicKey);
        Assert.EndsWith(" user@laptop", key.PublicKey);

        var info = inspector.Inspect(key.PrivateKey, null, out var problem);
        Assert.Equal(SshKeyProblem.None, problem);
        Assert.NotNull(info);

        // The public line derived from the private key is the same one the generator produced.
        Assert.Equal(key.PublicKey, inspector.PublicKey(key.PrivateKey, null, "user@laptop"));

        var blob = Convert.FromBase64String(key.PublicKey.Split(' ')[1]);
        Assert.Equal(SshKeyInspector.Fingerprint(blob), info.Fingerprint);
    }

    [Fact]
    public void Every_generated_key_is_different()
    {
        var generator = new SshKeyGenerator();
        Assert.NotEqual(generator.Generate(SshKeyType.Ed25519, "a").PublicKey, generator.Generate(SshKeyType.Ed25519, "a").PublicKey);
    }

    [Fact]
    public void Install_command_adds_the_line_once_with_strict_permissions()
    {
        var command = AuthorizedKeys.InstallCommand("ssh-ed25519 AAAAC3Nza me@pc\n");

        Assert.Contains("grep -qxF 'ssh-ed25519 AAAAC3Nza me@pc' ~/.ssh/authorized_keys", command);
        Assert.Contains("chmod 700 ~/.ssh", command);
        Assert.Contains("chmod 600 ~/.ssh/authorized_keys", command);
        Assert.Throws<ArgumentException>(() => AuthorizedKeys.InstallCommand("ssh-ed25519 AAAA 'evil"));
        Assert.Equal("me at pc", AuthorizedKeys.SafeComment("me 'at' \"pc\"\n").Replace("  ", " "));
    }
}

[Collection(SshServerCollection.Name)]
public sealed class SshKeyInstallIntegrationTests
{
    [SshFact]
    public async Task A_generated_key_installed_over_password_login_then_logs_in()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        var target = new ConnectionTarget { Address = endpoint[0], Port = int.Parse(endpoint[1]), Username = credentials[0], Password = credentials[1] };
        var factory = new SshNetConnectionFactory(new AlwaysTrust());
        var key = new SshKeyGenerator().Generate(SshKeyType.Ed25519, "sshelfy-test-" + Guid.NewGuid().ToString("N")[..6]);

        await using (var connection = await factory.ConnectAsync(target))
        {
            var install = await connection.RunCommandAsync(AuthorizedKeys.InstallCommand(key.PublicKey));
            Assert.Equal(0, install.ExitCode);

            // Running it again does not add a second copy.
            await connection.RunCommandAsync(AuthorizedKeys.InstallCommand(key.PublicKey));
            var count = await connection.RunCommandAsync($"grep -cxF '{key.PublicKey}' ~/.ssh/authorized_keys");
            Assert.Equal("1", count.Output.Trim());
        }

        try
        {
            await using var keyLogin = await factory.ConnectAsync(target with { Password = null, PrivateKey = key.PrivateKey });
            var whoami = await keyLogin.RunCommandAsync("whoami");
            Assert.Equal(credentials[0], whoami.Output.Trim());
        }
        finally
        {
            await using var cleanup = await factory.ConnectAsync(target);
            var comment = key.PublicKey.Split(' ')[2];
            await cleanup.RunCommandAsync($"sed -i '/{comment}/d' ~/.ssh/authorized_keys");
        }
    }

    private sealed class AlwaysTrust : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
