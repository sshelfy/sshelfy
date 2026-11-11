using CommunityToolkit.Mvvm.ComponentModel;

namespace Sshelfy.App.ViewModels;

public enum StatusLevel
{
    Idle,
    Busy,
    Ok,
    Error,
}

public sealed partial class StatusBarViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Message { get; set; } = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsOk), nameof(IsError))]
    public partial StatusLevel Level { get; set; } = StatusLevel.Idle;

    public bool IsBusy => Level == StatusLevel.Busy;
    public bool IsOk => Level == StatusLevel.Ok;
    public bool IsError => Level == StatusLevel.Error;

    /// <summary>Right-aligned context info for the active tab, e.g. "120×32 · UTF-8".</summary>
    [ObservableProperty]
    public partial string? Detail { get; set; }

    public void Set(string message, StatusLevel level = StatusLevel.Idle)
    {
        Message = message;
        Level = level;
    }
}
