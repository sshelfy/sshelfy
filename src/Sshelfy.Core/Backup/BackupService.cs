using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;
using Sshelfy.Core.Snippets;

namespace Sshelfy.Core.Backup;

/// <summary>
/// Builds backups of hosts, groups, keys, snippets and settings, and imports them: a plan lists
/// every item as new, identical or conflicting so the user can choose, then it is applied with ids
/// remapped where items were imported as copies.
/// </summary>
public sealed class BackupService
{
    private readonly IHostCatalog _catalog;
    private readonly ISecretVault _vault;
    private readonly ISnippetLibrary _snippets;
    private readonly ISettingsService _settings;

    public BackupService(IHostCatalog catalog, ISecretVault vault, ISnippetLibrary snippets, ISettingsService settings)
    {
        _catalog = catalog;
        _vault = vault;
        _snippets = snippets;
        _settings = settings;
    }

    /// <param name="includeSecrets">Passwords and private keys; needs the vault unlocked.</param>
    public async Task<BackupContent> CreateAsync(bool includeSecrets, bool includeSettings)
    {
        if (includeSecrets && _vault.State == VaultState.Locked)
            throw new VaultLockedException();
        var secrets = includeSecrets && _vault.State == VaultState.Unlocked;

        var hosts = new List<BackupHost>();
        foreach (var host in _catalog.Hosts)
            hosts.Add(new BackupHost(host, secrets ? await _vault.GetAsync(SecretKeys.HostPassword(host.Id)) : null));

        var keys = new List<BackupKey>();
        if (secrets)
        {
            foreach (var key in _catalog.Keys)
            {
                if (await _vault.GetAsync(SecretKeys.KeyMaterial(key.Id)) is { } material)
                    keys.Add(new BackupKey(key, material, await _vault.GetAsync(SecretKeys.KeyPassphrase(key.Id))));
            }
        }

        return new BackupContent
        {
            MachineName = Environment.MachineName,
            IncludesSecrets = secrets,
            Groups = [.. _catalog.Groups],
            Hosts = hosts,
            Keys = keys,
            Snippets = [.. _snippets.Snippets],
            Settings = includeSettings ? _settings.Current : null,
        };
    }

    public ImportPlan Plan(BackupContent content)
    {
        var items = new List<ImportItem>();

        foreach (var group in content.Groups)
        {
            var existing = _catalog.Groups.FirstOrDefault(g => g.Id == group.Id)
                           ?? _catalog.Groups.FirstOrDefault(g => g.Name == group.Name && g.ParentId == group.ParentId);
            items.Add(Item(ImportItemKind.Group, group.Name, null, existing is null ? ImportItemState.New : ImportItemState.Identical, existing?.Id, group));
        }

        foreach (var key in content.Keys)
        {
            // Keys are identified by their fingerprint: the same key is never imported twice.
            var existing = _catalog.Keys.FirstOrDefault(k => k.Fingerprint == key.Profile.Fingerprint);
            items.Add(Item(ImportItemKind.Key, key.Profile.Name, key.Profile.Fingerprint, existing is null ? ImportItemState.New : ImportItemState.Identical, existing?.Id, key));
        }

        foreach (var entry in content.Hosts)
        {
            var host = entry.Host;
            var existing = _catalog.FindHost(host.Id)
                           ?? _catalog.Hosts.FirstOrDefault(h => h.Address == host.Address && h.Port == host.Port && h.Username == host.Username);
            var state = existing is null ? ImportItemState.New
                : SameHost(existing, host) ? ImportItemState.Identical
                : ImportItemState.Conflict;
            var risks = Risks(host);
            var detail = risks.Count == 0 ? host.Endpoint : $"{host.Endpoint}  ·  ⚠ {string.Join(", ", risks)}";
            items.Add(Item(ImportItemKind.Host, host.DisplayName, detail, state, existing?.Id, entry));
        }

        foreach (var snippet in content.Snippets)
        {
            var existing = _snippets.Snippets.FirstOrDefault(s => s.Id == snippet.Id)
                           ?? _snippets.Snippets.FirstOrDefault(s => s.Name == snippet.Name);
            var state = existing is null ? ImportItemState.New
                : existing.Name == snippet.Name && existing.Command == snippet.Command && existing.RunImmediately == snippet.RunImmediately ? ImportItemState.Identical
                : ImportItemState.Conflict;
            items.Add(Item(ImportItemKind.Snippet, snippet.Name, snippet.Command.Split('\n')[0], state, existing?.Id, snippet));
        }

        return new ImportPlan { Content = content, Items = items };
    }

