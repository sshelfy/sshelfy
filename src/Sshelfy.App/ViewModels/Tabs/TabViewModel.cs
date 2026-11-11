using CommunityToolkit.Mvvm.ComponentModel;

namespace Sshelfy.App.ViewModels.Tabs;

/// <summary>A document in the main tab strip (terminal, SFTP browser, welcome page…).</summary>
public abstract partial class TabViewModel : ViewModelBase, IAsyncDisposable
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Subtitle { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public abstract AppIcon Icon { get; }

    /// <summary>True while the tab's session is being recorded (shows a dot on the tab).</summary>
    public virtual bool IsRecording => false;

    /// <summary>Something happened while the tab was in the background (a highlight trigger, a long command finished).</summary>
    [ObservableProperty]
    public partial bool HasAlert { get; set; }

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            HasAlert = false;
    }

    /// <summary>Context shown on the right side of the status bar while this tab is active.</summary>
    public virtual string? StatusDetail => null;

    /// <summary>Resource usage of the tab's server, shown in the status bar (null hides it).</summary>
    public virtual string? ServerStats => null;

    public virtual bool CanClose => true;

    /// <summary>Gives the tab a chance to cancel closing (e.g. unsaved changes).</summary>
    public virtual Task<bool> ConfirmCloseAsync() => Task.FromResult(true);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
