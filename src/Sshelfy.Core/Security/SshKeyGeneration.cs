namespace Sshelfy.Core.Security;

public enum SshKeyType
{
    Ed25519,
    Rsa4096,
}

/// <param name="PrivateKey">OpenSSH private key ("-----BEGIN OPENSSH PRIVATE KEY-----"), unencrypted: it is stored in the vault.</param>
/// <param name="PublicKey">One authorized_keys line: "ssh-ed25519 AAAA… comment".</param>
public sealed record GeneratedSshKey(string PrivateKey, string PublicKey);

public interface ISshKeyGenerator
{
    GeneratedSshKey Generate(SshKeyType type, string comment);
}

public static class AuthorizedKeys
{
    /// <summary>
    /// A POSIX shell command that adds <paramref name="publicKey"/> to ~/.ssh/authorized_keys once, creating the
    /// folder and file with the permissions sshd requires.
    /// </summary>
    public static string InstallCommand(string publicKey)
    {
        var line = string.Join(' ', publicKey.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (line.Length == 0 || line.Contains('\''))
            throw new ArgumentException("The public key line is empty or contains a quote.", nameof(publicKey));

        return "umask 077 && mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && " +
               $"(grep -qxF '{line}' ~/.ssh/authorized_keys || printf '%s\\n' '{line}' >> ~/.ssh/authorized_keys) && " +
               "chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";
    }

    /// <summary>A comment safe to put on an authorized_keys line (no quotes or line breaks).</summary>
    public static string SafeComment(string comment) =>
        new string(comment.Where(c => c is not ('\'' or '"' or '\r' or '\n' or '\\')).ToArray()).Trim();
}