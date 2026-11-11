using Sshelfy.Core.Hosts;

namespace Sshelfy.Core.Ssh;

/// <summary>An authenticated SSH connection that can open channels.</summary>
public interface ISshConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Negotiated cipher, e.g. "aes256-gcm@openssh.com"; for the status bar.</summary>
    string? CipherName { get; }

    /// <summary>Raised when the connection drops (message is null for a clean close).</summary>
    event EventHandler<string?>? Disconnected;

    Task<IShellChannel> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default);

    /// <summary>Starts a port forward on this connection.</summary>
    /// <exception cref="PortForwardException">The port could not be bound or the server refused it.</exception>
    Task<IPortForward> StartForwardAsync(PortForwardRule rule, CancellationToken cancellationToken = default);

    /// <summary>Runs a command in its own exec channel (not in the interactive shell) and waits for it to finish.</summary>
    Task<SshCommandResult> RunCommandAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>An interactive shell (PTY) channel.</summary>
public interface IShellChannel : IAsyncDisposable
{
    /// <summary>Raised on a background thread with output from the remote side.</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Raised once when the remote side closes the shell.</summary>
    event EventHandler? Closed;

    /// <summary>Queues input; writes keep their order.</summary>
    void Write(byte[] data);

    void Resize(int columns, int rows);
}

/// <summary>Result of a command run on the server outside the interactive shell.</summary>
public sealed record SshCommandResult(int ExitCode, string Output, string Error);

public interface ISshConnectionFactory
{
    /// <exception cref="SshAuthenticationFailedException">Credentials were rejected.</exception>
    /// <exception cref="SshConnectionFailedException">Network, protocol or host key problem.</exception>
    Task<ISshConnection> ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default);
}

public class SshConnectionFailedException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SshAuthenticationFailedException(string message, Exception? inner = null)
    : SshConnectionFailedException(message, inner);

public sealed class HostKeyRejectedException()
    : SshConnectionFailedException("The server's host key was not trusted.");
