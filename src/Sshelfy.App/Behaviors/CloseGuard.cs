using Avalonia;
using Avalonia.Controls;

namespace Sshelfy.App.Behaviors;

/// <summary>Implemented by a window's view model to veto closing (e.g. while sessions are open).</summary>
public interface ICloseGuard
{
    /// <returns>True when the window may close.</returns>
    Task<bool> CanCloseAsync();
}

/// <summary>
/// Attached property that asks an <see cref="ICloseGuard"/> before a window closes. Closing is
/// cancelled first, then retried once the (asynchronous) answer is yes.
/// </summary>
public static class CloseGuard
{
    public static readonly AttachedProperty<ICloseGuard?> GuardProperty =
        AvaloniaProperty.RegisterAttached<Window, ICloseGuard?>("Guard", typeof(CloseGuard));

    private static readonly AttachedProperty<bool> ApprovedProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("Approved", typeof(CloseGuard));

    static CloseGuard()
    {
        GuardProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            window.Closing -= OnClosing;
            if (e.NewValue is not null)
                window.Closing += OnClosing;
        });
    }

    public static ICloseGuard? GetGuard(Window window) => window.GetValue(GuardProperty);

    public static void SetGuard(Window window, ICloseGuard? value) => window.SetValue(GuardProperty, value);

    private static async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window || GetGuard(window) is not { } guard || window.GetValue(ApprovedProperty))
            return;

        e.Cancel = true;
        if (!await guard.CanCloseAsync())
            return;

        window.SetValue(ApprovedProperty, true);
        window.Close();
    }
}
