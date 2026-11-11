using Avalonia.Threading;
using Sshelfy.App.ViewModels;
using Sshelfy.Core.History;

namespace Sshelfy.App.Services;

/// <summary>Sends snippets and history entries to the terminal the user is working in.</summary>
public interface ITerminalCommandSender
{
    bool HasTarget { get; }

    string? TargetName { get; }

    event EventHandler? TargetChanged;

    /// <returns>False when no connected terminal is available.</returns>
    bool Send(string command, bool run);
}

internal sealed class TerminalCommandSender : ITerminalCommandSender
{
    private readonly WorkspaceViewModel _workspace;
    private readonly StatusBarViewModel _status;

    public TerminalCommandSender(WorkspaceViewModel workspace, StatusBarViewModel status)
    {
        _workspace = workspace;
        _status = status;
        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.LastTerminal))
                TargetChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool HasTarget => _workspace.LastTerminal is { IsConnected: true };

    public string? TargetName => _workspace.LastTerminal?.Host.DisplayName;

    public event EventHandler? TargetChanged;

    public bool Send(string command, bool run)
    {
        var terminal = _workspace.LastTerminal;
        if (terminal is null || !terminal.SendCommand(command, run))
        {
            _status.Set("Open a connected terminal to run commands", StatusLevel.Error);
            return false;
        }

        _workspace.Activate(terminal);
        return true;
    }
}

/// <summary>Stores commands submitted in any terminal tab in the per-host history.</summary>
internal sealed class CommandHistoryRecorder
{
    public CommandHistoryRecorder(WorkspaceViewModel workspace, ICommandHistory history)
    {
        workspace.TerminalAdded += (_, tab) =>
            tab.CommandSubmitted += (_, command) => history.Record(tab.HistoryKey, tab.Host.DisplayName, command);
    }
}

internal static class RelativeTime
{
    public static string Format(DateTimeOffset time)
    {
        var elapsed = DateTimeOffset.UtcNow - time;
        return elapsed.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)elapsed.TotalMinutes} min ago",
            < 86400 => $"{(int)elapsed.TotalHours} h ago",
            < 172800 => "yesterday",
            _ => time.ToLocalTime().ToString("d MMM"),
        };
    }
}

internal static class UiThread
{
    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already there).</summary>
    public static void Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
