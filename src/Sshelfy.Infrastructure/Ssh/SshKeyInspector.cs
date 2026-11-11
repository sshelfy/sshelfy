using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sshelfy.Core.Security;

namespace Sshelfy.Infrastructure.Ssh;

internal sealed class SshKeyInspector : ISshKeyInspector
{
    public SshKeyInfo? Inspect(string privateKey, string? passphrase, out SshKeyProblem problem)
    {
        problem = SshKeyProblem.None;

        // FIDO2 keys only hold a handle for the authenticator; SSH.NET cannot load them, the public part is enough.
        if (OpenSshKeyFile.ReadPublicKey(privateKey) is { } security && OpenSshKeyFile.IsSecurityKey(security.Type))
            return new SshKeyInfo(security.Type, Fingerprint(security.Blob));

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey.Trim() + "\n"));
            using var keyFile = new PrivateKeyFile(stream, passphrase);

            var algorithm = keyFile.HostKeyAlgorithms.First();
            return new SshKeyInfo(keyFile.Key.ToString() ?? algorithm.Name, Fingerprint(algorithm.Data));
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            problem = SshKeyProblem.PassphraseRequired;
        }
        catch (Exception ex) when (ex is SshException or CryptographicException && passphrase is not null)
        {
            problem = SshKeyProblem.WrongPassphrase;
        }
        catch (Exception ex) when (ex is SshException or CryptographicException or FormatException or ArgumentException or InvalidOperationException)
        {
            problem = SshKeyProblem.InvalidFormat;
        }

        return null;
    }

    public string? PublicKey(string privateKey, string? passphrase, string comment)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey.Trim() + "\n"));
            using var keyFile = new PrivateKeyFile(stream, passphrase);
            var blob = keyFile.HostKeyAlgorithms.First().Data;

            // The blob starts with the key type ("ssh-rsa" even when signing uses rsa-sha2-512).
            var length = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
            var type = Encoding.ASCII.GetString(blob, 4, length);
            var safe = AuthorizedKeys.SafeComment(comment);
            return $"{type} {Convert.ToBase64String(blob)}{(safe.Length > 0 ? " " + safe : string.Empty)}";
        }
        catch (Exception ex) when (ex is SshException or CryptographicException or FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            // Keys SSH.NET cannot load (security keys) still carry their public half in the file.
            if (OpenSshKeyFile.ReadPublicKey(privateKey) is not { } key)
                return null;
            var safe = AuthorizedKeys.SafeComment(comment);
            return $"{key.Type} {Convert.ToBase64String(key.Blob)}{(safe.Length > 0 ? " " + safe : string.Empty)}";
        }
    }

    /// <summary>OpenSSH "SHA256:" fingerprint of a public key blob.</summary>
    internal static string Fingerprint(byte[] publicKeyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=');
}
