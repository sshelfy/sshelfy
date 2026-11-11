using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Backup;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;
using Sshelfy.Core.Snippets;
using Sshelfy.Infrastructure.Ssh;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Core.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sshelfy-backup-" + Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string PrivateKey => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test_ed25519"));

    /// <summary>One installation: catalog, vault, snippets and settings, all in memory.</summary>
    private sealed class Installation
    {
        public InMemoryVault Vault { get; } = new();

        public HostCatalog Catalog { get; }

        public SnippetLibrary Snippets { get; } = new(new MemorySnippets());

        public FakeSettings Settings { get; } = new();

        public BackupService Backup { get; }

        public Installation()
        {
            Catalog = new HostCatalog(new InMemoryRepository(), Vault, new SshKeyInspector());
            Backup = new BackupService(Catalog, Vault, Snippets, Settings);
        }
    }

    private static async Task<Installation> SampleAsync()
    {
        var source = new Installation();
        var group = new HostGroup { Name = "prod" };
        var sub = new HostGroup { Name = "eu", ParentId = group.Id };
        await source.Catalog.SaveGroupAsync(group);
        await source.Catalog.SaveGroupAsync(sub);
        var key = await source.Catalog.ImportKeyAsync("deploy", PrivateKey, null);
        var bastion = new HostProfile { Name = "bastion", Address = "10.0.0.1", Username = "ops", GroupId = group.Id };
        var web = new HostProfile { Name = "web", Address = "10.0.0.2", Username = "deploy", GroupId = sub.Id, AuthMethod = AuthMethod.PrivateKey, KeyId = key.Id, JumpHostId = bastion.Id };
        await source.Catalog.SaveHostAsync(bastion, new SecretUpdate("bastion-pw"));
        await source.Catalog.SaveHostAsync(web);
        await source.Snippets.SaveAsync(new Snippet { Name = "disk", Command = "df -h" });
        source.Settings.Update(s => s with { TerminalFontSize = 15, LastLocalDirectory = @"C:\source-only" });
        return source;
    }

    [Fact]
    public async Task Restored_settings_cannot_approve_commands_or_change_the_editor()
    {
        var source = new Installation();
        var host = new HostProfile { Name = "evil", Address = "10.0.0.9", Username = "x", PreConnectCommand = "calc.exe", ForwardAgent = true };
        await source.Catalog.SaveHostAsync(host);
        source.Settings.Update(s => s with { ApprovedPreConnectCommands = ["APPROVED-BY-FILE"], ExternalEditor = "evil.exe", X11Display = "evil:0", TerminalFontSize = 17 });
        var content = await source.Backup.CreateAsync(includeSecrets: false, includeSettings: true);

        var target = new Installation();
        target.Settings.Update(s => s with { ExternalEditor = "code" });
        var plan = target.Backup.Plan(content);
        plan.RestoreSettings = true;
        await target.Backup.ApplyAsync(plan, withSecrets: false);

        Assert.Equal(17, target.Settings.Current.TerminalFontSize); // ordinary settings come along
        Assert.Empty(target.Settings.Current.ApprovedPreConnectCommands);
        Assert.Equal("code", target.Settings.Current.ExternalEditor);
        Assert.Equal("localhost:0.0", target.Settings.Current.X11Display);

        var preview = Assert.Single(plan.Items, i => i.Kind == ImportItemKind.Host);
        Assert.Contains("runs a local command before connecting", preview.Detail);
        Assert.Contains("forwards your SSH agent", preview.Detail);
    }

    [Fact]
    public async Task Export_and_import_into_an_empty_installation_restores_everything()
    {
        var source = await SampleAsync();
        var path = Path.Combine(_dir, "backup.sshelfy");
        await new EncryptedBackupFile().WriteAsync(path, await source.Backup.CreateAsync(includeSecrets: true, includeSettings: true), "export-pass");

        Assert.DoesNotContain("bastion-pw", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        var target = new Installation();
        target.Settings.Update(s => s with { LastLocalDirectory = @"D:\mine" });
        var content = await new EncryptedBackupFile().ReadAsync(path, "export-pass");
        var plan = target.Backup.Plan(content);
        Assert.All(plan.Items, i => Assert.Equal(ImportItemState.New, i.State));
        plan.RestoreSettings = true;

        var result = await target.Backup.ApplyAsync(plan, withSecrets: true);

        Assert.Equal(6, result.Imported); // 2 groups, 1 key, 2 hosts, 1 snippet
        var bastion = target.Catalog.Hosts.Single(h => h.Name == "bastion");
        var web = target.Catalog.Hosts.Single(h => h.Name == "web");
        Assert.Equal(bastion.Id, web.JumpHostId);
        Assert.Equal("eu", target.Catalog.Groups.Single(g => g.Id == web.GroupId).Name);
        Assert.Equal("prod", target.Catalog.Groups.Single(g => g.Id == target.Catalog.Groups.Single(x => x.Name == "eu").ParentId).Name);
        Assert.Equal(PrivateKey, await target.Vault.GetAsync(SecretKeys.KeyMaterial(web.KeyId!.Value)));
        Assert.Equal("bastion-pw", await target.Vault.GetAsync(SecretKeys.HostPassword(bastion.Id)));
        Assert.Equal("df -h", target.Snippets.Snippets.Single().Command);
        Assert.Equal(15, target.Settings.Current.TerminalFontSize);
        Assert.Equal(@"D:\mine", target.Settings.Current.LastLocalDirectory); // machine paths are kept
    }

    [Fact]
    public async Task Wrong_password_and_foreign_files_are_reported()
    {
        var source = await SampleAsync();
        var path = Path.Combine(_dir, "backup.sshelfy");
        await new EncryptedBackupFile().WriteAsync(path, await source.Backup.CreateAsync(true, false), "right");

        await Assert.ThrowsAsync<BackupPasswordException>(() => new EncryptedBackupFile().ReadAsync(path, "wrong"));

        var other = Path.Combine(_dir, "notes.json");
        await File.WriteAllTextAsync(other, "{\"hello\":1}");
        await Assert.ThrowsAsync<BackupFormatException>(() => new EncryptedBackupFile().ReadAsync(other, "right"));
    }

    [Fact]
    public async Task Importing_into_the_same_installation_finds_identical_items_and_resolves_conflicts()
    {
        var installation = await SampleAsync();
        var content = await installation.Backup.CreateAsync(true, false);

        // Change a host and a snippet after the backup was made.
        var web = installation.Catalog.Hosts.Single(h => h.Name == "web");
        await installation.Catalog.SaveHostAsync(web with { Name = "web (renamed)" });
        var snippet = installation.Snippets.Snippets.Single();
        await installation.Snippets.SaveAsync(snippet with { Command = "df -h /" });

        var plan = installation.Backup.Plan(content);
        Assert.Equal(ImportItemState.Identical, plan.Items.Single(i => i.Name == "bastion").State);
        Assert.Equal(ImportItemState.Identical, plan.Items.Single(i => i.Kind == ImportItemKind.Key).State);
        var hostConflict = plan.Items.Single(i => i.Kind == ImportItemKind.Host && i.State == ImportItemState.Conflict);
        var snippetConflict = plan.Items.Single(i => i.Kind == ImportItemKind.Snippet);
        Assert.Equal(ImportItemState.Conflict, snippetConflict.State);
        Assert.Equal(ImportChoice.Skip, hostConflict.Choice);

        hostConflict.Choice = ImportChoice.KeepBoth;
        snippetConflict.Choice = ImportChoice.Replace;
        var result = await installation.Backup.ApplyAsync(plan, withSecrets: true);

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Replaced);
        Assert.Equal(3, installation.Catalog.Hosts.Count);
        var copy = installation.Catalog.Hosts.Single(h => h.Name == "web (imported)");
        Assert.NotEqual(web.Id, copy.Id);
        Assert.Equal(web.KeyId, copy.KeyId); // reuses the existing key with the same fingerprint
        Assert.Equal("df -h", installation.Snippets.Snippets.Single().Command);
        Assert.Single(installation.Catalog.Keys);
    }

    [Fact]
    public async Task Without_secrets_hosts_come_without_passwords_and_key_hosts_fall_back_to_password()
    {
        var source = await SampleAsync();
        var content = await source.Backup.CreateAsync(includeSecrets: false, includeSettings: false);
        Assert.False(content.IncludesSecrets);
        Assert.Empty(content.Keys);

        var target = new Installation();
        var result = await target.Backup.ApplyAsync(target.Backup.Plan(content), withSecrets: true);

        Assert.False(result.SecretsSkipped);
        var web = target.Catalog.Hosts.Single(h => h.Name == "web");
        Assert.Null(web.KeyId);
        Assert.Equal(AuthMethod.Password, web.AuthMethod);
        Assert.Null(await target.Vault.GetAsync(SecretKeys.HostPassword(target.Catalog.Hosts.Single(h => h.Name == "bastion").Id)));
    }

    private sealed class MemorySnippets : ISnippetRepository
    {
        private List<Snippet> _items = [];

        public Task<List<Snippet>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_items.ToList());

        public Task SaveAsync(IReadOnlyList<Snippet> snippets, CancellationToken cancellationToken = default)
        {
            _items = snippets.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();

        public event EventHandler<AppSettings>? Changed;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Update(Func<AppSettings, AppSettings> mutate)
        {
            Current = mutate(Current);
            Changed?.Invoke(this, Current);
        }

        public Task FlushAsync() => Task.CompletedTask;
    }
}
