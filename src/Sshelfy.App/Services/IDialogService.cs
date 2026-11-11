using Sshelfy.App.ViewModels.Dialogs;

namespace Sshelfy.App.Services;

public interface IDialogService
{
    /// <returns>The dialog result, or default when it was cancelled.</returns>
    Task<TResult?> ShowAsync<TResult>(DialogViewModel<TResult> dialog);

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool isDestructive = false);

    Task<string?> PromptAsync(string title, string label, string? initialValue = null, string confirmText = "Save");
}
