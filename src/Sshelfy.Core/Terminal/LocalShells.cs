using Sshelfy.Core.Ssh;

namespace Sshelfy.Core.Terminal;

/// <summary>A shell that can run in a local terminal tab (PowerShell, cmd, a WSL distribution…).</summary>
/// <param name="Id">Stable id, used for history and restored tabs (e.g. "pwsh", "wsl:Ubuntu").</param>
/// <param name="Environment">Variables set for the process on top of the app's own (e.g. DISPLAY for X11 forwarding).</param>
/// <param name="Prepare">Runs before the process starts (e.g. writes the key file ssh.exe is pointed at); throw <see cref="LocalShellException"/> to fail.</param>
public sealed record LocalShellProfile(
    string Id,
    string Name,
    string Executable,
    string? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    Func<CancellationToken, Task>? Prepare = null);

/// <summary>Finds and starts local shells behind a pseudo console.</summary>
public interface ILocalShellLauncher
{
    /// <summary>False on platforms without a supported pseudo console.</summary>
    bool IsSupported { get; }

    /// <summary>The system OpenSSH client (ssh.exe), or null when it is not installed.</summary>
    string? FindOpenSsh() => null;

    /// <summary>Shells installed on this machine, the usual default first.</summary>
    Task<IReadOnlyList<LocalShellProfile>> DetectAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts the shell; its output and input flow through the returned channel like an SSH shell.</summary>
    /// <exception cref="LocalShellException">The shell could not be started.</exception>
    Task<IShellChannel> StartAsync(LocalShellProfile profile, int columns, int rows, string? workingDirectory = null, CancellationToken cancellationToken = default);
}

public sealed class LocalShellException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Platforms without local terminal support.</summary>
public sealed class NoLocalShells : ILocalShellLauncher
{
    public bool IsSupported => false;

    public Task<IReadOnlyList<LocalShellProfile>> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalShellProfile>>([]);

    public Task<IShellChannel> StartAsync(LocalShellProfile profile, int columns, int rows, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        throw new LocalShellException("Local terminals are not supported on this platform.");
}
