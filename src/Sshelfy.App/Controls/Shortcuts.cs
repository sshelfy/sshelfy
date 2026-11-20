using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Sshelfy.App.Controls;

// Windows uses Ctrl+Shift so plain Ctrl keys stay with the shell; macOS uses Cmd, which the shell never gets.
public static class Shortcuts
{
    private static readonly bool IsMac = OperatingSystem.IsMacOS();

    public static KeyModifiers CommandModifier => IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    private static readonly Dictionary<string, KeyGesture> All = new()
    {
        ["ToggleSidebar"] = Pick(Key.B, Ctrl | Shift, Key.B, Cmd),
        ["ToggleBottomPanel"] = Pick(Key.J, Ctrl | Shift, Key.J, Cmd),
        ["FindInTerminal"] = Pick(Key.F, Ctrl | Shift, Key.F, Cmd),
        ["CommandPalette"] = Pick(Key.P, Ctrl | Shift, Key.P, Cmd | Shift),
        ["PaletteAlternate"] = Pick(Key.Enter, Ctrl, Key.Enter, Cmd),
        ["NewLocalTerminal"] = Pick(Key.T, Ctrl | Shift, Key.T, Cmd),
        ["CloseTab"] = Pick(Key.W, Ctrl | Shift, Key.W, Cmd),
        ["SplitRight"] = Pick(Key.D, Ctrl | Shift, Key.D, Cmd),
        ["SplitDown"] = Pick(Key.E, Ctrl | Shift, Key.D, Cmd | Shift),
        ["SplitSftpRight"] = Pick(Key.O, Ctrl | Shift, Key.O, Cmd | Shift),
        ["NextTab"] = Pick(Key.Tab, Ctrl, Key.Tab, Ctrl),
        ["PreviousTab"] = Pick(Key.Tab, Ctrl | Shift, Key.Tab, Ctrl | Shift),
        ["NewHost"] = Pick(Key.N, Ctrl | Shift, Key.N, Cmd),
        ["QuickConnect"] = Pick(Key.K, Ctrl | Shift, Key.K, Cmd | Shift),
        ["Settings"] = Pick(Key.OemComma, Ctrl, Key.OemComma, Cmd),
        ["Save"] = Pick(Key.S, Ctrl, Key.S, Cmd),
        ["Refresh"] = Pick(Key.R, Ctrl, Key.R, Cmd),
        ["Copy"] = Pick(Key.C, Ctrl | Shift, Key.C, Cmd),
        ["Paste"] = Pick(Key.V, Ctrl | Shift, Key.V, Cmd),
        ["DeleteQuick"] = Pick(Key.Delete, Shift, Key.Back, Cmd),
    };

    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Cmd = KeyModifiers.Meta;

    public static KeyGesture Get(string name) =>
        All.TryGetValue(name, out var gesture) ? gesture : throw new ArgumentException($"Unknown shortcut '{name}'.", nameof(name));

    public static string Text(string name) => Text(Get(name));

    public static string Text(KeyGesture gesture) => IsMac ? MacText(gesture) : WindowsText(gesture);

    public static bool Matches(string name, KeyEventArgs e) => Get(name).Matches(e);

    private static KeyGesture Pick(Key windowsKey, KeyModifiers windows, Key macKey, KeyModifiers mac) =>
        IsMac ? new KeyGesture(macKey, mac) : new KeyGesture(windowsKey, windows);

    internal static string WindowsText(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        parts.Add(KeyText(gesture.Key, mac: false));
        return string.Join("+", parts);
    }

    internal static string MacText(KeyGesture gesture)
    {
        var text = string.Empty;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
            text += "⌃";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt))
            text += "⌥";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift))
            text += "⇧";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta))
            text += "⌘";
        return text + KeyText(gesture.Key, mac: true);
    }

    private static string KeyText(Key key, bool mac) => key switch
    {
        Key.OemComma => ",",
        Key.Enter => mac ? "↩" : "Enter",
        Key.Back => mac ? "⌫" : "Backspace",
        Key.Delete => mac ? "⌦" : "Del",
        Key.Tab => mac ? "⇥" : "Tab",
        _ => key.ToString(),
    };
}

public sealed class ShortcutExtension(string name) : MarkupExtension
{
    public string? Label { get; set; }

    public bool AsText { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Label is not null ? $"{Label} ({Shortcuts.Text(name)})"
        : AsText ? Shortcuts.Text(name)
        : Shortcuts.Get(name);
}
