using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.Core.Abstractions;
using Sshelfy.Core.Activity;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;
using Sshelfy.Core.Settings;
using Sshelfy.Core.Sftp;
using Sshelfy.Core.Ssh;

namespace Sshelfy.App.Tests;

internal sealed class FakeVault : ISecretVault
{
    private readonly Dictionary<string, string> _values = new();

    public VaultState State { get; set; } = VaultState.Unlocked;

    public string? CreatedWith { get; private set; }

    public bool CanRememberOnDevice => false;

    public bool IsRememberedOnDevice => false;

    public event EventHandler? StateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CreateAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default)
    {
        CreatedWith = masterPassword;
        State = VaultState.Unlocked;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<bool> UnlockAsync(string masterPassword, bool rememberOnDevice, CancellationToken cancellationToken = default)
    {
        if (masterPassword != "right password")
            return Task.FromResult(false);
        State = VaultState.Unlocked;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }

    public void Lock() => State = VaultState.Locked;

    public void ForgetDevice()
    {
    }

    public Task<bool> RememberOnDeviceAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

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
        foreach (var key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _values.Remove(key);
        return Task.CompletedTask;
    }

    public Task<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default) => Task.FromResult(new byte[32]);
}

internal sealed class MemoryHosts : IHostRepository
{
    private HostCatalogData _data = new();

    public Task<HostCatalogData> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_data);

    public Task SaveAsync(HostCatalogData data, CancellationToken cancellationToken = default)
    {
        _data = data;
        return Task.CompletedTask;
    }
}

internal sealed class NoKeys : ISshKeyInspector
{
    public SshKeyInfo? Inspect(string privateKey, string? passphrase, out SshKeyProblem problem)
    {
        problem = SshKeyProblem.InvalidFormat;
        return null;
    }
}

internal sealed class NoAgent : ISshAgentClient
{
    public Task<SshAgentStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SshAgentStatus.Unavailable("No agent in tests"));
}

internal sealed class OpenVaultGate : IVaultGate
{
    public Task<bool> EnsureUnlockedAsync(string? reason = null) => Task.FromResult(true);
}

/// <summary>Answers every dialog with a fixed result instead of showing it.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public List<DialogViewModel> Shown { get; } = [];

    public bool ConfirmResult { get; set; } = true;

    public string? PromptResult { get; set; }

    public Task<TResult?> ShowAsync<TResult>(DialogViewModel<TResult> dialog)
    {
        Shown.Add(dialog);
        return Task.FromResult<TResult?>(default);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool isDestructive = false) => Task.FromResult(ConfirmResult);

    public Task<string?> PromptAsync(string title, string label, string? initialValue = null, string confirmText = "Save") => Task.FromResult(PromptResult);
}

internal sealed class NoFiles : IFilePickerService
{
    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool allowMultiple = false, string? startDirectory = null) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync(string title, string? startDirectory = null) => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string title, string suggestedName, string? startDirectory = null) => Task.FromResult<string?>(null);
}

internal sealed class MemorySettings : ISettingsService
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

internal sealed class NoClipboard : IClipboardService
{
    public Task SetTextAsync(string text) => Task.CompletedTask;
}

internal sealed class NoActivity : IActivityLog
{
    public IReadOnlyList<ActivityEntry> Entries => [];

    public event EventHandler<ActivityEntry>? Added
    {
        add { }
        remove { }
    }

    public event EventHandler? Cleared
    {
        add { }
        remove { }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Add(ActivityKind kind, string host, string message)
    {
    }

    public void Clear()
    {
    }

    public Task FlushAsync() => Task.CompletedTask;
}

internal sealed class NoShell : IShellIntegration
{
    public void OpenFolder(string path)
    {
    }

    public void Reveal(string path)
    {
    }

    public void OpenFile(string path)
    {
    }
}

internal sealed class NoLocalFiles : ILocalFileSystem
{
    public string HomeDirectory => Path.GetTempPath();

    public Task<IReadOnlyList<LocalFileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalFileEntry>>([]);

    public string? Parent(string path) => null;

    public string Combine(string directory, string name) => Path.Combine(directory, name);

    public Task CreateDirectoryAsync(string path) => Task.CompletedTask;

    public Task RenameAsync(string from, string to) => Task.CompletedTask;

    public Task DeleteAsync(LocalFileEntry entry) => Task.CompletedTask;
}

internal sealed class DirectResolver : IConnectionResolver
{
    public Task<ConnectionTarget?> ResolveAsync(HostProfile host, string? knownPassword = null, bool forcePrompt = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<ConnectionTarget?>(new ConnectionTarget { Address = host.Address, Port = host.Port, Username = host.Username, Password = "pw" });
}

/// <summary>An SFTP server made of a dictionary: directory path → entries.</summary>
internal sealed class MemorySftp : ISftpSessionFactory, ISftpSession
{
    private readonly Dictionary<string, List<RemoteFileEntry>> _tree = new(StringComparer.Ordinal);

    public MemorySftp(string home, params string[] paths)
    {
        HomeDirectory = home;
        foreach (var path in paths)
            Add(path);
    }

    public string HomeDirectory { get; }

    public bool IsConnected => true;

    public event EventHandler<string?>? Disconnected
    {
        add { }
        remove { }
    }

    /// <summary>A trailing slash makes a directory.</summary>
    private void Add(string path)
    {
        var isDirectory = path.EndsWith('/');
        var full = path.TrimEnd('/');
        var parent = RemotePath.Parent(full);
        if (parent != full && !_tree.ContainsKey(parent) && parent != "/")
            Add(parent + "/");
        if (!_tree.TryGetValue(parent, out var siblings))
            _tree[parent] = siblings = [];
        if (siblings.All(e => e.FullPath != full))
            siblings.Add(new RemoteFileEntry(RemotePath.Name(full), full, isDirectory, false, isDirectory ? 0 : 100, DateTimeOffset.UtcNow, isDirectory ? "drwxr-xr-x" : "-rw-r--r--"));
        if (isDirectory && !_tree.ContainsKey(full))
            _tree[full] = [];
    }

    public Task<ISftpSession> ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default) => Task.FromResult<ISftpSession>(this);

    public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken cancellationToken = default) =>
        _tree.TryGetValue(path, out var entries)
            ? Task.FromResult<IReadOnlyList<RemoteFileEntry>>(entries.ToList())
            : throw new SftpOperationException("No such file or directory.");

    public Task<RemoteFileEntry?> StatAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tree.Values.SelectMany(e => e).FirstOrDefault(e => e.FullPath == path));

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Add(path + "/");
        return Task.CompletedTask;
    }

    public Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteAsync(RemoteFileEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DownloadFromAsync(string remotePath, long offset, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RemoteFileDetails> GetDetailsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new RemoteFileDetails(path, 0b110_100_100, 1000, 1000));

    public Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetOwnerAsync(string path, int userId, int groupId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
