using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Security;

namespace Sshelfy.Core.Hosts;

/// <summary>Instruction for a secret when saving: <c>null</c> keeps the stored value.</summary>
public readonly record struct SecretUpdate(string? Value)
{
    public static SecretUpdate Clear => new(null);
}

public sealed class SshKeyImportException(SshKeyProblem problem)
    : Exception(problem switch
    {
        SshKeyProblem.PassphraseRequired => "This key is encrypted. Enter its passphrase.",
        SshKeyProblem.WrongPassphrase => "The passphrase is incorrect.",
        _ => "This is not a supported private key (OpenSSH, PEM or PuTTY).",
    })
{
    public SshKeyProblem Problem { get; } = problem;
}

public interface IHostCatalog
{
    IReadOnlyList<HostGroup> Groups { get; }

    IReadOnlyList<HostProfile> Hosts { get; }

    IReadOnlyList<SshKeyProfile> Keys { get; }

    /// <summary>Raised on the caller's synchronization context after any change.</summary>
    event EventHandler? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    HostProfile? FindHost(Guid id);

    SshKeyProfile? FindKey(Guid id);

    Task SaveHostAsync(HostProfile host, SecretUpdate? password = null);

    Task DeleteHostAsync(Guid hostId);

    Task SaveGroupAsync(HostGroup group);

    /// <summary>Deletes a group; its hosts and subgroups move to the parent group.</summary>
    Task DeleteGroupAsync(Guid groupId);

    Task SetGroupExpandedAsync(Guid groupId, bool isExpanded);

    Task MoveHostAsync(Guid hostId, Guid? groupId);

    Task<SshKeyProfile> ImportKeyAsync(string name, string privateKey, string? passphrase);

    Task DeleteKeyAsync(Guid keyId);

    /// <summary>Attaches an OpenSSH certificate to a key, or removes it with null.</summary>
    /// <exception cref="SshCertificateException">Not a certificate, or one for a different key.</exception>
    Task AttachCertificateAsync(Guid keyId, string? certificate);

    Task MarkConnectedAsync(Guid hostId);

    Task<bool> HasSavedPasswordAsync(Guid hostId);

    /// <summary>True when following the jump chain from <paramref name="host"/> reaches it again.</summary>
    bool CreatesJumpLoop(HostProfile host);

    /// <summary>Resolves secrets from the vault. The vault must be unlocked if the host uses any.</summary>
    Task<ConnectionTarget> BuildTargetAsync(HostProfile host, string? passwordOverride = null);
}

/// <summary>
/// In-memory host catalog backed by <see cref="IHostRepository"/> (metadata) and
/// <see cref="ISecretVault"/> (passwords and keys).
/// </summary>
/// <remarks>
/// Awaits intentionally keep the caller's context so <see cref="Changed"/> fires on the UI thread.
/// </remarks>
public sealed class HostCatalog : IHostCatalog
{
    private readonly IHostRepository _repository;
    private readonly ISecretVault _vault;
    private readonly ISshKeyInspector _keyInspector;
    private HostCatalogData _data = new();

    public HostCatalog(IHostRepository repository, ISecretVault vault, ISshKeyInspector keyInspector)
    {
        _repository = repository;
        _vault = vault;
        _keyInspector = keyInspector;
    }

    public IReadOnlyList<HostGroup> Groups => _data.Groups;

    public IReadOnlyList<HostProfile> Hosts => _data.Hosts;

    public IReadOnlyList<SshKeyProfile> Keys => _data.Keys;

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _data = await _repository.LoadAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public HostProfile? FindHost(Guid id) => _data.Hosts.FirstOrDefault(h => h.Id == id);

    public SshKeyProfile? FindKey(Guid id) => _data.Keys.FirstOrDefault(k => k.Id == id);

