using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Import;
using Sshelfy.Core.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class SshConfigParserTests
{
    private const string Home = "/home/me";

    private static SshConfigParseResult Parse(string text, Func<string, IEnumerable<string>>? include = null) =>
        SshConfigParser.Parse(text, include, Home, "me");

    [Fact]
    public void Reads_basic_host_blocks()
    {
        var result = Parse("""
            Host web
                HostName 10.0.0.5
                User deploy
                Port 2200
                IdentityFile ~/.ssh/id_ed25519
            """);

        var web = Assert.Single(result.Hosts);
        Assert.Equal(("web", "10.0.0.5", 2200, "deploy"), (web.Alias, web.HostName, web.Port, web.User));
        Assert.Equal(["/home/me/.ssh/id_ed25519"], web.IdentityFiles);
    }

    [Fact]
    public void First_value_wins_and_wildcards_provide_defaults()
    {
        var result = Parse("""
            Host db
                User postgres
            Host *
                User fallback
                Port 2222
                IdentityFile ~/.ssh/shared
            Host db
                Port 9999
            """);

        var db = Assert.Single(result.Hosts);
        Assert.Equal("postgres", db.User);   // set before Host *
        Assert.Equal(2222, db.Port);         // first Port seen comes from Host *
        Assert.Equal("db", db.HostName);     // defaults to the alias
        Assert.Equal(["/home/me/.ssh/shared"], db.IdentityFiles);
    }

    [Fact]
    public void Multiple_aliases_negation_and_equals_syntax()
    {
        var result = Parse("""
            Host app1 app2 *.internal
                User=ops
            Host * !app2
                Port = 2022
            """);

        Assert.Equal(["app1", "app2"], result.Hosts.Select(h => h.Alias));
        Assert.Equal(2022, result.Hosts[0].Port);
        Assert.Equal(22, result.Hosts[1].Port);
        Assert.All(result.Hosts, h => Assert.Equal("ops", h.User));
    }

    [Fact]
    public void Hostname_token_and_proxy_jump()
    {
        var result = Parse("""
            Host inner
                HostName %h.corp.example
                ProxyJump bastion
            Host bastion
                HostName 203.0.113.10
            """);

        var inner = result.Hosts.Single(h => h.Alias == "inner");
        Assert.Equal("inner.corp.example", inner.HostName);
        Assert.Equal("bastion", inner.ProxyJump);
    }

    [Fact]
    public void Include_is_expanded_in_place()
    {
        var result = Parse("Include config.d/*\nHost main\n  User root", pattern =>
            pattern == "config.d/*" ? ["Host extra\n  HostName extra.example"] : []);

        Assert.Equal(["extra", "main"], result.Hosts.Select(h => h.Alias));
    }

    [Fact]
    public void Match_and_proxy_command_produce_warnings()
    {
        var result = Parse("""
            Match host foo
                User x
            Host legacy
                ProxyCommand ssh -W %h:%p jump
            """);

        Assert.Contains(result.Warnings, w => w.Contains("Match"));
        Assert.Contains(result.Warnings, w => w.Contains("ProxyCommand"));
        Assert.Equal(["legacy"], result.Hosts.Select(h => h.Alias));
    }

    [Fact]
    public void Known_hosts_parses_ports_and_skips_hashed_entries()
    {
        // Public key blob of tests/Fixtures/test_ed25519.pub.
        var key = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test_ed25519.pub")).Split(' ')[1];
        var text = $"""
            github.com,140.82.121.4 ssh-ed25519 {key}
            [bastion.example]:2222 ssh-ed25519 {key} comment
            |1|abc=|def= ssh-ed25519 {key}
            @cert-authority *.example ssh-ed25519 {key}
            """;

        var entries = KnownHostsParser.Parse(text, out var skipped);

        Assert.Equal(2, skipped);
        Assert.Equal(["github.com", "140.82.121.4", "bastion.example"], entries.Select(e => e.Host));
        Assert.Equal(2222, entries[2].Port);
        Assert.All(entries, e => Assert.Equal("SHA256:cSUiYh3kThi6xbKeuWnRG4/P8W80a+pOH3J5jeKWa7A", e.Fingerprint));
    }
}

public sealed class KnownHostsVerifierTests
{
    private readonly MemoryStore _store = new();
    private readonly RecordingPrompt _prompt = new();

    private KnownHostsVerifier Verifier => new(_store, _prompt);

    [Fact]
    public async Task Unknown_host_is_prompted_and_remembered()
    {
        Assert.True(await Verifier.VerifyAsync("h", 22, "ssh-ed25519", "SHA256:a"));
        Assert.Equal(HostKeyStatus.Unknown, _prompt.Last!.Status);
        Assert.True(await Verifier.VerifyAsync("h", 22, "ssh-ed25519", "SHA256:a"));
        Assert.Equal(1, _prompt.Count);
    }

    [Fact]
    public async Task Different_key_of_same_algorithm_is_a_change_and_replaces_old_keys()
    {
        await _store.AddAsync(new KnownHost("h", 22, "ssh-ed25519", "SHA256:old", DateTimeOffset.UtcNow));
        await _store.AddAsync(new KnownHost("h", 22, "ssh-rsa", "SHA256:rsa", DateTimeOffset.UtcNow));

        Assert.True(await Verifier.VerifyAsync("h", 22, "ssh-ed25519", "SHA256:new"));

        Assert.Equal(HostKeyStatus.Changed, _prompt.Last!.Status);
        Assert.Equal("SHA256:old", _prompt.Last.PreviousFingerprint);
        Assert.Equal(["SHA256:new"], (await _store.FindAsync("h", 22)).Select(k => k.Fingerprint));
    }

    [Fact]
    public async Task Key_of_another_algorithm_is_flagged_and_added()
    {
        await _store.AddAsync(new KnownHost("h", 22, "ssh-rsa", "SHA256:rsa", DateTimeOffset.UtcNow));

        Assert.True(await Verifier.VerifyAsync("h", 22, "ssh-ed25519", "SHA256:ed"));

        // Not a plain first connection: a man in the middle could offer a type it can forge.
        Assert.Equal(HostKeyStatus.OtherTypeKnown, _prompt.Last!.Status);
        Assert.Equal("ssh-rsa SHA256:rsa", _prompt.Last.PreviousFingerprint);
        Assert.Equal(2, (await _store.FindAsync("h", 22)).Count);
    }

    private sealed class RecordingPrompt : IHostKeyPrompt
    {
        public HostKeyChallenge? Last { get; private set; }

        public int Count { get; private set; }

        public Task<bool> ConfirmAsync(HostKeyChallenge challenge)
        {
            Last = challenge;
            Count++;
            return Task.FromResult(true);
        }
    }

    private sealed class MemoryStore : IKnownHostsStore
    {
        private readonly List<KnownHost> _hosts = [];

        public Task<IReadOnlyList<KnownHost>> FindAsync(string host, int port, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnownHost>>(_hosts.Where(h => h.Host == host && h.Port == port).ToList());

        public Task AddAsync(KnownHost knownHost, CancellationToken cancellationToken = default)
        {
            _hosts.RemoveAll(h => h.Host == knownHost.Host && h.Port == knownHost.Port && h.Algorithm == knownHost.Algorithm);
            _hosts.Add(knownHost);
            return Task.CompletedTask;
        }

        public Task ReplaceAllAsync(KnownHost knownHost, CancellationToken cancellationToken = default)
        {
            _hosts.RemoveAll(h => h.Host == knownHost.Host && h.Port == knownHost.Port);
            _hosts.Add(knownHost);
            return Task.CompletedTask;
        }
    }
}
