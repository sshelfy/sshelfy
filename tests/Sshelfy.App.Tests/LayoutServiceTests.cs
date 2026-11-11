using Avalonia.Headless.XUnit;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels;
using Sshelfy.App.ViewModels.Tabs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Settings;
using Sshelfy.Core.Ssh;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.Tests;

public sealed class LayoutServiceTests
{
    [AvaloniaFact]
    public async Task A_nested_split_is_saved_and_rebuilt_with_the_same_shape()
    {
        var (service, workspace, launcher, web, db) = await CreateAsync();
        var a = launcher.CreateTerminalTab(web);
        workspace.Open(a);
        var b = launcher.CreateTerminalTab(db);
        workspace.Split(a, b, sideBySide: true);
        workspace.Split(b, launcher.CreateLocalTab(Shells.Cmd), sideBySide: false);
        workspace.Split(b, launcher.CreateTerminalTab(new HostProfile { Address = "adhoc", Username = "x" }), sideBySide: true); // not saved

        var saved = service.Capture();

        var tab = Assert.Single(saved);
        Assert.Equal(SavedTabKind.Split, tab.Kind);
        var root = tab.Panes!;
        Assert.True(root.SideBySide);
        Assert.Equal(web.Id, root.First!.Tab!.HostId);
        Assert.False(root.Second!.SideBySide);
        Assert.Equal(db.Id, root.Second.First!.Tab!.HostId);
        Assert.Equal("cmd", root.Second.Second!.Tab!.LocalShellId);

        await workspace.CloseAllAsync();
        var opened = await service.OpenAsync(saved);

        Assert.Equal(3, opened);
        var split = Assert.IsType<SplitTabViewModel>(Assert.Single(workspace.Tabs, t => t is not WelcomeTabViewModel));
        var columns = Assert.IsType<ColumnsSplit>(split.Root);
        Assert.Equal(web.Id, Assert.IsType<PaneLeaf>(columns.First).Pane.Host.Id);
        var rows = Assert.IsType<RowsSplit>(columns.Second);
        Assert.Equal(db.Id, Assert.IsType<PaneLeaf>(rows.First).Pane.Host.Id);
        Assert.Equal("cmd", Assert.IsType<PaneLeaf>(rows.Second).Pane.LocalShell?.Id);
        await workspace.CloseAllAsync();
    }

    [AvaloniaFact]
    public async Task Panes_of_deleted_hosts_are_skipped()
    {
        var (service, workspace, _, web, _) = await CreateAsync();
        SavedTab[] tabs =
        [
            new(SavedTabKind.Split, Guid.Empty, Panes: new SavedPane(SideBySide: true,
                First: new SavedPane(new SavedTab(SavedTabKind.Terminal, Guid.NewGuid())),
                Second: new SavedPane(new SavedTab(SavedTabKind.Terminal, web.Id)))),
            new(SavedTabKind.Local, Guid.Empty, "no-such-shell"),
        ];

        var opened = await service.OpenAsync(tabs);

        Assert.Equal(1, opened);
        var terminal = Assert.IsType<TerminalTabViewModel>(workspace.Tabs.Last());
        Assert.Equal(web.Id, terminal.Host.Id);
        await workspace.CloseAllAsync();
    }

    private static async Task<(LayoutService, WorkspaceViewModel, TestLauncher, HostProfile, HostProfile)> CreateAsync()
    {
        var catalog = new HostCatalog(new MemoryHosts(), new FakeVault(), new NoKeys());
        var web = new HostProfile { Name = "web", Address = "web.example", Username = "deploy" };
        var db = new HostProfile { Name = "db", Address = "db.example", Username = "deploy" };
        await catalog.SaveHostAsync(web, null);
        await catalog.SaveHostAsync(db, null);

        var workspace = new WorkspaceViewModel();
        var launcher = new TestLauncher(catalog);
        return (new LayoutService(workspace, launcher, catalog, new Shells()), workspace, launcher, web, db);
    }

    private sealed class Shells : ILocalShellLauncher
    {
        public static readonly LocalShellProfile Cmd = new("cmd", "Command Prompt", "cmd.exe");

        public bool IsSupported => true;

        public Task<IReadOnlyList<LocalShellProfile>> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalShellProfile>>([Cmd]);

        public Task<IShellChannel> StartAsync(LocalShellProfile profile, int columns, int rows, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
            throw new LocalShellException("not in tests");
    }

    private sealed class Unreachable : ISshConnectionFactory
    {
        public Task<ISshConnection> ConnectAsync(ConnectionTarget target, CancellationToken cancellationToken = default) =>
            throw new SshConnectionFailedException("unreachable in tests");
    }

    /// <summary>Creates real terminal tabs whose connections fail at once.</summary>
    private sealed class TestLauncher(IHostCatalog catalog) : ISessionLauncher
    {
        private readonly MemorySettings _settings = new();
        private readonly Shells _shells = new();

        public TerminalTabViewModel CreateTerminalTab(HostProfile host) =>
            new(host, null, new Unreachable(), new DirectResolver(), catalog, _settings, new StatusBarViewModel(), new NoLogs(), new ScriptedDialogs(), new OpenVaultGate(), new NoActivity());

        public TerminalTabViewModel CreateLocalTab(LocalShellProfile profile) =>
            new(new HostProfile { Name = profile.Name, Address = "localhost", Username = "me" }, null, new Unreachable(), new DirectResolver(), catalog, _settings,
                new StatusBarViewModel(), new NoLogs(), new ScriptedDialogs(), new OpenVaultGate(), new NoActivity(), profile, _shells);

        public Task ConnectAsync(HostProfile host) => Task.CompletedTask;

        public Task OpenSftpAsync(HostProfile host, string? password = null, string? startPath = null) => Task.CompletedTask;

        public Task QuickConnectAsync(string? input = null) => Task.CompletedTask;

        public Task SplitAsync(TerminalTabViewModel source, bool sideBySide) => Task.CompletedTask;

        public Task OpenLocalAsync(LocalShellProfile profile) => Task.CompletedTask;
    }

    private sealed class NoLogs : ISessionLogStore
    {
        public string Directory => Path.GetTempPath();

        public Task<ISessionLogSink> CreateAsync(string hostName, SessionLogFormat format, bool encrypt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<SessionLogInfo> List() => [];

        public Task<string> ReadAllTextAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public int DeleteOlderThan(int retentionDays) => 0;

        public void Delete(string filePath)
        {
        }
    }
}
