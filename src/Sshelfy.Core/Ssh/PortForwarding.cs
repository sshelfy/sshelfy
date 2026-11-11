namespace Sshelfy.Core.Ssh;

public enum ForwardKind
{
    /// <summary>ssh -L: a port on this computer reaches a destination as seen from the server.</summary>
    Local,

    /// <summary>ssh -R: a port on the server reaches a destination as seen from this computer.</summary>
    Remote,

    /// <summary>ssh -D: a SOCKS proxy on this computer that tunnels through the server.</summary>
    Dynamic,
}

/// <summary>A saved port forwarding rule of a host.</summary>
public sealed record PortForwardRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ForwardKind Kind { get; init; } = ForwardKind.Local;

    /// <summary>Address the listening side binds to (local for -L/-D, remote for -R).</summary>
    public string BindAddress { get; init; } = "127.0.0.1";

    public int BindPort { get; init; }

    /// <summary>Target host; unused for dynamic forwards.</summary>
    public string DestinationHost { get; init; } = "localhost";

    public int DestinationPort { get; init; }

    /// <summary>Start the tunnel as soon as the session connects.</summary>
    public bool AutoStart { get; init; } = true;

    public string Describe() => Kind switch
    {
        ForwardKind.Local => $"{BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        ForwardKind.Remote => $"server {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        _ => $"SOCKS {BindAddress}:{BindPort}",
    };

    /// <returns>A message for the first invalid field, or null when the rule is usable.</returns>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress))
            return "Enter a bind address.";
        if (BindPort is < 1 or > 65535)
            return "The listening port must be between 1 and 65535.";
        if (Kind == ForwardKind.Dynamic)
            return null;
        if (string.IsNullOrWhiteSpace(DestinationHost))
            return "Enter a destination host.";
        if (DestinationPort is < 1 or > 65535)
            return "The destination port must be between 1 and 65535.";
        return null;
    }
}

public enum TunnelState
{
    Stopped,
    Active,
    Failed,
}

/// <summary>A running port forward on an SSH connection.</summary>
public interface IPortForward : IAsyncDisposable
{
    PortForwardRule Rule { get; }

    /// <summary>Number of connections accepted since the tunnel started.</summary>
    int ConnectionCount { get; }

    /// <summary>Raised (on a background thread) when a connection is accepted.</summary>
    event EventHandler? ConnectionOpened;

    /// <summary>Raised (on a background thread) when the tunnel fails after starting.</summary>
    event EventHandler<string>? Failed;
}

public sealed class PortForwardException(string message, Exception? inner = null) : Exception(message, inner);
