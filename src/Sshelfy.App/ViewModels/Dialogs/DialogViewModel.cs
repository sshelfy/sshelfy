using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sshelfy.App.ViewModels.Dialogs;

/// <summary>
/// Base for in-window modal dialogs. The dialog host shows the view registered for the concrete
/// type and awaits <see cref="Completion"/>.
/// </summary>
public abstract partial class DialogViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public abstract string Title { get; }

    /// <summary>Preferred card width; dialogs with forms are wider than confirmations.</summary>
    public virtual double Width => 440;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    internal Task<object?> Completion => _completion.Task;

    protected void Close(object? result) => _completion.TrySetResult(result);

    [RelayCommand]
    public void Cancel()
    {
        if (!IsBusy)
            Close(null);
    }
}

public abstract class DialogViewModel<TResult> : DialogViewModel
{
    protected void Close(TResult result) => base.Close(result);
}