    public async Task SaveHostAsync(HostProfile host, SecretUpdate? password = null)
    {
        if (CreatesJumpLoop(host))
            throw new InvalidOperationException("This jump host leads back to the host itself.");

        if (password is { } update)
            await _vault.SetAsync(SecretKeys.HostPassword(host.Id), string.IsNullOrEmpty(update.Value) ? null : update.Value);

        Upsert(_data.Hosts, host, h => h.Id == host.Id);
        await PersistAsync();
    }

    public async Task DeleteHostAsync(Guid hostId)
    {
        if (_data.Hosts.RemoveAll(h => h.Id == hostId) == 0)
            return;

        // Hosts that jumped through the deleted one now connect directly.
        for (var i = 0; i < _data.Hosts.Count; i++)
        {
            if (_data.Hosts[i].JumpHostId == hostId)
                _data.Hosts[i] = _data.Hosts[i] with { JumpHostId = null };
        }

        if (_vault.State == VaultState.Unlocked)
            await _vault.RemoveByPrefixAsync(SecretKeys.HostPrefix(hostId));

        await PersistAsync();
    }

    public async Task SaveGroupAsync(HostGroup group)
    {
        if (group.ParentId is { } parent && IsDescendantOrSelf(parent, group.Id))
            throw new InvalidOperationException("A group cannot be moved into itself.");

        Upsert(_data.Groups, group, g => g.Id == group.Id);
        await PersistAsync();
    }

    public async Task DeleteGroupAsync(Guid groupId)
    {
        var group = _data.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null)
            return;

        for (var i = 0; i < _data.Hosts.Count; i++)
        {
            if (_data.Hosts[i].GroupId == groupId)
                _data.Hosts[i] = _data.Hosts[i] with { GroupId = group.ParentId };
        }

        for (var i = 0; i < _data.Groups.Count; i++)
        {
            if (_data.Groups[i].ParentId == groupId)
                _data.Groups[i] = _data.Groups[i] with { ParentId = group.ParentId };
        }

