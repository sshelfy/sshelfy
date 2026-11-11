using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;

namespace Sshelfy.Core.Tests;

public sealed class HostCatalogTests
{
    private readonly InMemoryRepository _repository = new();
    private readonly InMemoryVault _vault = new();
    private readonly HostCatalog _catalog;

    public HostCatalogTests()
    {
        _catalog = new HostCatalog(_repository, _vault, new FakeInspector());
    }

    [Fact]
    public async Task Saving_host_with_password_stores_secret_in_vault_only()
    {
        var host = new HostProfile { Name = "web", Address = "web.local", Username = "root" };

        await _catalog.SaveHostAsync(host, new SecretUpdate("pw"));

        Assert.Single(_repository.Data.Hosts);
        Assert.Equal("pw", await _vault.GetAsync(SecretKeys.HostPassword(host.Id)));
        var target = await _catalog.BuildTargetAsync(host);
        Assert.Equal("pw", target.Password);
    }

    [Fact]
    public async Task Deleting_group_moves_children_to_parent()
    {
        var parent = new HostGroup { Name = "prod" };
        var child = new HostGroup { Name = "eu", ParentId = parent.Id };
        await _catalog.SaveGroupAsync(parent);
        await _catalog.SaveGroupAsync(child);
        var host = new HostProfile { Address = "a", Username = "u", GroupId = child.Id };
        await _catalog.SaveHostAsync(host);

        await _catalog.DeleteGroupAsync(child.Id);

        Assert.Equal(parent.Id, _catalog.FindHost(host.Id)!.GroupId);
        Assert.DoesNotContain(_catalog.Groups, g => g.Id == child.Id);
    }

    [Fact]
    public async Task Group_cannot_become_its_own_descendant()
    {
        var a = new HostGroup { Name = "a" };
        var b = new HostGroup { Name = "b", ParentId = a.Id };
        await _catalog.SaveGroupAsync(a);
        await _catalog.SaveGroupAsync(b);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.SaveGroupAsync(a with { ParentId = b.Id }));
    }

    [Fact]
    public async Task Deleting_key_detaches_it_from_hosts_and_vault()
    {
        var key = await _catalog.ImportKeyAsync("laptop", "KEY", null);
        var host = new HostProfile { Address = "a", Username = "u", AuthMethod = AuthMethod.PrivateKey, KeyId = key.Id };
        await _catalog.SaveHostAsync(host);

        await _catalog.DeleteKeyAsync(key.Id);

        Assert.Null(_catalog.FindHost(host.Id)!.KeyId);
        Assert.Null(await _vault.GetAsync(SecretKeys.KeyMaterial(key.Id)));
    }

    [Fact]
    public async Task Jump_chain_that_leads_back_to_the_host_is_rejected()
    {
        var a = new HostProfile { Address = "a", Username = "u" };
        var b = new HostProfile { Address = "b", Username = "u", JumpHostId = a.Id };
        await _catalog.SaveHostAsync(a);
        await _catalog.SaveHostAsync(b);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.SaveHostAsync(a with { JumpHostId = b.Id }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.SaveHostAsync(a with { JumpHostId = a.Id }));
    }

    [Fact]
    public async Task Deleting_a_jump_host_makes_dependents_connect_directly()
    {
        var bastion = new HostProfile { Address = "bastion", Username = "u" };
        var inner = new HostProfile { Address = "inner", Username = "u", JumpHostId = bastion.Id };
        await _catalog.SaveHostAsync(bastion);
        await _catalog.SaveHostAsync(inner);

        await _catalog.DeleteHostAsync(bastion.Id);

        Assert.Null(_catalog.FindHost(inner.Id)!.JumpHostId);
    }

    [Fact]
    public async Task Invalid_key_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<SshKeyImportException>(() => _catalog.ImportKeyAsync("x", "garbage", null));
        Assert.Equal(SshKeyProblem.InvalidFormat, ex.Problem);
    }

    private sealed class FakeInspector : ISshKeyInspector
    {
        public SshKeyInfo? Inspect(string privateKey, string? passphrase, out SshKeyProblem problem)
        {
            problem = privateKey == "KEY" ? SshKeyProblem.None : SshKeyProblem.InvalidFormat;
            return problem == SshKeyProblem.None ? new SshKeyInfo("ssh-ed25519", "SHA256:test") : null;
        }
    }
}
