using CommunityToolkit.Mvvm.ComponentModel;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>One editable highlight rule in Settings; every change is saved through <paramref name="changed"/>.</summary>
public sealed partial class HighlightRuleViewModel(HighlightRule rule, Action changed) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError))]
    public partial string Pattern { get; set; } = rule.Pattern;

    [ObservableProperty]
    public partial HighlightColor Color { get; set; } = rule.Color;

    [ObservableProperty]
    public partial bool Background { get; set; } = rule.Background;

    [ObservableProperty]
    public partial bool Notify { get; set; } = rule.Notify;

    [ObservableProperty]
    public partial bool IgnoreCase { get; set; } = rule.IgnoreCase;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = rule.IsEnabled;

    /// <summary>Why the pattern is not a valid regular expression (the rule is then skipped).</summary>
    public string? Error => string.IsNullOrEmpty(Pattern) ? null : TerminalHighlighter.Validate(Pattern);

    public bool HasError => Error is not null;

    public HighlightRule ToRule() => new(Pattern, Color, Background, Notify, IgnoreCase, IsEnabled);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Error) or nameof(HasError)))
            changed();
    }
}
