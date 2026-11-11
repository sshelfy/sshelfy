using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.Core.Activity;

namespace Sshelfy.App.ViewModels;

public sealed class ActivityItemViewModel(ActivityEntry entry) : ViewModelBase
{
    public ActivityEntry Entry { get; } = entry;

    public string Time => Entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Host => Entry.Host;

    public string Message => Entry.Message;

    public bool IsProblem => Entry.IsProblem;

    public AppIcon Icon => Entry.Category switch
    {
        ActivityCategory.Security => AppIcon.KeyRound,
        ActivityCategory.Transfers => AppIcon.ArrowLeftRight,
        ActivityCategory.Tunnels => AppIcon.Zap,
        _ => AppIcon.SquareTerminal,
    };
}

public sealed record ActivityFilterOption(string Name, ActivityCategory? Category);

/// <summary>The activity timeline shown in the bottom panel.</summary>
public sealed partial class ActivityPanelViewModel : ViewModelBase
{
    private const string AllHosts = "All hosts";

    private readonly IActivityLog _log;
    private readonly IDialogService _dialogs;

    public ActivityPanelViewModel(IActivityLog log, IDialogService dialogs)
    {
        _log = log;
        _dialogs = dialogs;
        _log.Added += (_, _) => UiThread.Run(Rebuild);
        _log.Cleared += (_, _) => UiThread.Run(Rebuild);
        Categories =
        [
            new("Everything", null),
            new("Connections", ActivityCategory.Connections),
            new("Security", ActivityCategory.Security),
            new("Transfers", ActivityCategory.Transfers),
            new("Tunnels", ActivityCategory.Tunnels),
        ];
        SelectedCategory = Categories[0];
        SelectedHost = AllHosts;
        Rebuild();
    }

    public ObservableCollection<ActivityItemViewModel> Items { get; } = [];

    public ObservableCollection<string> Hosts { get; } = [AllHosts];

    public IReadOnlyList<ActivityFilterOption> Categories { get; }

    [ObservableProperty]
    public partial ActivityFilterOption SelectedCategory { get; set; }

    [ObservableProperty]
    public partial string? SelectedHost { get; set; }

    public bool HasItems => Items.Count > 0;

    partial void OnSelectedCategoryChanged(ActivityFilterOption value) => Rebuild();

    partial void OnSelectedHostChanged(string? value) => Rebuild();

    [RelayCommand]
    private async Task Clear()
    {
        if (await _dialogs.ConfirmAsync("Clear activity", "Remove every entry from the activity log?", "Clear", isDestructive: true))
            _log.Clear();
    }

    private void Rebuild()
    {
        var entries = _log.Entries;

        foreach (var name in entries.Select(e => e.Host).Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
        {
            if (!Hosts.Contains(name))
                Hosts.Add(name);
        }

        var host = SelectedHost is null or AllHosts ? null : SelectedHost;
        Items.Clear();
        foreach (var entry in entries.Take(500))
        {
            if (SelectedCategory.Category is { } category && entry.Category != category)
                continue;
            if (host is not null && entry.Host != host)
                continue;
            Items.Add(new ActivityItemViewModel(entry));
        }

        OnPropertyChanged(nameof(HasItems));
    }
}
