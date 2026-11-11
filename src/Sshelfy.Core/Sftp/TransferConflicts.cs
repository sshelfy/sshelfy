namespace Sshelfy.Core.Sftp;

public enum ConflictChoice
{
    Overwrite,
    Skip,

    /// <summary>Transfer under a free name such as "report (1).pdf".</summary>
    KeepBoth,

    /// <summary>Append the missing rest to the shorter existing file.</summary>
    Resume,

    /// <summary>Stop the whole transfer.</summary>
    Cancel,
}

/// <summary>The target of a file transfer already exists.</summary>
public sealed record TransferConflict(
    TransferDirection Direction,
    string Name,
    string TargetPath,
    long ExistingSize,
    DateTimeOffset ExistingModified,
    long SourceSize,
    DateTimeOffset SourceModified,
    bool CanResume,
    bool IsPartOfFolder);

public sealed record ConflictDecision(ConflictChoice Choice, bool ApplyToAll = false);

/// <summary>Asks the user what to do with an existing target (implemented by the UI).</summary>
public interface ITransferConflictResolver
{
    Task<ConflictDecision> ResolveAsync(TransferConflict conflict, CancellationToken cancellationToken);
}

public static class TransferNames
{
    /// <summary>"report.pdf" → "report (1).pdf", "report (2).pdf"… the first name for which <paramref name="exists"/> is false.</summary>
    public static async Task<string> NextFreeAsync(string name, Func<string, Task<bool>> exists)
    {
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!await exists(candidate))
                return candidate;
        }

        throw new IOException($"No free name for \"{name}\".");
    }
}
