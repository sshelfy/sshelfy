using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed partial class ConfirmDialogViewModel : DialogViewModel<bool>
{
    public ConfirmDialogViewModel(string title, string message, string confirmText, bool isDestructive)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDestructive = isDestructive;
    }

    public override string Title { get; }

    public override double Width => 400;

    public string Message { get; }

    public string ConfirmText { get; }

    public bool IsDestructive { get; }

    [RelayCommand]
    private void Confirm() => Close(true);
}

public sealed partial class TextPromptDialogViewModel : DialogViewModel<string>
{
    public TextPromptDialogViewModel(string title, string label, string? initialValue, string confirmText)
    {
        Title = title;
        Label = label;
        Value = initialValue ?? string.Empty;
        ConfirmText = confirmText;
    }

    public override string Title { get; }

    public override double Width => 400;

    public string Label { get; }

    public string ConfirmText { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Value { get; set; }

    private bool CanConfirm => !string.IsNullOrWhiteSpace(Value);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Close(Value.Trim());
}
