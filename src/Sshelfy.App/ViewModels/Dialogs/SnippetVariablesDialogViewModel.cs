using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.Core.Snippets;

namespace Sshelfy.App.ViewModels.Dialogs;

public sealed partial class SnippetVariableField : ViewModelBase
{
    private readonly Action _changed;

    public SnippetVariableField(string name, string value, Action changed)
    {
        Name = name;
        _changed = changed;
        Value = value;
    }

    public string Name { get; }

    /// <summary>The first field takes focus when the dialog opens.</summary>
    public bool IsFirst { get; init; }

    [ObservableProperty]
    public partial string Value { get; set; }

    partial void OnValueChanged(string value) => _changed();
}

/// <summary>Asks for the values of a snippet's <c>{{variables}}</c> before it runs, with a live preview.</summary>
public sealed partial class SnippetVariablesDialogViewModel : DialogViewModel<IReadOnlyDictionary<string, string>>
{
    private readonly Snippet _snippet;

    public SnippetVariablesDialogViewModel(Snippet snippet, bool run)
    {
        _snippet = snippet;
        ConfirmText = run ? "Run" : "Insert";

        var variables = SnippetTemplate.Variables(snippet.Command);
        for (var i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];
            var value = snippet.LastValues?.GetValueOrDefault(variable.Name) ?? variable.DefaultValue ?? string.Empty;
            Fields.Add(new SnippetVariableField(variable.Name, value, () => OnPropertyChanged(nameof(Preview))) { IsFirst = i == 0 });
        }
    }

    public override string Title => _snippet.Name.Length > 0 ? _snippet.Name : "Run snippet";

    public override double Width => 480;

    public string ConfirmText { get; }

    public ObservableCollection<SnippetVariableField> Fields { get; } = [];

    public string Preview => SnippetTemplate.Fill(_snippet.Command, Values());

    [RelayCommand]
    private void Confirm() => Close(Values());

    private Dictionary<string, string> Values() => Fields.ToDictionary(f => f.Name, f => f.Value);
}