        _data.Groups.Remove(group);
        await PersistAsync();
    }

    public async Task SetGroupExpandedAsync(Guid groupId, bool isExpanded)
    {
        var index = _data.Groups.FindIndex(g => g.Id == groupId);
        if (index < 0 || _data.Groups[index].IsExpanded == isExpanded)
            return;

        _data.Groups[index] = _data.Groups[index] with { IsExpanded = isExpanded };
        // Expansion is cosmetic: persist, but don't make every view rebuild.
        await _repository.SaveAsync(_data);
    }

    public async Task MoveHostAsync(Guid hostId, Guid? groupId)
    {
        var index = _data.Hosts.FindIndex(h => h.Id == hostId);
        if (index < 0 || _data.Hosts[index].GroupId == groupId)
            return;

        _data.Hosts[index] = _data.Hosts[index] with { GroupId = groupId };
        await PersistAsync();
    }

    public async Task<SshKeyProfile> ImportKeyAsync(string name, string privateKey, string? passphrase)
    {
        passphrase = string.IsNullOrEmpty(passphrase) ? null : passphrase;
        var info = _keyInspector.Inspect(privateKey, passphrase, out var problem)
                   ?? throw new SshKeyImportException(problem);

        var key = new SshKeyProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? info.Algorithm : name.Trim(),
            Algorithm = info.Algorithm,
            Fingerprint = info.Fingerprint,
            HasPassphrase = passphrase is not null,
        };

        await _vault.SetAsync(SecretKeys.KeyMaterial(key.Id), privateKey);
        await _vault.SetAsync(SecretKeys.KeyPassphrase(key.Id), passphrase);

        _data.Keys.Add(key);
        await PersistAsync();
        return key;
    }

    public async Task AttachCertificateAsync(Guid keyId, string? certificate)
    {
        var index = _data.Keys.FindIndex(k => k.Id == keyId);
        if (index < 0)
            return;

        if (certificate is not null)
        {
            var parsed = SshCertificate.Parse(certificate);
            if (parsed.PublicKeyFingerprint != _data.Keys[index].Fingerprint)
                throw new SshCertificateException("This certificate was issued for a different key.");
            if (parsed.Kind != SshCertificateKind.User)
                throw new SshCertificateException("This is a host certificate; logging in needs a user certificate.");
            certificate = certificate.Trim();
        }

        _data.Keys[index] = _data.Keys[index] with { Certificate = certificate };
        await PersistAsync();
    }

    public async Task DeleteKeyAsync(Guid keyId)
    {
        if (_data.Keys.RemoveAll(k => k.Id == keyId) == 0)
            return;

        for (var i = 0; i < _data.Hosts.Count; i++)
        {
            if (_data.Hosts[i].KeyId == keyId)
                _data.Hosts[i] = _data.Hosts[i] with { KeyId = null };
        }

        if (_vault.State == VaultState.Unlocked)
            await _vault.RemoveByPrefixAsync(SecretKeys.KeyPrefix(keyId));

        await PersistAsync();
    }

    public async Task MarkConnectedAsync(Guid hostId)
    {
        var index = _data.Hosts.FindIndex(h => h.Id == hostId);
        if (index < 0)
            return;

        _data.Hosts[index] = _data.Hosts[index] with { LastConnectedAt = DateTimeOffset.UtcNow };
        await PersistAsync();
    }

    public async Task<bool> HasSavedPasswordAsync(Guid hostId) =>
        _vault.State == VaultState.Unlocked && await _vault.GetAsync(SecretKeys.HostPassword(hostId)) is not null;

    public async Task<ConnectionTarget> BuildTargetAsync(HostProfile host, string? passwordOverride = null)
    {
        string? password = passwordOverride;
        string? privateKey = null;
        string? passphrase = null;
        string? certificate = null;

        if (host.AuthMethod == AuthMethod.PrivateKey && host.KeyId is { } keyId)
        {
            privateKey = await _vault.GetAsync(SecretKeys.KeyMaterial(keyId));
            passphrase = await _vault.GetAsync(SecretKeys.KeyPassphrase(keyId));
            certificate = FindKey(keyId)?.Certificate;
        }
        else if (password is null && _vault.State == VaultState.Unlocked)
        {
            password = await _vault.GetAsync(SecretKeys.HostPassword(host.Id));
        }

        return new ConnectionTarget
        {
            UseAgent = host.AuthMethod == AuthMethod.Agent,
            AgentKeyFingerprint = host.AuthMethod == AuthMethod.Agent ? host.AgentKeyFingerprint : null,
            Address = host.Address,
            Port = host.Port,
            Username = host.Username,
            Password = password,
            PrivateKey = privateKey,
            Passphrase = passphrase,
            Certificate = certificate,
            HostId = host.Id,
            DisplayName = host.DisplayName,
        };
    }

    /// <summary>True when following the jump chain from <paramref name="host"/> reaches it again.</summary>
    public bool CreatesJumpLoop(HostProfile host)
    {
        var visited = new HashSet<Guid> { host.Id };
        var next = host.JumpHostId;
        while (next is { } id)
        {
            if (!visited.Add(id))
                return true;
            next = FindHost(id)?.JumpHostId;
        }

        return false;
    }

    private bool IsDescendantOrSelf(Guid candidate, Guid ancestor)
    {
        Guid? current = candidate;
        var guard = 0;
        while (current is { } id && guard++ < 256)
        {
            if (id == ancestor)
                return true;
            current = _data.Groups.FirstOrDefault(g => g.Id == id)?.ParentId;
        }

        return false;
    }

    private static void Upsert<T>(List<T> list, T item, Predicate<T> match)
    {
        var index = list.FindIndex(match);
        if (index >= 0)
            list[index] = item;
        else
            list.Add(item);
    }

    private async Task PersistAsync()
    {
        await _repository.SaveAsync(_data);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