    /// <param name="withSecrets">Write passwords and keys (the vault must be unlocked); otherwise only metadata is imported.</param>
    public async Task<ImportResult> ApplyAsync(ImportPlan plan, bool withSecrets)
    {
        if (withSecrets && _vault.State != VaultState.Unlocked)
            throw new VaultLockedException();
        var secrets = withSecrets && plan.Content.IncludesSecrets;
        int imported = 0, replaced = 0, skipped = 0;

        // Groups first, parents before children, so hosts can be placed into them.
        var groupIds = new Dictionary<Guid, Guid>();
        var groups = plan.Items.Where(i => i.Kind == ImportItemKind.Group).ToList();
        foreach (var item in OrderParentsFirst(groups))
        {
            var group = (HostGroup)item.Source;
            if (item.ExistingId is { } existing)
            {
                groupIds[group.Id] = existing;
                continue;
            }

            if (item.Choice == ImportChoice.Skip)
            {
                skipped++;
                continue;
            }

            var id = _catalog.Groups.Any(g => g.Id == group.Id) ? Guid.NewGuid() : group.Id;
            var parent = group.ParentId is { } p && groupIds.TryGetValue(p, out var mapped) ? mapped : (Guid?)null;
            await _catalog.SaveGroupAsync(group with { Id = id, ParentId = parent });
            groupIds[group.Id] = id;
            imported++;
        }

        // Keys need the vault; without secrets hosts lose their key reference.
        var keyIds = new Dictionary<Guid, Guid>();
        foreach (var item in plan.Items.Where(i => i.Kind == ImportItemKind.Key))
        {
            var key = (BackupKey)item.Source;
            if (item.ExistingId is { } existing)
            {
                keyIds[key.Profile.Id] = existing;
                continue;
            }

            if (!secrets || item.Choice == ImportChoice.Skip)
            {
                skipped++;
                continue;
            }

            var created = await _catalog.ImportKeyAsync(key.Profile.Name, key.PrivateKey, key.Passphrase);
            keyIds[key.Profile.Id] = created.Id;
            imported++;
        }

        // Hosts: decide ids first so jump host references can be remapped in a second pass.
        var hostIds = new Dictionary<Guid, Guid>();
        var toSave = new List<(HostProfile Host, string? Password)>();
        foreach (var item in plan.Items.Where(i => i.Kind == ImportItemKind.Host))
        {
            var entry = (BackupHost)item.Source;
            var host = entry.Host;
            switch (item.State, item.Choice)
            {
                case (ImportItemState.Identical, _):
                case (_, ImportChoice.Skip):
                    if (item.ExistingId is { } kept)
                        hostIds[host.Id] = kept;
                    skipped++;
                    continue;
                case (ImportItemState.Conflict, ImportChoice.Replace):
                    hostIds[host.Id] = item.ExistingId!.Value;
                    toSave.Add((host with { Id = item.ExistingId.Value }, entry.Password));
                    replaced++;
                    break;
                case (ImportItemState.Conflict, ImportChoice.KeepBoth):
                    var copy = Guid.NewGuid();
                    hostIds[host.Id] = copy;
                    toSave.Add((host with { Id = copy, Name = host.DisplayName + " (imported)" }, entry.Password));
                    imported++;
                    break;
                default:
                    hostIds[host.Id] = host.Id;
                    toSave.Add((host, entry.Password));
                    imported++;
                    break;
            }
        }

        foreach (var (host, password) in toSave)
        {
            var remapped = host with
            {
                GroupId = host.GroupId is { } g && groupIds.TryGetValue(g, out var group) ? group : null,
                KeyId = host.KeyId is { } k && keyIds.TryGetValue(k, out var key) ? key : null,
                JumpHostId = host.JumpHostId is { } j && hostIds.TryGetValue(j, out var jump) ? jump : null,
            };

            // A host whose key was not imported falls back to password authentication.
            if (remapped.AuthMethod == AuthMethod.PrivateKey && remapped.KeyId is null)
                remapped = remapped with { AuthMethod = AuthMethod.Password };

            var update = secrets && password is not null ? new SecretUpdate(password) : (SecretUpdate?)null;
            await _catalog.SaveHostAsync(remapped, update);
        }

        foreach (var item in plan.Items.Where(i => i.Kind == ImportItemKind.Snippet))
        {
            var snippet = (Snippet)item.Source;
            switch (item.State, item.Choice)
            {
                case (ImportItemState.Identical, _):
                case (_, ImportChoice.Skip):
                    skipped++;
                    break;
                case (ImportItemState.Conflict, ImportChoice.Replace):
                    await _snippets.SaveAsync(snippet with { Id = item.ExistingId!.Value });
                    replaced++;
                    break;
                case (ImportItemState.Conflict, ImportChoice.KeepBoth):
                    await _snippets.SaveAsync(snippet with { Id = Guid.NewGuid(), Name = snippet.Name + " (imported)" });
                    imported++;
                    break;
                default:
                    await _snippets.SaveAsync(snippet);
                    imported++;
                    break;
            }
        }

        if (plan.RestoreSettings && plan.Content.Settings is { } restored)
        {
            // Paths and session state belong to this machine. Settings that start programs or vouch for
            // them are never taken from a file: a backup could otherwise approve its own pre-connect
            // commands or make "Edit in external editor" run something else.
            _settings.Update(current => restored with
            {
                SessionLogDirectory = current.SessionLogDirectory,
                LastLocalDirectory = current.LastLocalDirectory,
                LastSessionTabs = current.LastSessionTabs,
                ApprovedPreConnectCommands = current.ApprovedPreConnectCommands,
                ExternalEditor = current.ExternalEditor,
                X11Display = current.X11Display,
            });
        }

        return new ImportResult(imported, replaced, skipped, SecretsSkipped: plan.Content.IncludesSecrets && !secrets);
    }

