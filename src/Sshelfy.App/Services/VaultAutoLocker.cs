using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Sshelfy.App.ViewModels;
using Sshelfy.Core.Security;
using Sshelfy.Core.Services;

namespace Sshelfy.App.Services;

/// <summary>
/// Locks the vault after a period without input in the app, and (on Windows) when the screen is
/// locked. Open sessions keep running; only the vault key leaves memory.
/// </summary>
internal sealed class VaultAutoLocker
{
    private const uint WmWtsSessionChange = 0x02B1;
    private const int WtsSessionLock = 0x7;

    private readonly ISecretVault _vault;
    private readonly ISettingsService _settings;
    private readonly StatusBarViewModel _status;
    private readonly ILogger<VaultAutoLocker> _logger;
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    public VaultAutoLocker(ISecretVault vault, ISettingsService settings, StatusBarViewModel status, ILogger<VaultAutoLocker> logger)
    {
        _vault = vault;
        _settings = settings;
        _status = status;
        _logger = logger;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => CheckIdle();
        _vault.StateChanged += (_, _) => _lastActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>Watches the main window's input and the session lock notification.</summary>
    public void Attach(Window window)
    {
        void Touch(object? sender, RoutedEventArgs e) => _lastActivity = DateTimeOffset.UtcNow;
        window.AddHandler(InputElement.KeyDownEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.TextInputEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        _timer.Start();

        if (OperatingSystem.IsWindows())
            window.Opened += (_, _) => WatchSessionLock(window);
    }

    private void CheckIdle()
    {
        var minutes = _settings.Current.VaultAutoLockMinutes;
        if (minutes <= 0 || _vault.State != VaultState.Unlocked)
            return;
        if (DateTimeOffset.UtcNow - _lastActivity < TimeSpan.FromMinutes(minutes))
            return;

        _vault.Lock();
        _logger.LogInformation("Vault locked after {Minutes} minutes without activity", minutes);
        _status.Set($"Vault locked after {(minutes == 1 ? "1 minute" : $"{minutes} minutes")} without activity");
    }

    private void WatchSessionLock(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            return;
        if (!WTSRegisterSessionNotification(hwnd, 0))
        {
            _logger.LogWarning("Could not subscribe to screen lock notifications");
            return;
        }

        Win32Properties.AddWndProcHookCallback(window, (IntPtr _, uint message, IntPtr wParam, IntPtr _, ref bool _) =>
        {
            if (message == WmWtsSessionChange && wParam.ToInt32() == WtsSessionLock &&
                _settings.Current.LockVaultWithScreen && _vault.State == VaultState.Unlocked)
            {
                _vault.Lock();
                _logger.LogInformation("Vault locked because the screen was locked");
                Dispatcher.UIThread.Post(() => _status.Set("Vault locked with the screen"));
            }

            return IntPtr.Zero;
        });
        window.Closed += (_, _) => WTSUnRegisterSessionNotification(hwnd);
    }

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}