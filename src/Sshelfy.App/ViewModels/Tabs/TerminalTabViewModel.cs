using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sshelfy.App.Services;
using Sshelfy.App.ViewModels.Tunnels;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Services;
using Sshelfy.Core.Ssh;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.ViewModels.Tabs;

public enum SessionState
{
    Connecting,
    Connected,
    Disconnected,
    Failed,
}

/// <summary>
/// One interactive SSH session: owns the connection, the shell channel and the terminal model,
/// and wires them together. The view only renders <see cref="Terminal"/>.
/// </summary>
public sealed partial class TerminalTabViewModel : TabViewModel
{
    private readonly HostProfile _host;
    private readonly ISshConnectionFactory _connections;
    private readonly IConnectionResolver _resolver;
    private readonly IHostCatalog _catalog;
    private readonly StatusBarViewModel _status;
    private readonly ISettingsService _settings;
    private readonly ISessionLogStore _logs;
    private readonly IDialogService _dialogs;
    private readonly IVaultGate _vaultGate;
    private SessionRecorder? _recorder;
    private readonly Sshelfy.Core.Activity.IActivityLog _activity;
    private DateTimeOffset? _connectedAt;

    private string? _password;
    private CancellationTokenSource? _connectCts;
    private ISshConnection? _connection;
    private IShellChannel? _shell;
    private readonly ILocalShellLauncher? _localShells;
    private readonly IAttentionService? _attention;

