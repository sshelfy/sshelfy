using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Sshelfy.Core.Security;

namespace Sshelfy.Infrastructure.Ssh;

/// <summary>
/// New key pairs in OpenSSH's own format ("openssh-key-v1"), so the private key reads like one made by
/// ssh-keygen. Ed25519 comes from BouncyCastle (already used by SSH.NET), RSA from .NET.
/// </summary>
internal sealed class SshKeyGenerator : ISshKeyGenerator
{
    public GeneratedSshKey Generate(SshKeyType type, string comment)
    {
        comment = AuthorizedKeys.SafeComment(comment);
        return type switch
        {
            SshKeyType.Ed25519 => Ed25519(comment),
            _ => Rsa(comment),
        };
    }

    private static GeneratedSshKey Ed25519(string comment)
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var publicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        var seed = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();

        var blob = new SshWriter().String("ssh-ed25519").Bytes(publicKey).ToArray();
        var secret = new SshWriter()
            .String("ssh-ed25519")
            .Bytes(publicKey)
            .Bytes([.. seed, .. publicKey]) // OpenSSH keeps the 32-byte seed followed by the public key
            .String(comment);
        CryptographicOperations.ZeroMemory(seed);
        return new GeneratedSshKey(Envelope(blob, secret), Line("ssh-ed25519", blob, comment));
    }

    private static GeneratedSshKey Rsa(string comment)
    {
        using var rsa = RSA.Create(4096);
        var p = rsa.ExportParameters(includePrivateParameters: true);
        var blob = new SshWriter().String("ssh-rsa").MpInt(p.Exponent!).MpInt(p.Modulus!).ToArray();
        var secret = new SshWriter()
            .String("ssh-rsa")
            .MpInt(p.Modulus!)
            .MpInt(p.Exponent!)
            .MpInt(p.D!)
            .MpInt(p.InverseQ!)
            .MpInt(p.P!)
            .MpInt(p.Q!)
            .String(comment);
        return new GeneratedSshKey(Envelope(blob, secret), Line("ssh-rsa", blob, comment));
    }

    private static string Line(string type, byte[] blob, string comment) =>
        $"{type} {Convert.ToBase64String(blob)}{(comment.Length > 0 ? " " + comment : string.Empty)}";

    /// <summary>The unencrypted openssh-key-v1 container (PROTOCOL.key in OpenSSH).</summary>
    private static string Envelope(byte[] publicBlob, SshWriter secret)
    {
        var check = RandomNumberGenerator.GetBytes(4);
        var section = new SshWriter().Raw(check).Raw(check).Raw(secret.ToArray());
        for (byte pad = 1; section.Length % 8 != 0; pad++)
            section.Raw([pad]);

        var body = new SshWriter()
            .Raw("openssh-key-v1\0"u8.ToArray())
            .String("none")
            .String("none")
            .Bytes([])
            .UInt32(1)
            .Bytes(publicBlob)
            .Bytes(section.ToArray())
            .ToArray();

        var base64 = Convert.ToBase64String(body);
        var text = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (var i = 0; i < base64.Length; i += 70)
            text.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        text.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return text.ToString();
    }

    /// <summary>SSH wire encoding (RFC 4251): uint32, string, mpint.</summary>
    private sealed class SshWriter
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public SshWriter UInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            _stream.Write(buffer);
            return this;
        }

        public SshWriter Bytes(byte[] value) => UInt32((uint)value.Length).Raw(value);

        public SshWriter String(string value) => Bytes(Encoding.UTF8.GetBytes(value));

        /// <summary>Big-endian two's complement, with a leading zero when the top bit is set.</summary>
        public SshWriter MpInt(byte[] unsignedBigEndian)
        {
            var value = new BigInteger(unsignedBigEndian, isUnsigned: true, isBigEndian: true);
            return Bytes(value.IsZero ? [] : value.ToByteArray(isUnsigned: false, isBigEndian: true));
        }

        public SshWriter Raw(byte[] value)
        {
            _stream.Write(value);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}