using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Core.Import;

/// <summary>Read access to the user's OpenSSH files (~/.ssh).</summary>
public interface ILocalSshFiles
{
    string HomeDirectory { get; }

    string LocalUser { get; }

    /// <summary>Path shown to the user, e.g. "C:\Users\me\.ssh\config".</summary>
    string ConfigPath { get; }

    string? ReadConfig();

    /// <summary>Texts of the files an Include pattern refers to (relative patterns are resolved against ~/.ssh).</summary>
    IEnumerable<string> ResolveInclude(string pattern);

    string? ReadKnownHosts();

    /// <summary>Reads a small text file (a private key); null if missing or too large.</summary>
    string? ReadKeyFile(string path);
}

public sealed record ImportCandidate(SshConfigHost Source, bool AlreadyExists, string? KeyPath);

public sealed record SshConfigScan(
    bool ConfigFound,
    IReadOnlyList<ImportCandidate> Candidates,
    IReadOnlyList<string> Warnings,
    int KnownHostsEntries);

public sealed record SshImportResult(int HostsImported, int KeysImported, int KnownHostsAdded, IReadOnlyList<string> Warnings);

/// <summary>Imports hosts, keys and trusted host keys from OpenSSH configuration.</summary>
public sealed class SshConfigImporter
{
    private const int PassphraseAttempts = 3;

    private readonly IHostCatalog _catalog;
    private readonly IKnownHostsStore _knownHosts;
    private readonly ILocalSshFiles _files;
    private readonly ISshKeyInspector _inspector;

    public SshConfigImporter(IHostCatalog catalog, IKnownHostsStore knownHosts, ILocalSshFiles files, ISshKeyInspector inspector)
    {
        _catalog = catalog;
        _knownHosts = knownHosts;
        _files = files;
        _inspector = inspector;
    }

    public SshConfigScan Scan()
    {
        var text = _files.ReadConfig();
        var knownHosts = _files.ReadKnownHosts() is { } kh ? KnownHostsParser.Parse(kh, out _).Count : 0;
        if (text is null)
            return new SshConfigScan(false, [], [], knownHosts);

        var parsed = SshConfigParser.Parse(text, _files.ResolveInclude, _files.HomeDirectory, _files.LocalUser);
        var candidates = parsed.Hosts
            .Select(h => new ImportCandidate(h, Exists(h), h.IdentityFiles.FirstOrDefault(p => _files.ReadKeyFile(p) is not null)))
            .ToList();
        return new SshConfigScan(true, candidates, parsed.Warnings, knownHosts);
    }