    public TerminalTabViewModel(
        HostProfile host,
        string? password,
        ISshConnectionFactory connections,
        IConnectionResolver resolver,
        IHostCatalog catalog,
        ISettingsService settings,
        StatusBarViewModel status,
        ISessionLogStore logs,
        IDialogService dialogs,
        IVaultGate vaultGate,
        Sshelfy.Core.Activity.IActivityLog activity,
        LocalShellProfile? localShell = null,
        ILocalShellLauncher? localShells = null,
        IAttentionService? attention = null)
    {
        _attention = attention;
        _activity = activity;
        LocalShell = localShell;
        _localShells = localShells;
        _logs = logs;
        _dialogs = dialogs;
        _vaultGate = vaultGate;
        _host = host;
        _password = password;
        _connections = connections;
        _resolver = resolver;
        _catalog = catalog;
        _status = status;

        _settings = settings;
        var s = settings.Current;
        FontFamily = s.TerminalFontFamily;
        FontSize = s.TerminalFontSize;
        settings.Changed += OnSettingsChanged;
        Terminal = new TerminalEmulator(100, 30, s.TerminalScrollbackLines);
        Terminal.Output += OnTerminalOutput;
        Terminal.Resized += OnTerminalResized;
        Terminal.TitleChanged += (_, title) => Dispatcher.UIThread.Post(() => RemoteTitle = title);
        Terminal.WorkingDirectoryChanged += (_, path) => Dispatcher.UIThread.Post(() => WorkingDirectory = path);
        Terminal.CommandSubmitted += (_, command) => Dispatcher.UIThread.Post(() =>
        {
            CommandSubmitted?.Invoke(this, command);
            WatchCommand(command);
        });
        Terminal.LineFed += OnLineFed;
        _highlightRules = s.HighlightRules;
        Highlighter = new TerminalHighlighter(s.HighlightRules);
        Search = new TerminalSearchViewModel(Terminal);
        Search.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalSearchViewModel.IsOpen))
                OnPropertyChanged(nameof(WantsKeyboardFocus));
        };

        Title = localShell?.Name ?? host.DisplayName;
        Subtitle = localShell is null ? host.Endpoint
            : localShell.Id.StartsWith("ssh:", StringComparison.Ordinal) ? $"{host.Endpoint}  ·  OpenSSH"
            : $"Local  ·  {localShell.Executable}";

        foreach (var rule in host.PortForwards)
            Tunnels.Add(new TunnelViewModel(this, rule, isTemporary: false));

        // Saved hosts keep history by id (survives renames); ad-hoc sessions by endpoint; local shells by shell.
        HistoryKey = localShell is not null && !IsExternalSsh ? "local:" + localShell.Id
            : catalog.FindHost(host.Id) is not null ? host.Id.ToString("N")
            : $"{host.Username}@{host.Address}:{host.Port}";

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsActive))
                OnPropertyChanged(nameof(WantsKeyboardFocus));
        };
    }

    public TerminalEmulator Terminal { get; }

    /// <summary>Set for a local shell (PowerShell, cmd, WSL) instead of an SSH session.</summary>
    public LocalShellProfile? LocalShell { get; }

    public bool IsLocal => LocalShell is not null;

    /// <summary>A saved host opened in the system OpenSSH client (for agent or X11 forwarding).</summary>
    public bool IsExternalSsh => LocalShell?.Id.StartsWith("ssh:", StringComparison.Ordinal) == true;

    public TerminalSearchViewModel Search { get; }

    public HostProfile Host => _host;

    /// <summary>The password this session logged in with (lets a split pane connect without asking again).</summary>
    internal string? KnownPassword => _password;

    /// <summary>Key under which this session's commands are remembered.</summary>
    public string HistoryKey { get; }

    public bool IsConnected => State == SessionState.Connected;

    /// <summary>Port forwards of this session: the host's saved rules plus any added during the session.</summary>
    public ObservableCollection<TunnelViewModel> Tunnels { get; } = [];

    public async Task StartTunnelAsync(TunnelViewModel tunnel)
    {
        if (_connection is { } connection && State == SessionState.Connected)
        {
            await tunnel.StartOnAsync(connection);
            OnPropertyChanged(nameof(StatusDetail));
            if (tunnel.IsFailed)
                _status.Set($"Tunnel {tunnel.Description}: {tunnel.Error}", StatusLevel.Error);
        }
    }

    /// <summary>Adds a tunnel to the running session (and starts it when connected).</summary>
    public async Task AddTunnelAsync(Sshelfy.Core.Ssh.PortForwardRule rule, bool isTemporary)
    {
        var tunnel = new TunnelViewModel(this, rule, isTemporary);
        Tunnels.Add(tunnel);
        await StartTunnelAsync(tunnel);
    }

    public async Task RemoveTunnelAsync(TunnelViewModel tunnel)
    {
        await tunnel.StopAsync();
        Tunnels.Remove(tunnel);
        OnPropertyChanged(nameof(StatusDetail));
    }

    /// <summary>Types a command into the session; <paramref name="run"/> also presses Enter.</summary>
    public bool SendCommand(string command, bool run)
    {
        if (State != SessionState.Connected)
            return false;

        Terminal.Paste(command);
        if (run)
            Terminal.SendKey(Sshelfy.Core.Terminal.TerminalKey.Enter);
        return true;
    }

    [ObservableProperty]
    public partial string FontFamily { get; private set; }

    [ObservableProperty]
    public partial double FontSize { get; private set; }

    public override AppIcon Icon => IsLocal ? AppIcon.Terminal : AppIcon.SquareTerminal;

    /// <summary>A command line the user ran in this session (read from the screen).</summary>
    public event EventHandler<string>? CommandSubmitted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnecting), nameof(IsFailed), nameof(IsDisconnected), nameof(ShowOverlay), nameof(StatusDetail), nameof(WantsKeyboardFocus), nameof(IsConnected), nameof(ShowSessionEnded))]
    public partial SessionState State { get; private set; } = SessionState.Connecting;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? RemoteTitle { get; private set; }

    /// <summary>The shell's current folder, when it reports it (OSC 7, see README).</summary>
    [ObservableProperty]
    public partial string? WorkingDirectory { get; private set; }

    /// <summary>The terminal should hold keyboard focus: tab is active, the session is live and the find bar is closed.</summary>
    public bool WantsKeyboardFocus => IsActive && State == SessionState.Connected && !Search.IsOpen;

    public bool IsConnecting => State == SessionState.Connecting;

    public bool IsFailed => State == SessionState.Failed;

    public bool IsDisconnected => State == SessionState.Disconnected;

    /// <summary>Connecting and failed states cover the terminal; a dropped session only shows a banner.</summary>
    public bool ShowOverlay => State is SessionState.Connecting or SessionState.Failed;

    public string ConnectingText =>
        LocalShell is { } local ? $"Starting {local.Name}…"
        : _host.JumpHostId is { } jumpId && _catalog.FindHost(jumpId) is { } jump ? $"Connecting to {_host.Endpoint} via {jump.DisplayName}…"
        : $"Connecting to {_host.Endpoint}…";

    public override bool IsRecording => _recorder is not null;

    public override string? StatusDetail => State != SessionState.Connected ? null
        : IsExternalSsh ? $"{(IsRecording ? "● REC  ·  " : string.Empty)}{Terminal.Columns}×{Terminal.Rows}  ·  OpenSSH{(_host.ForwardAgent ? "  ·  agent forwarded" : string.Empty)}{(_host.ForwardX11 ? "  ·  X11" : string.Empty)}"
        : LocalShell is { } local ? $"{(IsRecording ? "● REC  ·  " : string.Empty)}{Terminal.Columns}×{Terminal.Rows}  ·  {local.Name}"
        : $"{(IsRecording ? "● REC  ·  " : string.Empty)}{Terminal.Columns}×{Terminal.Rows}  ·  xterm-256color{(_connection?.CipherName is { } cipher ? "  ·  " + cipher : string.Empty)}{TunnelSummary}";

    private string TunnelSummary => Tunnels.Count(t => t.IsActive) switch
    {
        0 => string.Empty,
        1 => "  ·  1 tunnel",
        var n => $"  ·  {n} tunnels",
    };

    public Task ConnectAsync(bool forcePasswordPrompt = false)
    {
        CancelAutoReconnect();
        return ConnectCoreAsync(forcePasswordPrompt, background: false);
    }

    private enum ConnectResult
    {
        Connected,
        Failed,
        Stopped,
    }

    /// <param name="background">An automatic reconnect attempt: the terminal stays visible and failures are not reported as errors.</param>
    private async Task<ConnectResult> ConnectCoreAsync(bool forcePasswordPrompt, bool background)
    {
        await TearDownAsync();
        _connectCts = new CancellationTokenSource();
        var token = _connectCts.Token;

        if (!background)
        {
            State = SessionState.Connecting;
            ErrorMessage = null;
            _status.Set(ConnectingText, StatusLevel.Busy);
        }

        if (LocalShell is { } local)
            return await StartLocalAsync(local, token);

        try
        {
            var target = await _resolver.ResolveAsync(_host, _password, forcePasswordPrompt, token);
            if (target is null)
            {
                if (!background)
                    Fail("Connection cancelled.");
                return ConnectResult.Stopped;
            }

            _password = target.Password;
            var connection = await _connections.ConnectAsync(target, token);
            token.ThrowIfCancellationRequested();
            _connection = connection;
            connection.Disconnected += OnConnectionLost;

            if (background)
                Terminal.Feed("\x1b[2m[Reconnected]\x1b[0m\r\n");

            var shell = await connection.OpenShellAsync(Terminal.Columns, Terminal.Rows, token);
            _shell = shell;
            shell.DataReceived += OnShellData;
            shell.Closed += OnShellClosed;

            // The view may have resized the grid while we were connecting.
            shell.Resize(Terminal.Columns, Terminal.Rows);

            // The host profile's variables and startup command, typed with a leading space so shells leave them out of history.
            if (StartupScript.Build(_host.Environment, _host.StartupCommand) is { } startup)
                shell.Write(System.Text.Encoding.UTF8.GetBytes(" " + startup + "\r"));

            State = SessionState.Connected;
            _status.Set($"Connected to {_host.Endpoint}", StatusLevel.Ok);
            _connectedAt = DateTimeOffset.Now;
            _activity.Add(Sshelfy.Core.Activity.ActivityKind.Connected, _host.DisplayName,
                $"Terminal session to {target.Route} ({target.Endpoint})");

            foreach (var tunnel in Tunnels)
            {
                tunnel.RefreshAvailability();
                if (tunnel.Rule.AutoStart)
                    await StartTunnelAsync(tunnel);
            }

            if (_host.AlwaysRecordSession && _recorder is null)
                await StartRecordingAsync(askFirst: false);
            StartServerStats();
            if (_catalog.FindHost(_host.Id) is not null)
                await _catalog.MarkConnectedAsync(_host.Id);
            return ConnectResult.Connected;
        }
        catch (OperationCanceledException)
        {
            // Tab closed or reconnect started.
            return ConnectResult.Stopped;
        }
        catch (SshAuthenticationFailedException ex)
        {
            // A rejected password is retried with a prompt once; keys and repeated failures stop here.
            _activity.Add(Sshelfy.Core.Activity.ActivityKind.LoginFailed, _host.DisplayName, ex.Message);
            if (!background && !forcePasswordPrompt && _host.AuthMethod == AuthMethod.Password && !token.IsCancellationRequested)
            {
                _password = null;
                return await ConnectCoreAsync(forcePasswordPrompt: true, background: false);
            }

            if (!background)
                Fail(ex.Message);
            return ConnectResult.Stopped;
        }
        catch (SshConnectionFailedException ex)
        {
            // Unreachable while reconnecting is expected; only the final give-up is logged.
            if (background)
                return ConnectResult.Failed;
            _activity.Add(Sshelfy.Core.Activity.ActivityKind.ConnectionFailed, _host.DisplayName, ex.Message);
            Fail(ex.Message);
            return ConnectResult.Failed;
        }
    }

    // ---------------------------------------------------------------- automatic reconnect

    private static readonly int[] ReconnectDelays = [2, 5, 10, 20, 30];
    private const int MaxReconnectAttempts = 10;
    private CancellationTokenSource? _reconnectCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSessionEnded))]
    public partial bool IsReconnecting { get; private set; }

    [ObservableProperty]
    public partial string? ReconnectText { get; private set; }

    /// <summary>The plain "Session ended" banner; while reconnecting the banner shows progress instead.</summary>
    public bool ShowSessionEnded => IsDisconnected && !IsReconnecting;

    [RelayCommand]
    private void CancelReconnect()
    {
        CancelAutoReconnect();
        _status.Set($"Stopped reconnecting to {_host.Endpoint}");
    }

    private void CancelAutoReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;
    }

    /// <summary>Retries with growing pauses until the session is back, the user stops it or attempts run out.</summary>
    private async Task AutoReconnectAsync()
    {
        CancelAutoReconnect();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        var token = cts.Token;
        IsReconnecting = true;
        try
        {
            for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                var delay = ReconnectDelays[Math.Min(attempt - 1, ReconnectDelays.Length - 1)];
                for (var left = delay; left > 0; left--)
                {
                    ReconnectText = $"Connection lost. Reconnecting in {left} s  ·  attempt {attempt} of {MaxReconnectAttempts}";
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }

                ReconnectText = $"Reconnecting…  ·  attempt {attempt} of {MaxReconnectAttempts}";
                var result = await ConnectCoreAsync(forcePasswordPrompt: false, background: true);
                if (token.IsCancellationRequested)
                    return;
                if (result == ConnectResult.Connected)
                    return;

                if (result == ConnectResult.Stopped)
                    break;
            }

            var message = $"Could not reconnect after {MaxReconnectAttempts} attempts";
            _activity.Add(Sshelfy.Core.Activity.ActivityKind.ConnectionFailed, _host.DisplayName, message);
            _status.Set($"{message} to {_host.Endpoint}", StatusLevel.Error);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user, a manual reconnect or closing the tab.
        }
        finally
        {
            if (_reconnectCts == cts)
                _reconnectCts = null;
            cts.Dispose();
            IsReconnecting = false;
            ReconnectText = null;
        }
    }

    private async Task<ConnectResult> StartLocalAsync(LocalShellProfile local, CancellationToken token)
    {
        try
        {
            var start = WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (local.Prepare is { } prepare)
                await prepare(token);
            var shell = await (_localShells ?? new NoLocalShells()).StartAsync(local, Terminal.Columns, Terminal.Rows, start, token);
            _shell = shell;
            shell.DataReceived += OnShellData;
            shell.Closed += OnShellClosed;
            shell.Resize(Terminal.Columns, Terminal.Rows);
            State = SessionState.Connected;
            _status.Set($"Started {local.Name}", StatusLevel.Ok);
            return ConnectResult.Connected;
        }
        catch (OperationCanceledException)
        {
            return ConnectResult.Stopped;
        }
        catch (LocalShellException ex)
        {
            Fail(ex.Message);
            return ConnectResult.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Sshelfy.Core.Security.VaultLockedException)
        {
            // Preparing an OpenSSH session (writing a key file, reading the vault) failed.
            Fail(ex.Message);
            return ConnectResult.Failed;
        }
    }

    /// <summary>Lets the session's tunnels add to the activity log.</summary>
    internal void LogActivity(Sshelfy.Core.Activity.ActivityKind kind, string message) => _activity.Add(kind, _host.DisplayName, message);

    private void LogSessionEnd(string? reason)
    {
        if (_connectedAt is not { } started)
            return;
        _connectedAt = null;
        var duration = DateTimeOffset.Now - started;
        var length = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        _activity.Add(Sshelfy.Core.Activity.ActivityKind.Disconnected, _host.DisplayName,
            $"Terminal session ended after {length}{(reason is null ? string.Empty : ": " + reason)}");
    }

    [RelayCommand]
    private Task Reconnect() => ConnectAsync();

    [RelayCommand]
    public Task ToggleRecording() => IsRecording ? StopRecordingAsync() : StartRecordingAsync(askFirst: true);

    /// <summary>Starts writing this session to a log file (see Settings → Session logging).</summary>
    public async Task StartRecordingAsync(bool askFirst)
    {
        if (_recorder is not null)
            return;

        var s = _settings.Current;
        if (askFirst && !s.SessionLoggingAcknowledged)
        {
            var message = "Recordings can contain sensitive information: anything shown in the terminal, " +
                          "such as file contents or secrets printed by commands. They are saved to " +
                          $"{_logs.Directory}{(s.EncryptSessionLogs ? ", encrypted with your vault key" : string.Empty)}.";
            if (!await _dialogs.ConfirmAsync("Record session", message, "Start recording"))
                return;
            _settings.Update(x => x with { SessionLoggingAcknowledged = true });
        }

        if (s.EncryptSessionLogs && !await _vaultGate.EnsureUnlockedAsync("Session logs are encrypted with a key from the vault."))
            return;

        try
        {
            var sink = await _logs.CreateAsync(_host.DisplayName, s.SessionLogFormat, s.EncryptSessionLogs);
            var recorder = new SessionRecorder(Terminal, sink, s.SessionLogFormat, s.SessionLogTimestamps,
                (long)s.SessionLogMaxMegabytes * 1024 * 1024, _host.Endpoint);
            recorder.LimitReached += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                await StopRecordingAsync();
                _status.Set("Recording stopped: the log reached its size limit", StatusLevel.Error);
            });
            _recorder = recorder;
            NotifyRecordingChanged();
            _status.Set($"Recording to {Path.GetFileName(sink.FilePath)}", StatusLevel.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Sshelfy.Core.Security.VaultLockedException)
        {
            _status.Set($"Could not start recording: {ex.Message}", StatusLevel.Error);
        }
    }

    public async Task StopRecordingAsync()
    {
        if (_recorder is not { } recorder)
            return;

        _recorder = null;
        NotifyRecordingChanged();
        await recorder.DisposeAsync();
        if (!recorder.ReachedLimit)
            _status.Set($"Recording saved: {Path.GetFileName(recorder.FilePath)}");
    }

    private void NotifyRecordingChanged()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(StatusDetail));
    }

    private void OnSettingsChanged(object? sender, Sshelfy.Core.Settings.AppSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        FontFamily = settings.TerminalFontFamily;
        FontSize = settings.TerminalFontSize;
        if (!ReferenceEquals(settings.HighlightRules, _highlightRules))
        {
            _highlightRules = settings.HighlightRules;
            Highlighter = new TerminalHighlighter(settings.HighlightRules);
        }

        if (settings.ShowServerStats != (_statsCts is not null))
        {
            if (settings.ShowServerStats && State == SessionState.Connected)
                StartServerStats();
            else
                StopServerStats();
        }
    });

    // ---------------------------------------------------------------- highlights and notifications

    private IReadOnlyList<HighlightRule> _highlightRules;
    private long _lastOutputTicks;
    private long _lastTriggerTicks = long.MinValue / 2;
    private DispatcherTimer? _commandTimer;
    private string? _watchedCommand;
    private long _commandStartTicks;

    /// <summary>Colors matching text in the view; rebuilt when the rules change in Settings.</summary>
    [ObservableProperty]
    public partial TerminalHighlighter Highlighter { get; private set; }

    partial void OnHighlighterChanged(TerminalHighlighter value) => Terminal.ReportLineFeeds = value.HasNotifications;

    /// <summary>Runs on the feeding thread with the terminal locked: only a regex check, the rest is posted.</summary>
    private void OnLineFed(string line)
    {
        var highlighter = Highlighter;
        if (!highlighter.HasNotifications || highlighter.Notification(line) is null)
            return;

        // At most one waiting on the UI thread, however fast matching lines arrive.
        if (Environment.TickCount64 - Interlocked.Read(ref _lastTriggerTicks) < TriggerPause || Interlocked.Exchange(ref _triggerPending, 1) == 1)
            return;

        var text = line.Trim();
        if (text.Length > 80)
            text = text[..80] + "…";
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _triggerPending, 0);
            Alert(text, isTrigger: true);
        });
    }

    /// <summary>One trigger notification per burst: a log spewing errors should not flash the taskbar forever.</summary>
    private const long TriggerPause = 10_000;

    private int _triggerPending;

    /// <summary>Times a submitted command so its end can be reported when it ran long in the background.</summary>
    private void WatchCommand(string command)
    {
        StopWatchingCommand();
        if (_disposed || _settings.Current.NotifyLongCommandSeconds <= 0)
            return;

        _watchedCommand = command.Trim();
        _commandStartTicks = Environment.TickCount64;
        _commandTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _commandTimer.Tick += (_, _) => CheckCommandFinished();
        _commandTimer.Start();
    }

    private void StopWatchingCommand()
    {
        _commandTimer?.Stop();
        _commandTimer = null;
        _watchedCommand = null;
    }

    /// <summary>The command is done when output has paused and the cursor sits after a shell prompt.</summary>
    private void CheckCommandFinished()
    {
        if (State != SessionState.Connected || _watchedCommand is not { } command)
        {
            StopWatchingCommand();
            return;
        }

        var lastOutput = Interlocked.Read(ref _lastOutputTicks);
        if (Environment.TickCount64 - lastOutput < 2000 || lastOutput < _commandStartTicks)
            return;

        var lines = Terminal.GetPendingLines();
        if (lines.Count == 0 || !TerminalEmulator.LooksLikePrompt(lines[^1].Text))
            return;

        StopWatchingCommand();
        var duration = TimeSpan.FromMilliseconds(lastOutput - _commandStartTicks);
        if (duration.TotalSeconds >= _settings.Current.NotifyLongCommandSeconds)
            Alert($"finished {(command.Length > 60 ? command[..60] + "…" : command)} after {FormatDuration(duration)}");
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
        : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
        : $"{duration.Seconds}s";

    /// <summary>Reports an event unless the user is already looking at this tab.</summary>
    private void Alert(string message, bool isTrigger = false)
    {
        var appActive = _attention?.IsAppActive ?? true;
        if (_disposed || (IsActive && appActive))
            return;

        // Only a notification actually shown starts the pause (a match seen in the active tab does not).
        if (isTrigger)
            Interlocked.Exchange(ref _lastTriggerTicks, Environment.TickCount64);

        if (!IsActive)
            HasAlert = true;
        _status.Set($"{Title}: {message}", StatusLevel.Ok);
        _attention?.Request();
    }

    // ---------------------------------------------------------------- server resources

    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _statsCts;
    private string? _serverStats;

    public override string? ServerStats => _serverStats;

    private void SetServerStats(string? text)
    {
        if (_serverStats == text)
            return;
        _serverStats = text;
        OnPropertyChanged(nameof(ServerStats));
    }

    private void StartServerStats()
    {
        StopServerStats();
        if (IsLocal || _connection is not { } connection || !_settings.Current.ShowServerStats)
            return;

        _statsCts = new CancellationTokenSource();
        _ = PollServerStatsAsync(connection, _statsCts.Token);
    }

    private void StopServerStats()
    {
        if (_statsCts is { } cts)
        {
            _statsCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        SetServerStats(null);
    }

    /// <summary>Samples /proc over exec channels while the session lives; stops quietly on servers without it.</summary>
    private async Task PollServerStatsAsync(ISshConnection connection, CancellationToken token)
    {
        ServerSample? previous = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await connection.RunCommandAsync(ServerStatsProbe.Command, token);
                if (result.ExitCode != 0 || ServerStatsProbe.Parse(result.Output) is not { } sample)
                    return; // not a Linux server

                var text = Sshelfy.Core.Ssh.ServerStats.From(sample, previous).Format();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                        SetServerStats(text);
                });

                // CPU usage needs two samples, so the second one comes quickly.
                await Task.Delay(previous is null ? TimeSpan.FromSeconds(1) : StatsInterval, token);
                previous = sample;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The connection dropped; the session's own handlers report it.
        }
    }

    /// <summary>Set when the tab closes: work posted to the UI thread before that must not start timers again.</summary>
    private bool _disposed;

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        CancelAutoReconnect();
        _settings.Changed -= OnSettingsChanged;
        Terminal.Output -= OnTerminalOutput;
        Terminal.Resized -= OnTerminalResized;
        Terminal.LineFed -= OnLineFed;
        StopWatchingCommand();
        await TearDownAsync();
    }

    private void Fail(string message)
    {
        State = SessionState.Failed;
        ErrorMessage = message;
        _status.Set(message, StatusLevel.Error);
    }

    private void OnTerminalOutput(byte[] data) => _shell?.Write(data);

    private void OnShellData(byte[] data)
    {
        Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
        _recorder?.OnOutput(data);
        Terminal.Feed(data);
    }

    private void OnTerminalResized(object? sender, EventArgs e)
    {
        _shell?.Resize(Terminal.Columns, Terminal.Rows);
        _recorder?.OnResize(Terminal.Columns, Terminal.Rows);
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(StatusDetail)));
    }

    private void OnShellClosed(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => MarkDisconnected(null));

    private void OnConnectionLost(object? sender, string? message) => Dispatcher.UIThread.Post(() =>
    {
        if (sender != _connection)
            return; // a connection that was already replaced

        MarkDisconnected(message);

        // A message means the connection failed (network, server restart); "exit" or logout closes cleanly without one.
        // The shell may have reported its close first, so this also runs when the tab is already disconnected.
        if (message is not null && State == SessionState.Disconnected && !IsReconnecting && _settings.Current.AutoReconnect)
            _ = AutoReconnectAsync();
    });

    private void MarkDisconnected(string? message)
    {
        if (State != SessionState.Connected)
            return;

        StopServerStats();
        State = SessionState.Disconnected;
        ErrorMessage = message;
        LogSessionEnd(message);
        foreach (var tunnel in Tunnels)
            tunnel.MarkSessionEnded();
        _ = StopRecordingAsync();
        Terminal.Feed($"\r\n\x1b[2m[Session closed{(message is null ? string.Empty : ": " + message)}]\x1b[0m\r\n");
        if (IsActive)
            _status.Set(LocalShell is { } local && !IsExternalSsh ? $"{local.Name} exited" : $"Disconnected from {_host.Endpoint}");
    }

    private async Task TearDownAsync()
    {
        if (_connectCts is not null)
        {
            await _connectCts.CancelAsync();
            _connectCts.Dispose();
            _connectCts = null;
        }

        if (_shell is { } shell)
        {
            _shell = null;
            shell.DataReceived -= OnShellData;
            shell.Closed -= OnShellClosed;
            await shell.DisposeAsync();
        }

        StopServerStats();
        foreach (var tunnel in Tunnels)
            tunnel.MarkSessionEnded();

        await StopRecordingAsync();
        LogSessionEnd(null);

        if (_connection is { } connection)
        {
            _connection = null;
            connection.Disconnected -= OnConnectionLost;
            await connection.DisposeAsync();
        }
    }
}
