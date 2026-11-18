using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Sshelfy.App.Controls;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.App.Views;
using Sshelfy.Core.Hosts;

namespace Sshelfy.App.Tests;

public sealed class ShellShortcutsTests
{
    [AvaloniaFact]
    public async Task Window_shortcuts_open_sftp_next_to_a_terminal_and_close_the_focused_pane()
    {
        var services = Bootstrapper.BuildServices();
        var shell = services.GetRequiredService<ShellViewModel>();
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        var host = new HostProfile { Name = "local", Address = "127.0.0.1", Port = 1, Username = "me" };
        var terminal = services.GetRequiredService<ISessionLauncher>().CreateTerminalTab(host);
        shell.Workspace.Open(terminal);
        await UiHelpers.SettleAsync();

        Press(window, "SplitSftpRight");
        await UiHelpers.SettleAsync();

        var split = Assert.IsType<SplitTabViewModel>(shell.Workspace.ActiveTab);
        Assert.IsType<SftpTabViewModel>(split.ActivePane);

        Press(window, "CloseTab");
        await UiHelpers.SettleAsync();

        Assert.Same(terminal, Assert.Single(shell.Workspace.Tabs, t => t is not WelcomeTabViewModel));
        await shell.Workspace.CloseAllAsync();
    }

    private static void Press(Avalonia.Controls.Window window, string shortcut)
    {
        var gesture = Shortcuts.Get(shortcut);
        window.Press(gesture.Key, (RawInputModifiers)gesture.KeyModifiers);
    }
}
