using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sshelfy.Core.Security;

public sealed class SshCertificateException(string message) : Exception(message);

public enum SshCertificateKind
{
    User = 1,
    Host = 2,
}

/// <summary>An OpenSSH certificate (the "-cert.pub" line ssh-keygen -s writes).</summary>
/// <param name="PublicKeyFingerprint">Fingerprint of the key the certificate is for, to match it with a private key.</param>
public sealed record SshCertificate(
    string Type,
    SshCertificateKind Kind,
    string KeyId,
    IReadOnlyList<string> Principals,
    DateTimeOffset? ValidAfter,
    DateTimeOffset? ValidBefore,
    string PublicKeyFingerprint)
{
    /// <summary>Fields of the certified public key, after the nonce, per certificate type (rsa: e and n, ecdsa: curve and point…).</summary>
    private static readonly Dictionary<string, int> KeyFieldCounts = new(StringComparer.Ordinal)
    {
        ["ssh-ed25519-cert-v01@openssh.com"] = 1,
        ["ssh-rsa-cert-v01@openssh.com"] = 2,
        ["ecdsa-sha2-nistp256-cert-v01@openssh.com"] = 2,
        ["ecdsa-sha2-nistp384-cert-v01@openssh.com"] = 2,
        ["ecdsa-sha2-nistp521-cert-v01@openssh.com"] = 2,
        ["sk-ssh-ed25519-cert-v01@openssh.com"] = 2,
        ["sk-ecdsa-sha2-nistp256-cert-v01@openssh.com"] = 3,
    };

    public bool IsExpired(DateTimeOffset now) => ValidBefore is { } before && now >= before;

    /// <summary>"User certificate "deploy-2026" for deploy, ops · valid until 2026-11-30".</summary>
    public string Summary(DateTimeOffset now)
    {
        var principals = Principals.Count == 0 ? "any user" : string.Join(", ", Principals);
        var validity = ValidBefore is not { } before ? "no expiry"
            : IsExpired(now) ? $"expired {before.ToLocalTime():yyyy-MM-dd}"
            : $"valid until {before.ToLocalTime():yyyy-MM-dd HH:mm}";
        return $"{Kind} certificate \"{KeyId}\" for {principals} · {validity}";
    }

    /// <exception cref="SshCertificateException">Not an OpenSSH certificate line.</exception>
    public static SshCertificate Parse(string line)
    {
        var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !KeyFieldCounts.TryGetValue(parts[0], out var keyFields))
            throw new SshCertificateException("This is not an OpenSSH certificate. Pick the \"-cert.pub\" file that ssh-keygen -s created.");

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new SshCertificateException("The certificate is damaged (invalid base64).");
        }

        try
        {
            var reader = new SshReader(blob);
            var type = reader.String();
            if (type != parts[0])
                throw new SshCertificateException("The certificate type does not match its contents.");
            reader.Bytes(); // nonce

            // The certified key is the base type followed by its fields, as in a plain public key blob.
            var keyStart = reader.Position;
            for (var i = 0; i < keyFields; i++)
                reader.Bytes();
            // "ssh-ed25519-cert-v01@openssh.com" → "ssh-ed25519", but the security-key types keep their
            // domain: "sk-ssh-ed25519-cert-v01@openssh.com" → "sk-ssh-ed25519@openssh.com".
            var baseType = type.Replace("-cert-v01@openssh.com", string.Empty, StringComparison.Ordinal);
            if (baseType.StartsWith("sk-", StringComparison.Ordinal))
                baseType += "@openssh.com";
            var publicKey = SshReader.EncodeString(baseType).Concat(blob[keyStart..reader.Position]).ToArray();

            reader.UInt64(); // serial
            var kind = (SshCertificateKind)reader.UInt32();
            var keyId = reader.String();
            var principals = new List<string>();
            var packed = new SshReader(reader.Bytes());
            while (!packed.AtEnd)
                principals.Add(packed.String());
            var after = reader.UInt64();
            var before = reader.UInt64();

            return new SshCertificate(type, kind, keyId, principals,
                after == 0 ? null : DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(after, 253402300799)),
                before == ulong.MaxValue ? null : DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(before, 253402300799)),
                "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('='));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            throw new SshCertificateException("The certificate is damaged (truncated).");
        }
    }
}

/// <summary>FIDO2 security keys (ed25519-sk, ecdsa-sk) and other OpenSSH private keys SSH.NET cannot load.</summary>
public static class OpenSshKeyFile
{
    /// <summary>Key types that need a hardware authenticator for every signature.</summary>
    public static bool IsSecurityKey(string algorithm) => algorithm.StartsWith("sk-", StringComparison.Ordinal);

    /// <summary>
    /// The public key of an "OPENSSH PRIVATE KEY" file (stored unencrypted even when the private part has a passphrase).
    /// </summary>
    public static (string Type, byte[] Blob)? ReadPublicKey(string privateKey)
    {
        const string begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
        const string end = "-----END OPENSSH PRIVATE KEY-----";
        var start = privateKey.IndexOf(begin, StringComparison.Ordinal);
        var stop = privateKey.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < start)
            return null;

        try
        {
            var body = Convert.FromBase64String(string.Concat(privateKey[(start + begin.Length)..stop].Where(c => !char.IsWhiteSpace(c))));
            var magic = "openssh-key-v1\0"u8;
            if (!body.AsSpan().StartsWith(magic))
                return null;

            var reader = new SshReader(body[magic.Length..]);
            reader.String(); // cipher
            reader.String(); // kdf
            reader.Bytes(); // kdf options
            if (reader.UInt32() < 1)
                return null;
            var blob = reader.Bytes();
            return (new SshReader(blob).String(), blob);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    public static string Fingerprint(byte[] publicKeyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=');
}

/// <summary>Reads SSH wire encoding (RFC 4251): big-endian integers and length-prefixed strings.</summary>
internal sealed class SshReader(byte[] data)
{
    public int Position { get; private set; }

    public bool AtEnd => Position >= data.Length;

    public uint UInt32()
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public ulong UInt64()
    {
        var value = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(Position, 8));
        Position += 8;
        return value;
    }

    public byte[] Bytes()
    {
        var length = checked((int)UInt32());
        var value = data.AsSpan(Position, length).ToArray();
        Position += length;
        return value;
    }

    public string String() => Encoding.UTF8.GetString(Bytes());

    public static byte[] EncodeString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)bytes.Length);
        bytes.CopyTo(result, 4);
        return result;
    }
}
