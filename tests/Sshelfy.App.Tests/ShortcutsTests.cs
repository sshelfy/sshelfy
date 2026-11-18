using Avalonia.Input;
using Sshelfy.App.Controls;

namespace Sshelfy.App.Tests;

public sealed class ShortcutsTests
{
    [Fact]
    public void Gestures_are_written_the_platform_way()
    {
        Assert.Equal("Ctrl+Shift+W", Shortcuts.WindowsText(new KeyGesture(Key.W, KeyModifiers.Control | KeyModifiers.Shift)));
        Assert.Equal("Ctrl+,", Shortcuts.WindowsText(new KeyGesture(Key.OemComma, KeyModifiers.Control)));
        Assert.Equal("⇧⌘D", Shortcuts.MacText(new KeyGesture(Key.D, KeyModifiers.Meta | KeyModifiers.Shift)));
        Assert.Equal("⌘⌫", Shortcuts.MacText(new KeyGesture(Key.Back, KeyModifiers.Meta)));
    }

    [Fact]
    public void App_shortcuts_use_cmd_on_macos_and_ctrl_elsewhere()
    {
        var close = Shortcuts.Get("CloseTab");

        if (OperatingSystem.IsMacOS())
            Assert.Equal(new KeyGesture(Key.W, KeyModifiers.Meta), close);
        else
            Assert.Equal(new KeyGesture(Key.W, KeyModifiers.Control | KeyModifiers.Shift), close);
    }
}
