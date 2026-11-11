using System.Globalization;

namespace Sshelfy.Core.Hosts;

/// <summary>One knock of a port knocking sequence.</summary>
public readonly record struct PortKnock(int Port, bool Udp)
{
    public override string ToString() => Udp ? $"{Port}/udp" : Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses "7000, 8000/udp, 9000/tcp".</summary>
    /// <returns>The knocks, or null with <paramref name="error"/> when an entry is not a port.</returns>
    public static IReadOnlyList<PortKnock>? ParseSequence(string? text, out string? error)
    {
        error = null;
        var knocks = new List<PortKnock>();
        foreach (var entry in (text ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('/', 2);
            var protocol = parts.Length == 2 ? parts[1].ToLowerInvariant() : "tcp";
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535
                || protocol is not ("tcp" or "udp"))
            {
                error = $"\"{entry}\" is not a port. Use numbers like 7000, 8000/udp, 9000.";
                return null;
            }

            knocks.Add(new PortKnock(port, protocol == "udp"));
        }

        return knocks;
    }

    public static string Format(IEnumerable<PortKnock> knocks) => string.Join(", ", knocks);
}

/// <summary>Runs a host's preparation before the SSH connection: a local command (VPN, tunnel…) and port knocking.</summary>
public interface IPreConnectRunner
{
    /// <param name="knock">Knock the host's ports; only for the host this PC connects to directly (not behind a jump host).</param>
    /// <exception cref="Ssh.SshConnectionFailedException">The command failed or timed out.</exception>
    Task RunAsync(HostProfile host, bool knock, CancellationToken cancellationToken = default);
}

/// <summary>For platforms and tests without preparation.</summary>
public sealed class NoPreConnect : IPreConnectRunner
{
    public Task RunAsync(HostProfile host, bool knock, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
