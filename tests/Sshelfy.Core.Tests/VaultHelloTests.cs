using System.Security.Cryptography;
using Sshelfy.Core.Security;
using Sshelfy.Infrastructure.Security;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Core.Tests;

public sealed class VaultHelloTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sshelfy-hello-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SecretVault NewVault(FakeHello hello, bool deviceKeys = false) =>
        new(new StoragePaths(_dir), new XorProtector(deviceKeys), hello);

    private async Task<SecretVault> CreatedVaultAsync(FakeHello hello, bool deviceKeys = false)
    {
        var vault = NewVault(hello, deviceKeys);
        await vault.InitializeAsync();
        await vault.CreateAsync("master", rememberOnDevice: false);
        await vault.SetAsync("host/1/password", "s3cret");
        return vault;
    }

    [Fact]
    public async Task Hello_unlocks_after_a_restart_without_the_master_password()
    {
        var hello = new FakeHello();
        var vault = await CreatedVaultAsync(hello);

        Assert.Equal(HelloStatus.Success, await vault.EnableHelloAsync());
        Assert.True(vault.IsHelloEnabled);

        var reopened = NewVault(hello);
        await reopened.InitializeAsync();
        Assert.Equal(VaultState.Locked, reopened.State);
        Assert.Equal(HelloStatus.Success, await reopened.UnlockWithHelloAsync());
        Assert.Equal("s3cret", await reopened.GetAsync("host/1/password"));
    }

    [Fact]
    public async Task Cancelling_the_prompt_keeps_the_vault_locked_and_hello_on()
    {
        var hello = new FakeHello();
        var vault = await CreatedVaultAsync(hello);
        await vault.EnableHelloAsync();
        vault.Lock();

        hello.Next = HelloStatus.Cancelled;

        Assert.Equal(HelloStatus.Cancelled, await vault.UnlockWithHelloAsync());
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.True(vault.IsHelloEnabled);
        Assert.True(await vault.UnlockAsync("master", rememberOnDevice: false));
    }

    [Fact]
    public async Task A_new_hello_key_turns_hello_off_and_the_password_still_works()
    {
        var hello = new FakeHello();
        var vault = await CreatedVaultAsync(hello);
        await vault.EnableHelloAsync();
        vault.Lock();

        hello.Rotate(); // e.g. the PIN was reset: same API, different key

        Assert.Equal(HelloStatus.KeyMissing, await vault.UnlockWithHelloAsync());
        Assert.False(vault.IsHelloEnabled);
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.True(await vault.UnlockAsync("master", rememberOnDevice: false));
        Assert.Equal("s3cret", await vault.GetAsync("host/1/password"));
    }

    [Fact]
    public async Task Missing_hello_key_turns_hello_off()
    {
        var hello = new FakeHello();
        var vault = await CreatedVaultAsync(hello);
        await vault.EnableHelloAsync();
        vault.Lock();

        hello.Next = HelloStatus.KeyMissing;

        Assert.Equal(HelloStatus.KeyMissing, await vault.UnlockWithHelloAsync());
        Assert.False(vault.IsHelloEnabled);
        Assert.True(hello.Deleted);
    }

    [Fact]
    public async Task Hello_and_automatic_unlock_exclude_each_other()
    {
        var hello = new FakeHello();
        var vault = await CreatedVaultAsync(hello, deviceKeys: true);
        Assert.True(await vault.RememberOnDeviceAsync());
        Assert.True(vault.IsRememberedOnDevice);

        await vault.EnableHelloAsync();
        Assert.True(vault.IsHelloEnabled);
        Assert.False(vault.IsRememberedOnDevice);

        Assert.True(await vault.RememberOnDeviceAsync());
        Assert.False(vault.IsHelloEnabled);
    }

    [Fact]
    public async Task Enabling_needs_an_unlocked_vault_and_available_hello()
    {
        var hello = new FakeHello { Available = false };
        var vault = await CreatedVaultAsync(hello);

        Assert.Equal(HelloStatus.Unavailable, await vault.EnableHelloAsync());

        vault.Lock();
        await Assert.ThrowsAsync<VaultLockedException>(() => vault.EnableHelloAsync());
    }

    /// <summary>Deterministic "signature": HMAC with a per-key secret, like RSA PKCS#1 signing with a fixed key.</summary>
    private sealed class FakeHello : IHelloSigner
    {
        private byte[] _secret = RandomNumberGenerator.GetBytes(32);

        public bool Available { get; set; } = true;

        public HelloStatus Next { get; set; } = HelloStatus.Success;

        public bool Deleted { get; private set; }

        public void Rotate() => _secret = RandomNumberGenerator.GetBytes(32);

        public Task<bool> IsAvailableAsync() => Task.FromResult(Available);

        public Task<HelloStatus> CreateKeyAsync() => Task.FromResult(HelloStatus.Success);

        public Task<HelloSignature> SignAsync(byte[] data) =>
            Task.FromResult(Next == HelloStatus.Success ? new HelloSignature(HelloStatus.Success, HMACSHA256.HashData(_secret, data)) : new HelloSignature(Next));

        public Task DeleteKeyAsync()
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }

    private sealed class XorProtector(bool supported) : IDeviceKeyProtector
    {
        public bool IsSupported => supported;

        public byte[] Protect(byte[] data, byte[] entropy) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data, byte[] entropy) => Protect(data, entropy);
    }
}
