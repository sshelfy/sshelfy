namespace Sshelfy.Core.Hosts;

/// <summary>Parses "user@host[:port]" or "ssh user@host -p port" style input.</summary>
public static class QuickConnectParser
{
    public static bool TryParse(string? input, out string username, out string address, out int port)
    {
        username = string.Empty;
        address = string.Empty;
        port = 22;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0] == "ssh")
            parts.RemoveAt(0);

        string? target = null;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "-p" && i + 1 < parts.Count && int.TryParse(parts[i + 1], out var p))
            {
                port = p;
                i++;
            }
            else if (!parts[i].StartsWith('-'))
            {
                target ??= parts[i];
            }
        }

        if (target is null)
            return false;

        var at = target.LastIndexOf('@');
        if (at <= 0 || at == target.Length - 1)
            return false;

        username = target[..at];
        var hostPart = target[(at + 1)..];

        // [ipv6]:port, host:port, or bare host
        if (hostPart.StartsWith('['))
        {
            var close = hostPart.IndexOf(']');
            if (close < 0)
                return false;
            address = hostPart[1..close];
            var rest = hostPart[(close + 1)..];
            if (rest.StartsWith(':') && !int.TryParse(rest[1..], out port))
                return false;
        }
        else
        {
            var colon = hostPart.LastIndexOf(':');
            if (colon > 0 && hostPart.IndexOf(':') == colon)
            {
                if (!int.TryParse(hostPart[(colon + 1)..], out port))
                    return false;
                address = hostPart[..colon];
            }
            else
            {
                address = hostPart;
            }
        }

        return address.Length > 0 && port is > 0 and < 65536;
    }
}
