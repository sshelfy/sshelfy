using Sshelfy.Core.Hosts;
using Sshelfy.Core.Settings;
using Sshelfy.Core.Snippets;

namespace Sshelfy.Core.Backup;

public sealed record BackupHost(HostProfile Host, string? Password);

public sealed record BackupKey(SshKeyProfile Profile, string PrivateKey, string? Passphrase);

/// <summary>Everything a backup file carries. Secrets are present only when the export included them.</summary>
public sealed record BackupContent
{
    public int Version { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string? MachineName { get; init; }

    public bool IncludesSecrets { get; init; }

    public List<HostGroup> Groups { get; init; } = [];

    public List<BackupHost> Hosts { get; init; } = [];

    public List<BackupKey> Keys { get; init; } = [];

    public List<Snippet> Snippets { get; init; } = [];

    public AppSettings? Settings { get; init; }
}

/// <summary>Reads and writes password-protected backup files.</summary>
public interface IBackupFile
{
    Task WriteAsync(string path, BackupContent content, string password, CancellationToken cancellationToken = default);

    /// <exception cref="BackupPasswordException">Wrong password (or a damaged file).</exception>
    /// <exception cref="BackupFormatException">Not a sshelfy backup, or from a newer version.</exception>
    Task<BackupContent> ReadAsync(string path, string password, CancellationToken cancellationToken = default);
}

public sealed class BackupPasswordException() : Exception("The password is wrong, or the file is damaged.");

public sealed class BackupFormatException(string message) : Exception(message);

public enum ImportItemKind
{
    Group,
    Host,
    Key,
    Snippet,
}

public enum ImportItemState
{
    /// <summary>Not present yet.</summary>
    New,

    /// <summary>Already present with the same content; nothing to do.</summary>
    Identical,

    /// <summary>Something with the same identity (id or name) but different content exists.</summary>
    Conflict,
}

public enum ImportChoice
{
    Import,
    Skip,

    /// <summary>Overwrite the existing item with the backup's version.</summary>
    Replace,

    /// <summary>Import as a separate copy next to the existing item.</summary>
    KeepBoth,
}

/// <summary>One row of the import preview.</summary>
public sealed class ImportItem
{
    public required ImportItemKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Detail { get; init; }

    public required ImportItemState State { get; init; }

    /// <summary>What to do; new items default to <see cref="ImportChoice.Import"/>, conflicts to <see cref="ImportChoice.Skip"/>.</summary>
    public ImportChoice Choice { get; set; }

    /// <summary>The item in this installation it collides with (or equals).</summary>
    public Guid? ExistingId { get; init; }

    internal object Source { get; init; } = null!;
}

public sealed class ImportPlan
{
    public required BackupContent Content { get; init; }

    public required IReadOnlyList<ImportItem> Items { get; init; }

    public bool HasSettings => Content.Settings is not null;

    public bool RestoreSettings { get; set; }
}

public sealed record ImportResult(int Imported, int Replaced, int Skipped, bool SecretsSkipped);