    /// <param name="askPassphrase">Asks for the passphrase of an encrypted key; null skips the key.</param>
    public async Task<SshImportResult> ImportAsync(
        IReadOnlyList<ImportCandidate> selected,
        Guid? groupId,
        bool importKnownHosts,
        Func<string, SshKeyProblem, Task<string?>> askPassphrase)
    {
        var warnings = new List<string>();
        var keysByPath = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        var keysImported = 0;

        // 1. Keys, deduplicated by fingerprint against the ones already in the vault.
        foreach (var path in selected.Select(c => c.KeyPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (keyId, imported) = await ImportKeyAsync(path, askPassphrase, warnings);
            keysByPath[path] = keyId;
            if (imported)
                keysImported++;
        }

        // 2. Hosts.
        var created = new Dictionary<string, HostProfile>(StringComparer.Ordinal);
        foreach (var candidate in selected.Where(c => !c.AlreadyExists))
        {
            var source = candidate.Source;
            var keyId = candidate.KeyPath is { } kp ? keysByPath.GetValueOrDefault(kp) : null;
            var host = new HostProfile
            {
                Name = source.Alias,
                Address = source.HostName,
                Port = source.Port,
                Username = source.User ?? _files.LocalUser,
                GroupId = groupId,
                AuthMethod = keyId is null ? AuthMethod.Password : AuthMethod.PrivateKey,
                KeyId = keyId,
            };
            await _catalog.SaveHostAsync(host);
            created[source.Alias] = host;
        }

        // 3. ProxyJump links, once every imported host exists.
        foreach (var candidate in selected.Where(c => c.Source.ProxyJump is not null && created.ContainsKey(c.Source.Alias)))
        {
            var spec = candidate.Source.ProxyJump!;
            var hops = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (hops.Length > 1)
                warnings.Add($"{candidate.Source.Alias}: only the last hop of \"{spec}\" was linked; configure earlier hops on that host.");

            var jump = FindJumpHost(hops[^1], created);
            if (jump is null)
            {
                warnings.Add($"{candidate.Source.Alias}: jump host \"{hops[^1]}\" is not a saved host, so it was not linked.");
                continue;
            }

            var host = created[candidate.Source.Alias] with { JumpHostId = jump.Id };
            if (_catalog.CreatesJumpLoop(host))
            {
                warnings.Add($"{candidate.Source.Alias}: ProxyJump loops back, so it was not linked.");
                continue;
            }

            await _catalog.SaveHostAsync(host);
            created[candidate.Source.Alias] = host;
        }

        // 4. Trusted host keys.
        var knownAdded = 0;
        if (importKnownHosts && _files.ReadKnownHosts() is { } knownText)
        {
            foreach (var entry in KnownHostsParser.Parse(knownText, out _))
            {
                var existing = await _knownHosts.FindAsync(entry.Host, entry.Port);
                if (existing.Any(k => k.Fingerprint == entry.Fingerprint))
                    continue;
                if (existing.Any(k => k.Algorithm == entry.Algorithm))
                {
                    warnings.Add($"known_hosts: {entry.Host}:{entry.Port} has a different {entry.Algorithm} key than the one sshelfy trusts; kept sshelfy's.");
                    continue;
                }

                await _knownHosts.AddAsync(entry);
                knownAdded++;
            }
        }

        return new SshImportResult(created.Count, keysImported, knownAdded, warnings);
    }

    private bool Exists(SshConfigHost source) =>
        _catalog.Hosts.Any(h => h.Port == source.Port
                                && string.Equals(h.Address, source.HostName, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(h.Username, source.User ?? _files.LocalUser, StringComparison.Ordinal));

    private HostProfile? FindJumpHost(string spec, Dictionary<string, HostProfile> created)
    {
        if (created.TryGetValue(spec, out var byAlias))
            return byAlias;

        // "user@host:port" or a bare host name.
        var address = spec;
        var user = (string?)null;
        var port = 22;
        if (address.LastIndexOf('@') is var at and > 0)
        {
            user = address[..at];
            address = address[(at + 1)..];
        }

        if (address.LastIndexOf(':') is var colon and > 0 && int.TryParse(address[(colon + 1)..], out var parsed))
        {
            port = parsed;
            address = address[..colon];
        }

        return created.Values.Concat(_catalog.Hosts).FirstOrDefault(h =>
            (string.Equals(h.Name, spec, StringComparison.Ordinal) && user is null)
            || (string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase) && h.Port == port
                && (user is null || h.Username == user)));
    }

    private async Task<(Guid? KeyId, bool Imported)> ImportKeyAsync(string path, Func<string, SshKeyProblem, Task<string?>> askPassphrase, List<string> warnings)
    {
        var content = _files.ReadKeyFile(path);
        if (content is null)
            return (null, false);

        string? passphrase = null;
        for (var attempt = 0; attempt <= PassphraseAttempts; attempt++)
        {
            var info = _inspector.Inspect(content, passphrase, out var problem);
            if (info is not null)
            {
                if (_catalog.Keys.FirstOrDefault(k => k.Fingerprint == info.Fingerprint) is { } existing)
                    return (existing.Id, false);

                var key = await _catalog.ImportKeyAsync(Path.GetFileName(path), content, passphrase);
                return (key.Id, true);
            }

            if (problem == SshKeyProblem.InvalidFormat)
            {
                warnings.Add($"{path} is not a supported private key; hosts using it fall back to password login.");
                return (null, false);
            }

            if (attempt == PassphraseAttempts)
                break;

            passphrase = await askPassphrase(path, problem);
            if (passphrase is null)
                break;
        }

        warnings.Add($"{Path.GetFileName(path)} was skipped (no passphrase); hosts using it fall back to password login.");
        return (null, false);
    }
}
