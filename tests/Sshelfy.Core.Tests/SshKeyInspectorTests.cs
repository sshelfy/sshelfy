using Sshelfy.Core.Security;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class SshKeyInspectorTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private readonly SshKeyInspector _inspector = new();

    [Fact]
    public void Reads_openssh_ed25519_key_and_matches_ssh_keygen_fingerprint()
    {
        var info = _inspector.Inspect(Fixture("test_ed25519"), null, out var problem);

        Assert.Equal(SshKeyProblem.None, problem);
        Assert.NotNull(info);
        Assert.Equal("ssh-ed25519", info.Algorithm);
        Assert.Equal("SHA256:cSUiYh3kThi6xbKeuWnRG4/P8W80a+pOH3J5jeKWa7A", info.Fingerprint);
    }

    [Fact]
    public void Reads_pem_rsa_key()
    {
        var info = _inspector.Inspect(Fixture("test_rsa_pem"), null, out var problem);

        Assert.Equal(SshKeyProblem.None, problem);
        Assert.Equal("SHA256:sKwiPd4QNNnpxX8vp0PhQKZG5VMRrJ0B48wSyrz6Gv4", info!.Fingerprint);
    }

    [Fact]
    public void Encrypted_key_reports_missing_and_wrong_passphrase()
    {
        var key = Fixture("test_ed25519_encrypted");

        Assert.Null(_inspector.Inspect(key, null, out var missing));
        Assert.Equal(SshKeyProblem.PassphraseRequired, missing);

        Assert.Null(_inspector.Inspect(key, "nope", out var wrong));
        Assert.Equal(SshKeyProblem.WrongPassphrase, wrong);

        Assert.NotNull(_inspector.Inspect(key, "test-passphrase", out var ok));
        Assert.Equal(SshKeyProblem.None, ok);
    }

    [Fact]
    public void Garbage_is_invalid_format()
    {
        Assert.Null(_inspector.Inspect("not a key", null, out var problem));
        Assert.Equal(SshKeyProblem.InvalidFormat, problem);
    }
}
