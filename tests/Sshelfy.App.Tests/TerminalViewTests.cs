using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Sshelfy.App.Controls;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.Tests;

public sealed class TerminalViewTests
{
    private static (Window Window, TerminalView View, TerminalEmulator Terminal, StringBuilder Sent) Open()
    {
        var terminal = new TerminalEmulator(80, 24);
        var sent = new StringBuilder();
        terminal.Output += data => sent.Append(Encoding.UTF8.GetString(data));
        var view = new TerminalView { Terminal = terminal };
        var window = UiHelpers.Show(view);
        view.Focus();
        return (window, view, terminal, sent);
    }

    [AvaloniaFact]
    public void Typed_text_and_enter_reach_the_session()
    {
        var (window, _, _, sent) = Open();

        window.KeyTextInput("ls -la");
        window.Press(Key.Enter, physical: PhysicalKey.Enter);

        Assert.Equal("ls -la\r", sent.ToString());
    }

    [AvaloniaFact]
    public void Ctrl_letter_sends_a_control_code_but_ctrl_shift_letters_are_left_to_the_app()
    {
        var (window, _, _, sent) = Open();

        window.Press(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Assert.Equal("\u0003", sent.ToString());

        sent.Clear();
        window.Press(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, "F");
        Assert.Equal(string.Empty, sent.ToString());
    }

    [AvaloniaFact]
    public void Arrow_keys_follow_the_application_cursor_mode()
    {
        var (window, _, terminal, sent) = Open();

        window.Press(Key.Up, physical: PhysicalKey.ArrowUp);
        terminal.Feed("\u001b[?1h");
        window.Press(Key.Up, physical: PhysicalKey.ArrowUp);

        Assert.Equal("\u001b[A\u001bOA", sent.ToString());
    }

    [AvaloniaFact]
    public async Task Copy_puts_the_selection_on_the_clipboard_and_paste_types_it()
    {
        var (window, view, terminal, sent) = Open();
        terminal.Feed("hello clipboard");
        var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!;

        view.SelectAll();
        await view.CopySelectionAsync();
        Assert.Contains("hello clipboard", await clipboard.TryGetTextAsync());

        await clipboard.SetTextAsync("echo pasted");
        await view.PasteAsync();
        Assert.Equal("echo pasted", sent.ToString());
        _ = window;
    }

    [AvaloniaFact]
    public async Task Hovering_a_url_underlines_it_and_shows_where_it_goes()
    {
        var (window, view, terminal, _) = Open();
        terminal.Feed("docs at https://example.com/guide for details");
        await UiHelpers.SettleAsync();

        window.MouseMove(view.TranslatePoint(view.CellCenter(12, 0), window)!.Value, RawInputModifiers.None);
        Assert.Equal("https://example.com/guide", view.HoverLink?.Uri);
        Assert.Contains("https://example.com/guide", ToolTip.GetTip(view) as string);

        window.MouseMove(view.TranslatePoint(view.CellCenter(2, 0), window)!.Value, RawInputModifiers.None);
        Assert.Null(view.HoverLink);
    }

    [AvaloniaFact]
    public void Ctrl_shift_v_pastes_through_the_keyboard()
    {
        var (window, view, _, sent) = Open();
        TopLevel.GetTopLevel(view)!.Clipboard!.SetTextAsync("whoami").GetAwaiter().GetResult();

        window.Press(Key.V, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.V, "V");

        Assert.Equal("whoami", sent.ToString());
    }
}