using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Snippets;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed partial class SnippetEditorDialogViewModel : DialogViewModel<Snippet>
{
    private readonly Snippet _original;
    private readonly bool _isNew;

    public SnippetEditorDialogViewModel(Snippet? original, bool isNew = false)
    {
        _isNew = original is null || isNew;
        _original = original ?? new Snippet();
        Name = _original.Name;
        Command = _original.Command;
        RunImmediately = _original.RunImmediately;
    }

    public override string Title => _isNew ? "New snippet" : "Edit snippet";

    public override double Width => 520;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Command { get; set; }

    [ObservableProperty]
    public partial bool RunImmediately { get; set; }

    private bool CanSave => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Command);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Close(_original with
    {
        Name = Name.Trim(),
        Command = Command.TrimEnd().Replace("\r\n", "\n"),
        RunImmediately = RunImmediately,
    });
}
