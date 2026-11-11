using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;

namespace Sshelfy.Core.Tests;

internal sealed class InMemoryRepository : IHostRepository
{
    public HostCatalogData Data { get; private set; } = new();

    public Task<HostCatalogData> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Data);

    public Task SaveAsync(HostCatalogData data, CancellationToken cancellationToken = default)
    {
        Data = data;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryVault : ISecretVault
{
    private readonly Dictionary<string, string> _values = new();

    public VaultState State => VaultState.Unlocked;
    public bool CanRememberOnDevice => false;
    public bool IsRememberedOnDevice => false;
    public event EventHandler? StateChanged { add { } remove { } }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CreateAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> UnlockAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void Lock() { }
    public void ForgetDevice() { }
    public Task<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default) =>
        Task.FromResult(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(purpose)));
    public Task<bool> RememberOnDeviceAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        if (value is null)
            _values.Remove(key);
        else
            _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        foreach (var k in _values.Keys.Where(k => k.StartsWith(prefix)).ToList())
            _values.Remove(k);
        return Task.CompletedTask;
    }
}
