using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Sshelfy.App.Tests;

public sealed class SmokeTests
{
    [AvaloniaFact]
    public void Window_shows_on_the_headless_platform()
    {
        var window = new Window { Content = new TextBlock { Text = "hello" } };
        window.Show();
        Assert.True(window.IsVisible);
    }
}