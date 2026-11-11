using System.Text;
using Sshelfy.Core.Hosts;

namespace Sshelfy.Core.Ssh;

/// <summary>
/// Arguments for the system OpenSSH client (ssh.exe), used for features SSH.NET cannot provide:
/// agent forwarding (-A) and X11 forwarding (-Y). The session then runs in a local terminal.
/// </summary>
public static class OpenSshCommandLine
{
    /// <param name="jumps">Jump hosts from the first hop to the last (nearest the target).</param>
    /// <param name="identityFile">Private key to log in with (a FIDO2 key written out for ssh.exe); its certificate is read from "{file}-cert.pub".</param>
    /// <exception cref="ArgumentException">An address or user name could be read as an ssh option (see <see cref="HostValidation"/>).</exception>
    public static string Build(HostProfile host, IReadOnlyList<HostProfile> jumps, bool forwardAgent, bool forwardX11, string? identityFile = null)
    {
        foreach (var hop in jumps.Append(host))
        {
            if (HostValidation.Problem(hop) is { } problem)
                throw new ArgumentException($"{hop.DisplayName}: {problem}");
        }

        var remote = StartupScript.RemoteCommand(host.Environment, host.StartupCommand);
        var args = new List<string> { "-o", "ServerAliveInterval=30" };
        if (identityFile is not null)
            args.AddRange(["-i", identityFile, "-o", "IdentitiesOnly=yes"]);
        if (forwardAgent)
            args.Add("-A");
        if (forwardX11)
            args.Add("-Y");
        if (host.Port != 22)
            args.AddRange(["-p", host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (jumps.Count > 0)
            args.AddRange(["-J", string.Join(',', jumps.Select(Destination))]);

        foreach (var rule in host.PortForwards.Where(r => r.AutoStart))
        {
            args.AddRange(rule.Kind switch
            {
                ForwardKind.Local => ["-L", $"{rule.BindAddress}:{rule.BindPort}:{rule.DestinationHost}:{rule.DestinationPort}"],
                ForwardKind.Remote => ["-R", $"{rule.BindAddress}:{rule.BindPort}:{rule.DestinationHost}:{rule.DestinationPort}"],
                _ => ["-D", $"{rule.BindAddress}:{rule.BindPort}"],
            });
        }

        // Variables and the startup command run as the remote command, then the login shell keeps the session open.
        if (remote is not null)
            args.Add("-t");
        if (host.Username.Length > 0)
            args.AddRange(["-l", host.Username]);

        // "--" ends the options: nothing after it can be taken for one. The port went in -p.
        args.AddRange(["--", host.Address]);
        if (remote is not null)
            args.Add(remote);
        return string.Join(' ', args.Select(Quote));
    }

    /// <summary>A DISPLAY value such as "localhost:0.0" or "127.0.0.1:1" (it goes into a process environment block).</summary>
    public static bool IsValidDisplay(string display) =>
        display.Length <= 255 && display.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']' or '_')
        && X11Endpoint(display) is not null;

    /// <summary>The TCP endpoint of an X display such as "localhost:0.0" (port 6000 + display number); null for other forms.</summary>
    public static (string Host, int Port)? X11Endpoint(string display)
    {
        var colon = display.LastIndexOf(':');
        if (colon < 0)
            return null;
        var host = colon == 0 ? "localhost" : display[..colon];
        var number = display[(colon + 1)..].Split('.')[0];
        return int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n < 1000
            ? (host, 6000 + n)
            : null;
    }

    /// <summary>user@host[:port], the form -J expects.</summary>
    private static string Destination(HostProfile host) =>
        host.Port == 22 ? $"{host.Username}@{host.Address}" : $"{host.Username}@{host.Address}:{host.Port}";

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules) for arguments with spaces or quotes.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return argument;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(c);
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
