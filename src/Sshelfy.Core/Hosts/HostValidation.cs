namespace Sshelfy.Core.Hosts;

/// <summary>
/// Checks for addresses and user names that cannot be real and could be read as options by the
/// OpenSSH client (e.g. a user name "-oProxyCommand=…" in an imported host).
/// </summary>
public static class HostValidation
{
    /// <returns>Null when both are usable, otherwise what is wrong.</returns>
    public static string? Problem(string address, string username)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "Enter an address.";
        if (address.StartsWith('-') || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '@'))
            return "The address cannot start with \"-\" or contain spaces or \"@\".";
        if (username.StartsWith('-') || username.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '@'))
            return "The user name cannot start with \"-\" or contain spaces or \"@\".";
        return null;
    }

    public static string? Problem(HostProfile host) => Problem(host.Address, host.Username);
}
