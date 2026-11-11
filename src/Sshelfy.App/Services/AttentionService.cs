using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Sshelfy.App.Services;

/// <summary>Gets the user's attention for something that happened in a background tab.</summary>
public interface IAttentionService
{
    /// <summary>The main window has keyboard focus.</summary>
    bool IsAppActive { get; }

    /// <summary>Flashes the taskbar button until the window is activated (Windows); no-op elsewhere or when active.</summary>
    void Request();
}

internal sealed class AttentionService : IAttentionService
{
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public bool IsAppActive => MainWindow?.IsActive == true;

    public void Request()
    {
        if (!OperatingSystem.IsWindows() || MainWindow is not { IsActive: false } window || window.TryGetPlatformHandle()?.Handle is not { } hwnd)
            return;

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = hwnd,
            Flags = FlashTray | FlashUntilForeground,
        };
        FlashWindowEx(ref info);
    }

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
