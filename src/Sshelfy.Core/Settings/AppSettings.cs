namespace Sshelfy.Core.Settings;

/// <summary>
/// User preferences persisted between runs. Layout values are stored in device-independent pixels.
/// </summary>
public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.System;

    public bool IsSidebarOpen { get; init; } = true;
    public double SidebarWidth { get; init; } = 280;

    public bool IsBottomPanelOpen { get; init; }
    public double BottomPanelHeight { get; init; } = 200;

    public string TerminalFontFamily { get; init; } = "Cascadia Mono, Consolas, SF Mono, Menlo, DejaVu Sans Mono, monospace";
    public double TerminalFontSize { get; init; } = 13;
    public int TerminalScrollbackLines { get; init; } = 5000;

    /// <summary>Terminal color scheme by name; null follows the app's light or dark theme.</summary>
    public string? TerminalThemeName { get; init; }

    /// <summary>Color schemes imported by the user (Windows Terminal, iTerm2, base16).</summary>
    public IReadOnlyList<Terminal.TerminalTheme> CustomTerminalThemes { get; init; } = [];

    /// <summary>Regular expressions that color matching terminal text, optionally with a notification.</summary>
    public IReadOnlyList<Terminal.HighlightRule> HighlightRules { get; init; } = Terminal.HighlightRule.Defaults;

    /// <summary>Notify when a command running at least this long finishes in a background tab; 0 turns it off.</summary>
    public int NotifyLongCommandSeconds { get; init; } = 30;

    /// <summary>DISPLAY for X11 forwarding: where the local X server (VcXsrv, X410, Xming) listens.</summary>
    public string X11Display { get; init; } = "localhost:0.0";

    /// <summary>Editor for "Edit in external editor" (e.g. "code --wait"); empty uses the app the system associates with the file.</summary>
    public string? ExternalEditor { get; init; }

    /// <summary>Look for a newer release on GitHub once a day (only the version number is fetched).</summary>
    public bool CheckForUpdates { get; init; } = true;

    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>Hashes of host pre-connect commands the user approved (typed in the editor or allowed when asked).</summary>
    public IReadOnlyList<string> ApprovedPreConnectCommands { get; init; } = [];

    public string? LastLocalDirectory { get; init; }

    /// <summary>Remember commands typed in terminal sessions (per host).</summary>
    public bool RememberCommandHistory { get; init; } = true;

    /// <summary>Folder for session recordings; null uses logs/sessions in the data folder.</summary>
    public string? SessionLogDirectory { get; init; }

    public Terminal.SessionLogFormat SessionLogFormat { get; init; } = Terminal.SessionLogFormat.PlainText;

    /// <summary>Prefix every recorded line with the time (plain text format).</summary>
    public bool SessionLogTimestamps { get; init; } = true;

    /// <summary>Encrypt recordings with a key derived from the vault.</summary>
    public bool EncryptSessionLogs { get; init; }

    /// <summary>Recordings older than this are deleted at start-up; 0 keeps them.</summary>
    public int SessionLogRetentionDays { get; init; } = 30;

    /// <summary>A recording stops when it reaches this size.</summary>
    public int SessionLogMaxMegabytes { get; init; } = 100;

    /// <summary>The user saw the warning about sensitive data in recordings.</summary>
    public bool SessionLoggingAcknowledged { get; init; }

    /// <summary>How much goes into the diagnostic log (logs/app-*.log).</summary>
    public DiagnosticLevel DiagnosticLevel { get; init; } = DiagnosticLevel.Normal;

    /// <summary>Lock the vault after this many minutes without input in the app; 0 = never.</summary>
    public int VaultAutoLockMinutes { get; init; }

    /// <summary>Lock the vault when the computer's screen is locked.</summary>
    public bool LockVaultWithScreen { get; init; } = true;

    /// <summary>SFTP tabs show the local computer next to the server.</summary>
    public bool SftpDualPane { get; init; }

    /// <summary>The file browser shows a preview of the selected file.</summary>
    public bool SftpPreview { get; init; }

    /// <summary>After a dropped connection (not a normal logout), keep trying to reconnect with growing pauses.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>Show CPU, memory, disk and uptime of the active SSH server in the status bar (Linux servers).</summary>
    public bool ShowServerStats { get; init; } = true;

    /// <summary>Reopen the terminal and SFTP tabs of saved hosts when the app starts.</summary>
    public bool RestoreTabsOnStartup { get; init; }

    /// <summary>Tabs open when the app was last closed (saved hosts only, no secrets).</summary>
    public IReadOnlyList<SavedTab> LastSessionTabs { get; init; } = [];

    /// <summary>Named sets of tabs and split panes the user saved to open in one go.</summary>
    public IReadOnlyList<SavedLayout> Layouts { get; init; } = [];
}

public enum DiagnosticLevel
{
    /// <summary>Warnings and errors only.</summary>
    Errors,

    /// <summary>Plus connections, disconnects and other notable events.</summary>
    Normal,

    /// <summary>Everything, for troubleshooting.</summary>
    Verbose,
}

public enum SavedTabKind
{
    Terminal,
    Sftp,
    Local,

    /// <summary>A tab split into panes; see <see cref="SavedTab.Panes"/>.</summary>
    Split,
}

/// <param name="LocalShellId">For <see cref="SavedTabKind.Local"/>: the shell to start again.</param>
/// <param name="Panes">For <see cref="SavedTabKind.Split"/>: the pane tree.</param>
public sealed record SavedTab(SavedTabKind Kind, Guid HostId, string? LocalShellId = null, SavedPane? Panes = null);

/// <summary>A node of a saved split: a terminal (<paramref name="Tab"/>) or two nodes side by side or stacked.</summary>
public sealed record SavedPane(SavedTab? Tab = null, bool SideBySide = false, SavedPane? First = null, SavedPane? Second = null)
{
    /// <summary>The terminals from left/top to right/bottom.</summary>
    public IEnumerable<SavedTab> Leaves() =>
        Tab is not null ? [Tab] : (First?.Leaves() ?? []).Concat(Second?.Leaves() ?? []);
}

/// <summary>A named set of tabs (with their splits) to open together.</summary>
public sealed record SavedLayout(string Name, IReadOnlyList<SavedTab> Tabs);
