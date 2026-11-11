namespace Sshelfy.Core.Hosts;

public enum AuthMethod
{
    /// <summary>Password stored in the vault, or asked for on every connect.</summary>
    Password,

    /// <summary>Private key stored in the vault (see <see cref="SshKeyProfile"/>).</summary>
    PrivateKey,

    /// <summary>Keys held by the user's SSH agent (OpenSSH agent, Pageant).</summary>
    Agent,
}

/// <summary>
/// A saved connection. Contains no secrets: passwords and keys live in the vault,
/// keyed by <see cref="Id"/> (see <see cref="Security.SecretKeys"/>).
/// </summary>
public sealed record HostProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string Username { get; init; } = string.Empty;

    public Guid? GroupId { get; init; }

    public AuthMethod AuthMethod { get; init; } = AuthMethod.Password;

    /// <summary>Key used when <see cref="AuthMethod"/> is <see cref="AuthMethod.PrivateKey"/>.</summary>
    public Guid? KeyId { get; init; }

    public string? Notes { get; init; }

    /// <summary>With <see cref="AuthMethod.Agent"/>: fingerprint of the agent key to use, or null for any.</summary>
    public string? AgentKeyFingerprint { get; init; }

    /// <summary>Saved host to connect through (ProxyJump); null for a direct connection.</summary>
    public Guid? JumpHostId { get; init; }

    /// <summary>Record every terminal session to this host (see session logging settings).</summary>
    public bool AlwaysRecordSession { get; init; }

    /// <summary>Tunnels opened with this host's sessions.</summary>
    public IReadOnlyList<Ssh.PortForwardRule> PortForwards { get; init; } = [];

    /// <summary>Forward the local SSH agent. SSH.NET cannot do this, so the session runs in the system OpenSSH client (ssh.exe).</summary>
    public bool ForwardAgent { get; init; }

    /// <summary>Show remote X11 programs on a local X server (ssh -Y); also runs in the system OpenSSH client.</summary>
    public bool ForwardX11 { get; init; }

    /// <summary>Typed into the shell after login, e.g. "cd /var/www &amp;&amp; tmux attach".</summary>
    public string? StartupCommand { get; init; }

    /// <summary>Variables exported in terminal sessions before the startup command.</summary>
    public IReadOnlyList<EnvironmentVariable> Environment { get; init; } = [];

    /// <summary>Folder the file browser opens first; the home folder when empty.</summary>
    public string? SftpStartPath { get; init; }

    /// <summary>Local command run before connecting (e.g. bring up a VPN); a non-zero exit code stops the connection.</summary>
    public string? PreConnectCommand { get; init; }

    /// <summary>Ports to knock, in order, before connecting.</summary>
    public IReadOnlyList<PortKnock> KnockSequence { get; init; } = [];

    /// <summary>Pause between knocks and before connecting.</summary>
    public int KnockDelayMilliseconds { get; init; } = 200;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastConnectedAt { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    public string Endpoint => Port == 22 ? $"{Username}@{Address}" : $"{Username}@{Address}:{Port}";
}

/// <summary>A folder in the host tree. Groups can nest via <see cref="ParentId"/>.</summary>
public sealed record HostGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public Guid? ParentId { get; init; }

    public bool IsExpanded { get; init; } = true;
}

/// <summary>
/// Metadata of an imported private key. The key material itself is encrypted in the vault,
/// so one key can be shared by many hosts.
/// </summary>
public sealed record SshKeyProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    /// <summary>Key algorithm, e.g. "ssh-ed25519".</summary>
    public string Algorithm { get; init; } = string.Empty;

    /// <summary>OpenSSH style fingerprint, e.g. "SHA256:abc…".</summary>
    public string Fingerprint { get; init; } = string.Empty;

    public bool HasPassphrase { get; init; }

    /// <summary>An OpenSSH certificate for this key ("-cert.pub" line), presented when logging in.</summary>
    public string? Certificate { get; init; }

    /// <summary>A FIDO2 key (ed25519-sk, ecdsa-sk): sessions run in the system OpenSSH client, which talks to the authenticator.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSecurityKey => Security.OpenSshKeyFile.IsSecurityKey(Algorithm);

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Everything the host repository persists, as one document.</summary>
public sealed record HostCatalogData
{
    public int Version { get; init; } = 1;

    public List<HostGroup> Groups { get; init; } = [];

    public List<HostProfile> Hosts { get; init; } = [];

    public List<SshKeyProfile> Keys { get; init; } = [];
}

/// <summary>
/// Fully resolved connection parameters, including secrets. Short-lived: build it right before
/// connecting and do not keep it around.
/// </summary>
public sealed record ConnectionTarget
{
    public required string Address { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    public string? Password { get; init; }

    public string? PrivateKey { get; init; }

    public string? Passphrase { get; init; }

    /// <summary>OpenSSH certificate for <see cref="PrivateKey"/>, if the key has one.</summary>
    public string? Certificate { get; init; }

    /// <summary>Authenticate with keys from the SSH agent.</summary>
    public bool UseAgent { get; init; }

    /// <summary>Restricts agent authentication to one key (null tries every agent key).</summary>
    public string? AgentKeyFingerprint { get; init; }

    /// <summary>Host this target was built from, if any (quick connect has none).</summary>
    public Guid? HostId { get; init; }

    /// <summary>Jump host to tunnel through; itself may have a <see cref="Via"/>.</summary>
    public ConnectionTarget? Via { get; init; }

    /// <summary>"web via bastion", for progress messages.</summary>
    public string Route => Via is null ? DisplayName : $"{DisplayName} via {Via.Route}";

    public string DisplayName { get; init; } = string.Empty;

    public string Endpoint => Port == 22 ? $"{Username}@{Address}" : $"{Username}@{Address}:{Port}";
}