    /// <summary>Host settings that act on this computer or expose it, shown in the import preview so a shared file cannot hide them.</summary>
    internal static List<string> Risks(HostProfile host)
    {
        var risks = new List<string>();
        if (!string.IsNullOrWhiteSpace(host.PreConnectCommand))
            risks.Add("runs a local command before connecting");
        if (host.ForwardAgent)
            risks.Add("forwards your SSH agent");
        if (host.ForwardX11)
            risks.Add("forwards X11");
        if (host.PortForwards.Any(r => r.AutoStart && r.Kind != Ssh.ForwardKind.Remote && r.BindAddress is not ("127.0.0.1" or "localhost" or "::1")))
            risks.Add("opens a tunnel reachable from your network");
        if (!string.IsNullOrWhiteSpace(host.StartupCommand))
            risks.Add("runs a startup command on the server");
        return risks;
    }

    private static ImportItem Item(ImportItemKind kind, string name, string? detail, ImportItemState state, Guid? existing, object source) => new()
    {
        Kind = kind,
        Name = name,
        Detail = detail,
        State = state,
        ExistingId = existing,
        Source = source,
        Choice = state == ImportItemState.New ? ImportChoice.Import : ImportChoice.Skip,
    };

    private static bool SameHost(HostProfile a, HostProfile b) =>
        a.Name == b.Name && a.Address == b.Address && a.Port == b.Port && a.Username == b.Username && a.AuthMethod == b.AuthMethod;

    private static IEnumerable<ImportItem> OrderParentsFirst(List<ImportItem> groups)
    {
        var done = new HashSet<Guid>();
        var pending = new List<ImportItem>(groups);
        while (pending.Count > 0)
        {
            var ready = pending.Where(i => ((HostGroup)i.Source).ParentId is not { } p || done.Contains(p) || groups.All(g => ((HostGroup)g.Source).Id != p)).ToList();
            if (ready.Count == 0)
                ready = pending; // a cycle in a hand-edited file: import the rest flat
            foreach (var item in ready)
            {
                done.Add(((HostGroup)item.Source).Id);
                pending.Remove(item);
                yield return item;
            }
        }
    }
}
