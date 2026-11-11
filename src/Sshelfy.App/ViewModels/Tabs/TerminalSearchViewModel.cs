using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.ViewModels.Tabs;

/// <summary>
/// Find bar over a terminal. Matches are re-computed as new output arrives; the view highlights
/// <see cref="Matches"/> and scrolls to <see cref="ActiveMatch"/>.
/// </summary>
public sealed partial class TerminalSearchViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(300);

    private readonly TerminalEmulator _terminal;
    private int _refreshPending;

    public TerminalSearchViewModel(TerminalEmulator terminal)
    {
        _terminal = terminal;
        _terminal.Changed += OnTerminalChanged;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatches))]
    public partial IReadOnlyList<TerminalMatch> Matches { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveMatch), nameof(Summary))]
    public partial int CurrentIndex { get; private set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool IsInvalid { get; private set; }

    /// <summary>Changes whenever the search field should take focus.</summary>
    [ObservableProperty]
    public partial int FocusRequest { get; private set; }

    public bool HasMatches => Matches.Count > 0;

    public TerminalMatch? ActiveMatch => CurrentIndex >= 0 && CurrentIndex < Matches.Count ? Matches[CurrentIndex] : null;

    public string Summary => IsInvalid ? "Invalid pattern"
        : Query.Length == 0 ? string.Empty
        : Matches.Count == 0 ? "No results"
        : Matches.Count >= TerminalSearch.MaxMatches ? $"{CurrentIndex + 1} of {TerminalSearch.MaxMatches}+"
        : $"{CurrentIndex + 1} of {Matches.Count}";

    [RelayCommand]
    public void Open()
    {
        IsOpen = true;
        FocusRequest++;
        Search(keepPosition: true);
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Matches = [];
        CurrentIndex = -1;
    }

    /// <summary>Moves to the previous (older) match; the search starts at the bottom, so this is the main direction.</summary>
    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Next() => Step(1);

    partial void OnQueryChanged(string value) => Search(keepPosition: false);

    partial void OnMatchCaseChanged(bool value) => Search(keepPosition: false);

    partial void OnUseRegexChanged(bool value) => Search(keepPosition: false);

    private void Step(int direction)
    {
        if (Matches.Count == 0)
            return;
        CurrentIndex = ((CurrentIndex < 0 ? Matches.Count : CurrentIndex) + direction + Matches.Count) % Matches.Count;
    }

    private void Search(bool keepPosition)
    {
        if (!IsOpen)
            return;

        var previous = keepPosition ? ActiveMatch : null;
        IReadOnlyList<TerminalMatch> matches;
        try
        {
            lock (_terminal.SyncRoot)
            {
                matches = TerminalSearch.Find(_terminal, Query, MatchCase, UseRegex);
            }

            IsInvalid = false;
        }
        catch (ArgumentException)
        {
            matches = [];
            IsInvalid = true;
        }

        Matches = matches;
        CurrentIndex = matches.Count == 0 ? -1 : previous is { } match ? Nearest(matches, match) : matches.Count - 1;

        // The index can stay the same while the match itself changed (e.g. one more letter typed).
        OnPropertyChanged(nameof(ActiveMatch));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Index of the same match after a refresh, or the first one at or after its old position.</summary>
    private static int Nearest(IReadOnlyList<TerminalMatch> matches, TerminalMatch target)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (m.StartLine > target.StartLine || (m.StartLine == target.StartLine && m.StartColumn >= target.StartColumn))
                return i;
        }

        return matches.Count - 1;
    }

    private void OnTerminalChanged(object? sender, EventArgs e)
    {
        // Output arrives on a background thread; refresh at most a few times per second.
        if (!IsOpen || Query.Length == 0 || Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;

        _ = Task.Delay(RefreshDelay).ContinueWith(_ => UiThread.Run(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            Search(keepPosition: true);
        }), TaskScheduler.Default);
    }
}
