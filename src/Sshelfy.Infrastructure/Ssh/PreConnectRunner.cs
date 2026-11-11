using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Infrastructure.Ssh;

/// <summary>Runs pre-connect commands through the system shell and knocks ports with short TCP attempts or UDP datagrams.</summary>
internal sealed class PreConnectRunner(ILogger<PreConnectRunner> logger) : IPreConnectRunner
{
    internal static TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan KnockTimeout = TimeSpan.FromMilliseconds(300);

    public async Task RunAsync(HostProfile host, bool knock, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(host.PreConnectCommand))
            await RunCommandAsync(host, host.PreConnectCommand.Trim(), cancellationToken);

        if (knock && host.KnockSequence.Count > 0)
            await KnockAsync(host, cancellationToken);
    }

    private async Task RunCommandAsync(HostProfile host, string command, CancellationToken cancellationToken)
    {
        var start = OperatingSystem.IsWindows()
            // cmd /s /c "…" drops only the outer quotes, so the command keeps its own quoting (argv escaping would break it).
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"{command}\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["SSHELFY_HOST"] = host.Address;
        start.Environment["SSHELFY_PORT"] = host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["SSHELFY_USER"] = host.Username;

        logger.LogInformation("Running the pre-connect command for {Host}", host.DisplayName);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SshConnectionFailedException($"Could not run the command before connecting: {ex.Message}", ex);
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw new SshConnectionFailedException($"The command before connecting did not finish within {CommandTimeout.TotalSeconds:0} seconds.");
        }

        if (process.ExitCode != 0)
        {
            // A child the command started in the background (a VPN client, "start …") can keep the pipes
            // open after the command itself ended: take what arrived and do not wait for more.
            var detail = LastLine(await ReadUpToAsync(error)) ?? LastLine(await ReadUpToAsync(output));
            throw new SshConnectionFailedException(
                $"The command before connecting failed (exit code {process.ExitCode}){(detail is null ? "." : ": " + detail)}");
        }
    }

    private static async Task<string> ReadUpToAsync(Task<string> reader)
    {
        var finished = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(2)));
        return finished == reader ? await reader : string.Empty;
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    /// <summary>Each knock only has to reach the host: closed ports and timeouts are expected.</summary>
    private async Task KnockAsync(HostProfile host, CancellationToken cancellationToken)
    {
        logger.LogInformation("Knocking {Count} port(s) on {Host}", host.KnockSequence.Count, host.DisplayName);
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(host.KnockDelayMilliseconds, 0, 10_000));
        foreach (var knock in host.KnockSequence)
        {
            if (knock.Udp)
            {
                using var udp = new UdpClient();
                try
                {
                    await udp.SendAsync(new byte[1], host.Address, knock.Port, cancellationToken);
                }
                catch (SocketException)
                {
                }
            }
            else
            {
                using var tcp = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(KnockTimeout);
                try
                {
                    await tcp.ConnectAsync(host.Address, knock.Port, attempt.Token);
                }
                catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                }
            }

            await Task.Delay(delay, cancellationToken);
        }
    }
}
