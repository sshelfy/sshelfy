using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sshelfy.App.Tests;

internal static class UiHelpers
{
    public static Window Show(Control content, double width = 900, double height = 700)
    {
        var window = new Window { Content = content, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    public static T Find<T>(this Visual root, Func<T, bool>? predicate = null) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(c => predicate?.Invoke(c) ?? true);

    public static IEnumerable<T> FindAll<T>(this Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    /// <summary>A real mouse click in the middle of the control.</summary>
    public static void Click(this Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException("Control is not in the window.");
        window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    public static void Type(this Window window, Control target, string text)
    {
        target.Focus();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    public static void Press(this Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None, PhysicalKey physical = PhysicalKey.None, string? symbol = null)
    {
        window.KeyPress(key, modifiers, physical, symbol);
        window.KeyRelease(key, modifiers, physical, symbol);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets queued async continuations on the UI thread run.</summary>
    public static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }
}