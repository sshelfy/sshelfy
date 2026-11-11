using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Import;
using Sshelfy.Core.Security;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class SshConfigImporterTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private readonly HostCatalog _catalog = new(new InMemoryRepository(), new InMemoryVault(), new SshKeyInspector());
    private readonly MemoryKnownHosts _knownHosts = new();
    private readonly FakeFiles _files = new();

    private SshConfigImporter Importer => new(_catalog, _knownHosts, _files, new SshKeyInspector());

    public SshConfigImporterTests()
    {
        var plain = Path.Combine(Fixtures, "test_ed25519");
        var encrypted = Path.Combine(Fixtures, "test_ed25519_encrypted");
        _files.Config = $"""
            Host bastion
                HostName 203.0.113.10
                User jump
                IdentityFile {plain}
            Host web
                HostName 10.0.0.5
                User deploy
                IdentityFile {encrypted}
                ProxyJump bastion
            Host legacy
                HostName old.example
            Host existing
                HostName 192.0.2.1
                User root
            """;
        var pub = File.ReadAllText(Path.Combine(Fixtures, "test_ed25519.pub")).Split(' ')[1];
        _files.KnownHosts = $"203.0.113.10 ssh-ed25519 {pub}\n[10.0.0.5]:22 ssh-ed25519 {pub}\n";
    }

    [Fact]
    public async Task Scan_marks_existing_hosts_and_finds_key_files()
    {
        await _catalog.SaveHostAsync(new HostProfile { Name = "old", Address = "192.0.2.1", Username = "root" });

        var scan = Importer.Scan();

        Assert.True(scan.ConfigFound);
        Assert.Equal(4, scan.Candidates.Count);
        Assert.True(scan.Candidates.Single(c => c.Source.Alias == "existing").AlreadyExists);
        Assert.NotNull(scan.Candidates.Single(c => c.Source.Alias == "web").KeyPath);
        Assert.Null(scan.Candidates.Single(c => c.Source.Alias == "legacy").KeyPath);
        Assert.Equal(2, scan.KnownHostsEntries);
    }

    [Fact]
    public async Task Import_creates_hosts_keys_jump_links_and_known_hosts()
    {
        var asked = new List<(string Path, SshKeyProblem Problem)>();
        var scan = Importer.Scan();

        var result = await Importer.ImportAsync(scan.Candidates, groupId: null, importKnownHosts: true, (path, problem) =>
        {
            asked.Add((path, problem));
            return Task.FromResult<string?>(asked.Count == 1 ? "wrong" : "test-passphrase");
        });

        Assert.Equal(4, result.HostsImported);
        Assert.Equal(2, result.KeysImported);
        Assert.Equal(2, result.KnownHostsAdded);
        Assert.Equal([SshKeyProblem.PassphraseRequired, SshKeyProblem.WrongPassphrase], asked.Select(a => a.Problem));

        var bastion = _catalog.Hosts.Single(h => h.Name == "bastion");
        var web = _catalog.Hosts.Single(h => h.Name == "web");
        var legacy = _catalog.Hosts.Single(h => h.Name == "legacy");
        Assert.Equal(AuthMethod.PrivateKey, web.AuthMethod);
        Assert.Equal(bastion.Id, web.JumpHostId);
        Assert.Equal(AuthMethod.Password, legacy.AuthMethod);
        Assert.Equal(_files.LocalUser, legacy.Username);
    }

    [Fact]
    public async Task Key_already_in_the_vault_is_reused()
    {
        var existing = await _catalog.ImportKeyAsync("laptop", File.ReadAllText(Path.Combine(Fixtures, "test_ed25519")), null);
        var bastion = Importer.Scan().Candidates.Where(c => c.Source.Alias == "bastion").ToList();

        var result = await Importer.ImportAsync(bastion, null, false, (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(0, result.KeysImported);
        Assert.Equal(existing.Id, _catalog.Hosts.Single(h => h.Name == "bastion").KeyId);
    }

    [Fact]
    public async Task Skipped_passphrase_falls_back_to_password_login()
    {
        var web = Importer.Scan().Candidates.Where(c => c.Source.Alias == "web").ToList();

        var result = await Importer.ImportAsync(web, null, false, (_, _) => Task.FromResult<string?>(null));

        Assert.Equal(AuthMethod.Password, _catalog.Hosts.Single().AuthMethod);
        Assert.Contains(result.Warnings, w => w.Contains("skipped"));
        Assert.Contains(result.Warnings, w => w.Contains("bastion"));   // jump host not imported in this run
    }

    private sealed class FakeFiles : ILocalSshFiles
    {
        public string? Config { get; set; }

        public string? KnownHosts { get; set; }

        public string HomeDirectory => "/home/me";

        public string LocalUser => "me";

        public string ConfigPath => "/home/me/.ssh/config";

        public string? ReadConfig() => Config;

        public IEnumerable<string> ResolveInclude(string pattern) => [];

        public string? ReadKnownHosts() => KnownHosts;

        public string? ReadKeyFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private sealed class MemoryKnownHosts : IKnownHostsStore
    {
        private readonly List<KnownHost> _hosts = [];

        public Task<IReadOnlyList<KnownHost>> FindAsync(string host, int port, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnownHost>>(_hosts.Where(h => h.Host == host && h.Port == port).ToList());

        public Task AddAsync(KnownHost knownHost, CancellationToken cancellationToken = default)
        {
            _hosts.Add(knownHost);
            return Task.CompletedTask;
        }

        public Task ReplaceAllAsync(KnownHost knownHost, CancellationToken cancellationToken = default) => AddAsync(knownHost, cancellationToken);
    }
}
