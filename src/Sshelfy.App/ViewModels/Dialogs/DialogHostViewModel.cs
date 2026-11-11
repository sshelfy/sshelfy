using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sshelfy.App.Services;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>
/// Stack of open dialogs rendered as an overlay over the main window. Dialogs can open other
/// dialogs (e.g. importing a key from the host editor); only the top one is shown.
/// </summary>
public sealed partial class DialogHostViewModel : ViewModelBase, IDialogService
{
    private readonly ObservableCollection<DialogViewModel> _stack = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial DialogViewModel? Current { get; private set; }

    public bool HasDialog => Current is not null;

    public async Task<TResult?> ShowAsync<TResult>(DialogViewModel<TResult> dialog)
    {
        _stack.Add(dialog);
        Current = dialog;
        try
        {
            var result = await dialog.Completion;
            return result is TResult typed ? typed : default;
        }
        finally
        {
            _stack.Remove(dialog);
            Current = _stack.Count > 0 ? _stack[^1] : null;
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool isDestructive = false) =>
        await ShowAsync(new ConfirmDialogViewModel(title, message, confirmText, isDestructive));

    public Task<string?> PromptAsync(string title, string label, string? initialValue = null, string confirmText = "Save") =>
        ShowAsync(new TextPromptDialogViewModel(title, label, initialValue, confirmText));
}
