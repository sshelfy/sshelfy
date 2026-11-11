using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class SshCertificateTests
{
    private static readonly string SshKeygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");

    [Fact]
    public async Task Reads_a_certificate_signed_by_ssh_keygen_and_matches_it_to_the_key()
    {
        if (!File.Exists(SshKeygen))
            return; // Windows without the OpenSSH client

        var key = new SshKeyGenerator().Generate(SshKeyType.Ed25519, "user");
        var certificate = await SignAsync(key.PublicKey, "deploy-2026", "deploy,ops");

        var parsed = SshCertificate.Parse(certificate);

        Assert.Equal("ssh-ed25519-cert-v01@openssh.com", parsed.Type);
        Assert.Equal(SshCertificateKind.User, parsed.Kind);
        Assert.Equal("deploy-2026", parsed.KeyId);
        Assert.Equal(["deploy", "ops"], parsed.Principals);
        Assert.NotNull(parsed.ValidBefore);
        Assert.False(parsed.IsExpired(DateTimeOffset.UtcNow));
        Assert.Equal(new SshKeyInspector().Inspect(key.PrivateKey, null, out _)!.Fingerprint, parsed.PublicKeyFingerprint);
        Assert.StartsWith("User certificate \"deploy-2026\" for deploy, ops · valid until ", parsed.Summary(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_catalog_only_attaches_a_certificate_to_its_own_key()
    {
        if (!File.Exists(SshKeygen))
            return;

        var generator = new SshKeyGenerator();
        var mine = generator.Generate(SshKeyType.Ed25519, "mine");
        var other = generator.Generate(SshKeyType.Ed25519, "other");
        var catalog = new HostCatalog(new InMemoryRepository(), new InMemoryVault(), new SshKeyInspector());
        var key = await catalog.ImportKeyAsync("mine", mine.PrivateKey, null);

        await Assert.ThrowsAsync<SshCertificateException>(() => catalog.AttachCertificateAsync(key.Id, "ssh-ed25519 AAAA not-a-cert"));
        var foreign = await SignAsync(other.PublicKey, "x", "x");
        await Assert.ThrowsAsync<SshCertificateException>(() => catalog.AttachCertificateAsync(key.Id, foreign));

        var certificate = await SignAsync(mine.PublicKey, "ok", "deploy");
        await catalog.AttachCertificateAsync(key.Id, certificate);
        var host = new HostProfile { Address = "h", Username = "deploy", AuthMethod = AuthMethod.PrivateKey, KeyId = key.Id };
        Assert.Equal(certificate.Trim(), (await catalog.BuildTargetAsync(host)).Certificate);

        await catalog.AttachCertificateAsync(key.Id, null);
        Assert.Null(catalog.FindKey(key.Id)!.Certificate);
    }

    /// <summary>
    /// Needs SSHELFY_TEST_SSH and SSHELFY_TEST_CA: the private key of a CA the server lists in TrustedUserCAKeys.
    /// The key is not in authorized_keys, so only the certificate gets it in.
    /// </summary>
    [Fact]
    public async Task Logs_in_with_a_certificate_the_server_trusts()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH");
        var caKey = Environment.GetEnvironmentVariable("SSHELFY_TEST_CA");
        if (string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(caKey) || !File.Exists(SshKeygen))
            return;

        var at = spec.LastIndexOf('@');
        var user = spec[..at].Split(':', 2)[0];
        var endpoint = spec[(at + 1)..].Split(':');
        var key = new SshKeyGenerator().Generate(SshKeyType.Ed25519, "cert-test");
        var certificate = await SignAsync(key.PublicKey, "cert-test", user, caKey);
        var target = new ConnectionTarget { Address = endpoint[0], Port = int.Parse(endpoint[1]), Username = user, PrivateKey = key.PrivateKey };
        var factory = new SshNetConnectionFactory(new AlwaysTrust());

        await Assert.ThrowsAsync<Sshelfy.Core.Ssh.SshAuthenticationFailedException>(() => factory.ConnectAsync(target));

        await using var connection = await factory.ConnectAsync(target with { Certificate = certificate });
        var whoami = await connection.RunCommandAsync("whoami");
        Assert.Equal(user, whoami.Output.Trim());
    }

    [Fact]
    public void A_security_key_certificate_matches_its_key()
    {
        var keyFields = Concat(Bytes(new byte[32]), Str("ssh:"));
        var publicBlob = Concat(Str("sk-ssh-ed25519@openssh.com"), keyFields);
        var serial = new byte[8];
        var validity = Concat(new byte[8], [0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]);
        var cert = Concat(Str("sk-ssh-ed25519-cert-v01@openssh.com"), Bytes(new byte[32]), keyFields, serial, UInt32(1),
            Str("yubikey"), Bytes(Str("deploy")), validity, Bytes([]), Bytes([]), Bytes([]));

        var parsed = SshCertificate.Parse("sk-ssh-ed25519-cert-v01@openssh.com " + Convert.ToBase64String(cert));

        Assert.Equal(OpenSshKeyFile.Fingerprint(publicBlob), parsed.PublicKeyFingerprint);
        Assert.Equal(["deploy"], parsed.Principals);
    }

    [Fact]
    public void Security_keys_are_recognised_from_the_file_alone()
    {
        var blob = Concat(Str("sk-ssh-ed25519@openssh.com"), Bytes(new byte[32]), Str("ssh:"));
        var privateKey = SecurityKeyFile(blob);
        var inspector = new SshKeyInspector();

        var info = inspector.Inspect(privateKey, null, out var problem);

        Assert.Equal(SshKeyProblem.None, problem);
        Assert.Equal("sk-ssh-ed25519@openssh.com", info!.Algorithm);
        Assert.Equal(OpenSshKeyFile.Fingerprint(blob), info.Fingerprint);
        Assert.True(new SshKeyProfile { Algorithm = info.Algorithm }.IsSecurityKey);
        Assert.Equal($"sk-ssh-ed25519@openssh.com {Convert.ToBase64String(blob)} me@laptop", inspector.PublicKey(privateKey, null, "me@laptop"));
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIA== plain key")]
    [InlineData("ssh-ed25519-cert-v01@openssh.com !!!")]
    [InlineData("ssh-ed25519-cert-v01@openssh.com AAAAIHNzaC1lZDI1NTE5LWNlcnQtdjAxQG9wZW5zc2guY29t")]
    public void Other_text_is_not_a_certificate(string line) =>
        Assert.Throws<SshCertificateException>(() => SshCertificate.Parse(line));

    /// <summary>Signs a public key with <paramref name="caKey"/>, or a throwaway CA, using the system ssh-keygen.</summary>
    private static async Task<string> SignAsync(string publicKey, string keyId, string principals, string? caKey = null)
    {
        var folder = Directory.CreateTempSubdirectory("sshelfy-cert-");
        try
        {
            var ca = caKey ?? Path.Combine(folder.FullName, "ca");
            var user = Path.Combine(folder.FullName, "user.pub");
            await File.WriteAllTextAsync(user, publicKey + "\n");
            if (caKey is null)
                await RunAsync("-q", "-t", "ed25519", "-N", "", "-C", "ca", "-f", ca);
            await RunAsync("-q", "-s", ca, "-I", keyId, "-n", principals, "-V", "+52w", user);
            return await File.ReadAllTextAsync(Path.Combine(folder.FullName, "user-cert.pub"));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static async Task RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(SshKeygen) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, "ssh-keygen failed: " + error);
    }

    private static string SecurityKeyFile(byte[] publicBlob)
    {
        var body = Concat("openssh-key-v1\0"u8.ToArray(), Str("none"), Str("none"), Bytes([]), UInt32(1), Bytes(publicBlob), Bytes(new byte[64]));
        var base64 = Convert.ToBase64String(body);
        var lines = Enumerable.Range(0, (base64.Length + 69) / 70).Select(i => base64.Substring(i * 70, Math.Min(70, base64.Length - i * 70)));
        return $"-----BEGIN OPENSSH PRIVATE KEY-----\n{string.Join('\n', lines)}\n-----END OPENSSH PRIVATE KEY-----\n";
    }

    private static byte[] Str(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    private static byte[] Bytes(byte[] value) => Concat(UInt32((uint)value.Length), value);

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private sealed class AlwaysTrust : Sshelfy.Core.Ssh.IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
