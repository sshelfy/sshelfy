using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Sshelfy.App.Controls;
using Sshelfy.App.ViewModels;
using Sshelfy.App.ViewModels.Sftp;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.App.Views.Tabs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Sftp;

namespace Sshelfy.App.Tests;

public sealed class SftpViewTests
{
    private static async Task<(Window Window, SftpTabViewModel Tab)> OpenAsync(double width = 1100, params string[] extraPaths)
    {
        var (window, tab, _) = await OpenWithServerAsync(width, extraPaths);
        return (window, tab);
    }

    private static async Task<(Window Window, SftpTabViewModel Tab, MemorySftp Server)> OpenWithServerAsync(double width = 1100, params string[] extraPaths)
    {
        var server = new MemorySftp("/home/me", ["/home/me/projects/", "/home/me/projects/app.py", "/home/me/notes.txt", "/home/me/.profile", "/etc/", .. extraPaths]);
        var tab = new SftpTabViewModel(new HostProfile { Address = "server", Username = "me" }, null, null, server, new DirectResolver(),
            new TransferQueue(), new ScriptedDialogs(), new NoFiles(), new MemorySettings(), new NoClipboard(), new WorkspaceViewModel(),
            new StatusBarViewModel(), new NoActivity(), new NoLocalFiles(), new NoShell());
        var window = UiHelpers.Show(new SftpTabView { DataContext = tab }, width, 700);
        await tab.ConnectAsync();
        await UiHelpers.SettleAsync();
        return (window, tab, server);
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
    public async Task A_long_name_in_a_narrow_pane_is_cut_short_instead_of_running_into_the_next_column()
    {
        const string longName = "20261104-0912-a-rather-long-backup-folder-name";
        var (window, tab) = await OpenAsync(520, $"/home/me/{longName}/");
        var list = window.Find<ListBox>(l => ReferenceEquals(l.ItemsSource, tab.Entries));
        var entry = tab.Entries.Single(e => e.Name == longName);

        var name = list.Find<TextBlock>(t => t.Text == longName);
        var modified = list.Find<TextBlock>(t => t.Text == entry.ModifiedText && t.DataContext == entry);
        var nameRight = name.TranslatePoint(new Point(name.Bounds.Width, 0), list)!.Value.X;
        var modifiedLeft = modified.TranslatePoint(new Point(0, 0), list)!.Value.X;

        Assert.True(nameRight <= modifiedLeft, $"name ends at {nameRight}, modified starts at {modifiedLeft}");
    }

    [AvaloniaFact]
    public async Task The_quick_delete_shortcut_deletes_the_selected_file()
    {
        var (window, tab, server) = await OpenWithServerAsync();
        var list = window.Find<ListBox>(l => ReferenceEquals(l.ItemsSource, tab.Entries));
        window.Click(list.Find<TextBlock>(t => t.Text == "notes.txt"));

        var gesture = Shortcuts.Get("DeleteQuick");
        window.Press(gesture.Key, (RawInputModifiers)gesture.KeyModifiers);
        await UiHelpers.SettleAsync();

        Assert.Equal(["/home/me/notes.txt"], server.Deleted);
        Assert.Equal("/home/me", tab.CurrentPath);
    }

    [AvaloniaFact]
    public async Task The_save_shortcut_uploads_the_edited_file()
    {
        var (_, tab, server) = await OpenWithServerAsync();
        var editor = new EditorTabViewModel(tab, server, "/home/me/notes.txt", "hello"u8.ToArray(), new ScriptedDialogs(), new StatusBarViewModel());
        var window = UiHelpers.Show(new EditorTabView { DataContext = editor });
        await UiHelpers.SettleAsync();
        window.Type(window.Find<TextBox>(), " world");

        var gesture = Shortcuts.Get("Save");
        window.Press(gesture.Key, (RawInputModifiers)gesture.KeyModifiers);
        await UiHelpers.SettleAsync();

        Assert.Equal(["/home/me/notes.txt"], server.Uploaded);
        Assert.False(editor.IsDirty);
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
