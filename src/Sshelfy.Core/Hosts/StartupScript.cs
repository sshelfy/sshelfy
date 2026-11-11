using System.Text;
using System.Text.RegularExpressions;

namespace Sshelfy.Core.Hosts;

/// <summary>An environment variable a host profile sets in its sessions.</summary>
public sealed record EnvironmentVariable(string Name, string Value);

/// <summary>
/// The shell line that applies a host's environment variables and startup command. Servers usually
/// refuse SSH "env" requests (AcceptEnv), so the variables are exported by the login shell instead.
/// </summary>
public static partial class StartupScript
{
    /// <summary>POSIX line such as "export A='1' B='2'; cd /srv && tmux attach", or null when there is nothing to run.</summary>
    public static string? Build(IReadOnlyList<EnvironmentVariable> environment, string? command)
    {
        var parts = new List<string>();
        var exports = environment.Where(v => IsValidName(v.Name)).Select(v => $"{v.Name}={Quote(v.Value)}").ToList();
        if (exports.Count > 0)
            parts.Add("export " + string.Join(' ', exports));
        if (!string.IsNullOrWhiteSpace(command))
            parts.Add(command.Trim());
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Remote command for ssh -t: the startup line, then the user's login shell so the session stays open.</summary>
    public static string? RemoteCommand(IReadOnlyList<EnvironmentVariable> environment, string? command) =>
        Build(environment, command) is { } line ? line + "; exec \"${SHELL:-/bin/sh}\" -l" : null;

    public static bool IsValidName(string name) => VariableName().IsMatch(name);

    /// <summary>Parses "NAME=value" lines (blank lines and # comments are skipped).</summary>
    /// <returns>The variables, or null with <paramref name="error"/> naming the first bad line.</returns>
    public static IReadOnlyList<EnvironmentVariable>? Parse(string text, out string? error)
    {
        error = null;
        var variables = new List<EnvironmentVariable>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var equals = line.IndexOf('=');
            var name = equals < 0 ? line : line[..equals].Trim();
            if (equals < 0 || !IsValidName(name))
            {
                error = $"Line {number}: use NAME=value, with letters, digits and _ in the name.";
                return null;
            }

            variables.Add(new EnvironmentVariable(name, line[(equals + 1)..]));
        }

        return variables;
    }

    public static string Format(IReadOnlyList<EnvironmentVariable> environment) =>
        string.Join('\n', environment.Select(v => $"{v.Name}={v.Value}"));

    /// <summary>Single-quotes a value for POSIX shells (embedded quotes become '\'').</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();
}
