using System.Security.Cryptography;
using System.Text;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Services;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.Services;

/// <summary>
/// Runs a host's pre-connect step, asking once before a command runs that the user did not type in
/// this app (e.g. from an imported backup): a host file must not be a way to run programs unnoticed.
/// </summary>
public sealed class PreConnectGate(IPreConnectRunner runner, ISettingsService settings, IDialogService dialogs)
{
    /// <summary>Marks the host's current command as the user's own (called when the host editor saves it).</summary>
    public void Approve(HostProfile host)
    {
        if (string.IsNullOrWhiteSpace(host.PreConnectCommand))
            return;
        var key = Key(host);
        if (!settings.Current.ApprovedPreConnectCommands.Contains(key))
            settings.Update(s => s with { ApprovedPreConnectCommands = [.. s.ApprovedPreConnectCommands, key] });
    }

    /// <returns>False when the user declined to run the command.</returns>
    /// <exception cref="SshConnectionFailedException">The command failed.</exception>
    public async Task<bool> RunAsync(HostProfile host, bool knock, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(host.PreConnectCommand) && !settings.Current.ApprovedPreConnectCommands.Contains(Key(host)))
        {
            var allowed = await dialogs.ConfirmAsync(
                "Run a command before connecting?",
                $"{host.DisplayName} runs this on your computer before each connection:\n\n{host.PreConnectCommand.Trim()}\n\nOnly allow it if you know where this host's settings came from.",
                "Run it");
            if (!allowed)
                return false;
            Approve(host);
        }

        await runner.RunAsync(host, knock, cancellationToken);
        return true;
    }

    private static string Key(HostProfile host) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.Id.ToString("N") + "\n" + host.PreConnectCommand!.Trim())));
}
