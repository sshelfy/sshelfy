using System.Security.Cryptography;
using Sshelfy.Core.Security;
using Sshelfy.Infrastructure.Security;
using Sshelfy.Infrastructure.Storage;

namespace Sshelfy.Core.Tests;

public sealed class SecretVaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sshelfy-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SecretVault NewVault(IDeviceKeyProtector? protector = null) =>
        new(new StoragePaths(_dir), protector ?? new FakeProtector(supported: false));

    [Fact]
    public async Task Create_then_reopen_with_correct_password_reads_values()
    {
        var vault = NewVault();
        await vault.InitializeAsync();
        Assert.Equal(VaultState.NotCreated, vault.State);

        await vault.CreateAsync("correct horse", rememberOnDevice: false);
        await vault.SetAsync("host/1/password", "s3cret");

        var reopened = NewVault();
        await reopened.InitializeAsync();
        Assert.Equal(VaultState.Locked, reopened.State);
        Assert.True(await reopened.UnlockAsync("correct horse", rememberOnDevice: false));
        Assert.Equal("s3cret", await reopened.GetAsync("host/1/password"));
    }

    [Fact]
    public async Task Wrong_password_does_not_unlock()
    {
        var vault = NewVault();
        await vault.InitializeAsync();
        await vault.CreateAsync("right", rememberOnDevice: false);

        var reopened = NewVault();
        await reopened.InitializeAsync();
        Assert.False(await reopened.UnlockAsync("wrong", rememberOnDevice: false));
        Assert.Equal(VaultState.Locked, reopened.State);
        await Assert.ThrowsAsync<VaultLockedException>(() => reopened.GetAsync("x"));
    }

    [Fact]
    public async Task Secrets_are_not_stored_in_plain_text()
    {
        var vault = NewVault();
        await vault.InitializeAsync();
        await vault.CreateAsync("pw", rememberOnDevice: false);
        await vault.SetAsync("host/1/password", "very-recognizable-value");

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "vault.json"));
        Assert.DoesNotContain("very-recognizable-value", json);
    }

    [Fact]
    public void Ciphertext_is_bound_to_its_entry_name()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var sealedValue = SecretVault.Seal(key, "host/a/password", "value");

        Assert.Equal("value", SecretVault.Open(key, "host/a/password", sealedValue));
        Assert.ThrowsAny<CryptographicException>(() => SecretVault.Open(key, "host/b/password", sealedValue));
    }

    [Fact]
    public async Task Remembered_device_key_unlocks_on_start()
    {
        var protector = new FakeProtector(supported: true);
        var vault = NewVault(protector);
        await vault.InitializeAsync();
        await vault.CreateAsync("pw", rememberOnDevice: true);
        await vault.SetAsync("k", "v");

        var reopened = NewVault(protector);
        await reopened.InitializeAsync();
        Assert.Equal(VaultState.Unlocked, reopened.State);
        Assert.Equal("v", await reopened.GetAsync("k"));

        reopened.ForgetDevice();
        var third = NewVault(protector);
        await third.InitializeAsync();
        Assert.Equal(VaultState.Locked, third.State);
    }

    [Fact]
    public async Task Remove_by_prefix_only_removes_matching_entries()
    {
        var vault = NewVault();
        await vault.InitializeAsync();
        await vault.CreateAsync("pw", rememberOnDevice: false);
        await vault.SetAsync("host/1/password", "a");
        await vault.SetAsync("host/2/password", "b");

        await vault.RemoveByPrefixAsync("host/1/");

        Assert.Null(await vault.GetAsync("host/1/password"));
        Assert.Equal("b", await vault.GetAsync("host/2/password"));
    }

    private sealed class FakeProtector(bool supported) : IDeviceKeyProtector
    {
        public bool IsSupported => supported;

        // Reversible but not identity, so tests catch accidental use of raw bytes.
        public byte[] Protect(byte[] data, byte[] entropy) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data, byte[] entropy) => Protect(data, entropy);
    }
}
