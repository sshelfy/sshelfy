using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Sshelfy.App.ViewModels;
using Sshelfy.App.ViewModels.Sftp;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.App.Views.Tabs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.Tests;

public sealed class SftpViewTests
{
    private static async Task<(Window Window, SftpTabViewModel Tab)> OpenAsync()
    {
        var server = new MemorySftp("/home/me", "/home/me/projects/", "/home/me/projects/app.py", "/home/me/notes.txt", "/home/me/.profile", "/etc/");
        var tab = new SftpTabViewModel(new HostProfile { Address = "server", Username = "me" }, null, null, server, new DirectResolver(),
            new TransferQueue(), new ScriptedDialogs(), new NoFiles(), new MemorySettings(), new NoClipboard(), new WorkspaceViewModel(),
            new StatusBarViewModel(), new NoActivity(), new NoLocalFiles(), new NoShell());
        var window = UiHelpers.Show(new SftpTabView { DataContext = tab }, 1100, 700);
        await tab.ConnectAsync();
        await UiHelpers.SettleAsync();
        return (window, tab);
    }


    [AvaloniaFact]
    public async Task Opens_in_the_home_folder_with_folders_first_and_hidden_files_hidden()
    {
        var (_, tab) = await OpenAsync();

        Assert.Equal("/home/me", tab.CurrentPath);
        Assert.Equal(["projects", "notes.txt"], tab.Entries.Select(e => e.Name));
        Assert.Equal(["/", "home", "me"], tab.Breadcrumbs.Select(b => b.Name));
    }

    [AvaloniaFact]
    public async Task Enter_opens_a_folder_and_backspace_goes_up()
    {
        var (window, tab) = await OpenAsync();
        var list = window.Find<ListBox>(l => ReferenceEquals(l.ItemsSource, tab.Entries));

        window.Click(list.Find<TextBlock>(t => t.Text == "projects"));
        Assert.Equal(["projects"], tab.SelectedEntries.Select(e => e.Name));
        window.Press(Key.Enter, physical: PhysicalKey.Enter);
        await UiHelpers.SettleAsync();

        Assert.Equal("/home/me/projects", tab.CurrentPath);
        Assert.Equal(["app.py"], tab.Entries.Select(e => e.Name));
        Assert.Contains(window.FindAll<TextBlock>(), t => t.Text == "app.py" && t.IsEffectivelyVisible);

        window.Click(list.Find<TextBlock>(t => t.Text == "app.py"));
        window.Press(Key.Back, physical: PhysicalKey.Backspace);
        await UiHelpers.SettleAsync();
        Assert.Equal("/home/me", tab.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Breadcrumbs_back_forward_and_hidden_toggle()
    {
        var (window, tab) = await OpenAsync();

        window.Click(window.Find<Button>(b => b.CommandParameter as string == "/"));
        await UiHelpers.SettleAsync();
        Assert.Equal("/", tab.CurrentPath);
        Assert.Equal(["etc", "home"], tab.Entries.Select(e => e.Name));

        await tab.GoBackCommand.ExecuteAsync(null);
        Assert.Equal("/home/me", tab.CurrentPath);
        await tab.GoForwardCommand.ExecuteAsync(null);
        Assert.Equal("/", tab.CurrentPath);
        await tab.GoBackCommand.ExecuteAsync(null);

        window.Click(window.Find<ToggleButton>(b => ToolTip.GetTip(b) as string == "Show hidden files"));
        Assert.Contains(tab.Entries, e => e.Name == ".profile");
    }

    [AvaloniaFact]
    public async Task Clicking_the_size_header_sorts_by_size()
    {
        var (window, tab) = await OpenAsync();

        window.Click(window.Find<Button>(b => b.CommandParameter is FileSortColumn.Size && b.DataContext == tab));

        Assert.Equal(FileSortColumn.Size, tab.Sort.Column);
        Assert.True(tab.Entries.First().IsDirectory); // folders stay first
    }
}
