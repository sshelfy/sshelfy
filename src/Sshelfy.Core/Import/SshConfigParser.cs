using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Core.Import;

/// <summary>A concrete host alias from ~/.ssh/config with its effective settings.</summary>
public sealed record SshConfigHost(
    string Alias,
    string HostName,
    int Port,
    string? User,
    IReadOnlyList<string> IdentityFiles,
    string? ProxyJump);

public sealed record SshConfigParseResult(IReadOnlyList<SshConfigHost> Hosts, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads OpenSSH client configuration the way ssh does for the supported keywords:
/// blocks apply in file order, the first value obtained for a keyword wins (IdentityFile
/// accumulates), and wildcard blocks provide defaults for concrete aliases.
/// </summary>
public static class SshConfigParser
{
    private const int MaxIncludeDepth = 16;

    private sealed record Block(IReadOnlyList<string> Patterns, List<(string Key, string Value)> Options, bool IsMatch);

    /// <param name="content">Main config file text.</param>
    /// <param name="resolveInclude">Returns the text of every file an Include pattern refers to.</param>
    /// <param name="homeDirectory">Used for "~" and "%d".</param>
    /// <param name="localUser">Used for "%u" and as the default user in "%r".</param>
    public static SshConfigParseResult Parse(
        string content,
        Func<string, IEnumerable<string>>? resolveInclude,
        string homeDirectory,
        string localUser)
    {
        var warnings = new List<string>();
        var blocks = new List<Block> { new(["*"], [], false) };
        ReadLines(content, blocks, resolveInclude, warnings, depth: 0);

        if (blocks.Any(b => b.IsMatch))
            warnings.Add("Match blocks are not supported and were ignored.");

        var aliases = blocks
            .Where(b => !b.IsMatch)
            .SelectMany(b => b.Patterns)
            .Where(p => p.IndexOfAny(['*', '?', '!']) < 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var hosts = new List<SshConfigHost>();
        foreach (var alias in aliases)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var identities = new List<string>();
            foreach (var block in blocks.Where(b => !b.IsMatch && Matches(b.Patterns, alias)))
            {
                foreach (var (key, value) in block.Options)
                {
                    if (key.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase))
                        identities.Add(value);
                    else
                        values.TryAdd(key, value);
                }
            }

            var hostName = values.TryGetValue("HostName", out var hn) ? hn.Replace("%h", alias, StringComparison.Ordinal) : alias;
            var user = values.GetValueOrDefault("User");
            var port = 22;
            if (values.TryGetValue("Port", out var portText) && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            {
                warnings.Add($"{alias}: invalid port \"{portText}\", using 22.");
                port = 22;
            }

            var proxyJump = values.GetValueOrDefault("ProxyJump");
            if (string.Equals(proxyJump, "none", StringComparison.OrdinalIgnoreCase))
                proxyJump = null;
            if (values.ContainsKey("ProxyCommand"))
                warnings.Add($"{alias}: ProxyCommand is not supported; use ProxyJump instead.");

            var expanded = identities
                .Select(path => ExpandPath(path, homeDirectory, localUser, alias, hostName, user ?? localUser))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            hosts.Add(new SshConfigHost(alias, hostName, port, user, expanded, proxyJump));
        }

        return new SshConfigParseResult(hosts, warnings);
    }

    private static void ReadLines(string content, List<Block> blocks, Func<string, IEnumerable<string>>? resolveInclude, List<string> warnings, int depth)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var (key, value) = SplitKeyword(line);
            if (key.Length == 0)
                continue;

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(new Block(Tokenize(value), [], false));
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(new Block([], [], true));
            }
            else if (key.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                if (resolveInclude is null)
                    continue;
                if (depth >= MaxIncludeDepth)
                {
                    warnings.Add("Include nesting is too deep; the rest was skipped.");
                    continue;
                }

                foreach (var pattern in Tokenize(value))
                {
                    foreach (var included in resolveInclude(pattern))
                        ReadLines(included, blocks, resolveInclude, warnings, depth + 1);
                }
            }
            else
            {
                blocks[^1].Options.Add((key, Unquote(value)));
            }
        }
    }

    private static (string Key, string Value) SplitKeyword(string line)
    {
        // "Key value", "Key=value" or "Key = value".
        var match = Regex.Match(line, @"^(\S+?)\s*(?:=\s*|\s+)(.*)$");
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value.Trim()) : (line, string.Empty);
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(value, "\"([^\"]*)\"|(\\S+)"))
            tokens.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return tokens;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>OpenSSH pattern list: any positive match and no negated match.</summary>
    internal static bool Matches(IReadOnlyList<string> patterns, string alias)
    {
        var matched = false;
        foreach (var pattern in patterns)
        {
            var negated = pattern.StartsWith('!');
            var body = negated ? pattern[1..] : pattern;
            if (!Glob(body, alias))
                continue;
            if (negated)
                return false;
            matched = true;
        }

        return matched;
    }

    private static bool Glob(string pattern, string text)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }

    private static string ExpandPath(string path, string home, string localUser, string alias, string hostName, string remoteUser)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = home + path[1..];

        return path
            .Replace("%d", home, StringComparison.Ordinal)
            .Replace("%u", localUser, StringComparison.Ordinal)
            .Replace("%r", remoteUser, StringComparison.Ordinal)
            .Replace("%h", hostName, StringComparison.Ordinal)
            .Replace("%n", alias, StringComparison.Ordinal)
            .Replace("%%", "%", StringComparison.Ordinal);
    }
}

/// <summary>Parses OpenSSH known_hosts files.</summary>
public static class KnownHostsParser
{
    /// <param name="skipped">Hashed entries, markers (@cert-authority, @revoked) and unreadable lines.</param>
    public static IReadOnlyList<KnownHost> Parse(string content, out int skipped)
    {
        skipped = 0;
        var result = new List<KnownHost>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0].StartsWith('@') || parts[0].StartsWith("|1|", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException)
            {
                skipped++;
                continue;
            }

            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
            foreach (var hostSpec in parts[0].Split(','))
            {
                if (hostSpec.StartsWith('!') || hostSpec.IndexOfAny(['*', '?']) >= 0)
                    continue; // Wildcards and negations cannot be stored as concrete trust entries.

                var (host, port) = ParseHost(hostSpec);
                result.Add(new KnownHost(host, port, parts[1], fingerprint, DateTimeOffset.UtcNow));
            }
        }

        return result;
    }

    private static (string Host, int Port) ParseHost(string spec)
    {
        // "[host]:port" for non-default ports.
        if (spec.StartsWith('[') && spec.IndexOf("]:", StringComparison.Ordinal) is var close and > 0
            && int.TryParse(spec[(close + 2)..], out var port))
            return (spec[1..close], port);
        return (spec, 22);
    }
}
