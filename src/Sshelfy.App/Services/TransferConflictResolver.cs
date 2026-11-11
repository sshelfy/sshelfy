using Avalonia.Threading;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.Services;

/// <summary>Shows the "file already exists" dialog for the transfer queue.</summary>
internal sealed class TransferConflictResolver(IDialogService dialogs) : ITransferConflictResolver
{
    public Task<ConflictDecision> ResolveAsync(TransferConflict conflict, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
            await dialogs.ShowAsync(new TransferConflictDialogViewModel(conflict))
            ?? new ConflictDecision(ConflictChoice.Cancel)); // closing the dialog stops the transfer
}