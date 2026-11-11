using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>"The file already exists": overwrite, skip, keep both or resume.</summary>
public sealed partial class TransferConflictDialogViewModel(TransferConflict conflict) : DialogViewModel<ConflictDecision>
{
    public override string Title => "File already exists";

    public override double Width => 480;

    public string Message => conflict.Direction == TransferDirection.Upload
        ? $"\"{conflict.Name}\" already exists on the server in {RemotePath.Parent(conflict.TargetPath)}."
        : $"\"{conflict.Name}\" already exists in {Path.GetDirectoryName(conflict.TargetPath)}.";

    public string ExistingText => Describe(conflict.ExistingSize, conflict.ExistingModified);

    public string SourceText => Describe(conflict.SourceSize, conflict.SourceModified);

    public bool CanResume => conflict.CanResume;

    public string ResumeHint => $"The existing file looks like an interrupted copy: {ByteSize.Format(conflict.ExistingSize)} of {ByteSize.Format(conflict.SourceSize)}.";

    /// <summary>Only offered while copying a folder, where more conflicts may follow.</summary>
    public bool CanApplyToAll => conflict.IsPartOfFolder;

    [ObservableProperty]
    public partial bool ApplyToAll { get; set; }

    [RelayCommand]
    private void Choose(ConflictChoice choice) => Close(new ConflictDecision(choice, ApplyToAll));

    [RelayCommand]
    private void CancelTransfer() => Close(new ConflictDecision(ConflictChoice.Cancel));

    private static string Describe(long size, DateTimeOffset modified) =>
        $"{ByteSize.Format(size)}  ·  {(modified == default ? "unknown date" : modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}";
}
