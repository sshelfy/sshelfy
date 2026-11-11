using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Sshelfy.App.ViewModels.Dialogs;
using Sshelfy.App.Views.Dialogs;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Security;

namespace Sshelfy.App.Tests;

public sealed class DialogTests
{
    [AvaloniaFact]
    public async Task Creating_the_vault_from_the_dialog_uses_the_typed_password()
    {
        var vault = new FakeVault { State = VaultState.NotCreated };
        var dialog = new VaultDialogViewModel(vault);
        var window = UiHelpers.Show(new VaultDialogView { DataContext = dialog });
        var boxes = window.FindAll<TextBox>().ToList();

        window.Type(boxes[0], "correct horse");
        window.Type(boxes[1], "correct horse");
        window.Press(Key.Enter, physical: PhysicalKey.Enter);
        await UiHelpers.SettleAsync();

        Assert.Equal("correct horse", vault.CreatedWith);
        Assert.Equal(true, await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task Mismatched_or_short_passwords_show_an_error_and_create_nothing()
    {
        var vault = new FakeVault { State = VaultState.NotCreated };
        var dialog = new VaultDialogViewModel(vault);
        var window = UiHelpers.Show(new VaultDialogView { DataContext = dialog });
        var boxes = window.FindAll<TextBox>().ToList();

        window.Type(boxes[0], "correct horse");
        window.Type(boxes[1], "correct hose");
        window.Click(window.Find<Button>(b => b.Command == dialog.SubmitCommand));
        await UiHelpers.SettleAsync();

        Assert.Null(vault.CreatedWith);
        Assert.Contains(window.FindAll<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "The passwords don't match.");
    }

    [AvaloniaFact]
    public async Task A_wrong_master_password_keeps_the_vault_locked()
    {
        var vault = new FakeVault { State = VaultState.Locked };
        var dialog = new VaultDialogViewModel(vault);
        var window = UiHelpers.Show(new VaultDialogView { DataContext = dialog });

        window.Type(window.Find<TextBox>(), "guess");
        window.Press(Key.Enter, physical: PhysicalKey.Enter);
        await UiHelpers.SettleAsync();

        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Equal("Incorrect password.", dialog.Error);

        window.Type(window.Find<TextBox>(), "right password");
        window.Press(Key.Enter, physical: PhysicalKey.Enter);
        await UiHelpers.SettleAsync();
        Assert.Equal(VaultState.Unlocked, vault.State);
    }

    [AvaloniaFact]
    public async Task Host_editor_saves_what_was_typed_and_validates_the_port()
    {
        var vault = new FakeVault();
        var catalog = new HostCatalog(new MemoryHosts(), vault, new NoKeys());
        await catalog.InitializeAsync();
        var dialog = new HostEditorDialogViewModel(catalog, new ScriptedDialogs(), new OpenVaultGate(), new NoFiles(), new NoAgent(), null, null, false);
        var window = UiHelpers.Show(new HostEditorDialogView { DataContext = dialog });
        var save = window.Find<Button>(b => b.Command == dialog.SaveCommand);

        Assert.False(save.IsEffectivelyEnabled); // address and user are required

        window.Type(window.Find<TextBox>(b => b.PlaceholderText == "e.g. Production web"), "Production web");
        window.Type(window.Find<TextBox>(b => b.PlaceholderText == "hostname or IP"), "web.example.com");
        window.Type(window.Find<TextBox>(b => b.PlaceholderText == "root"), "deploy");
        var port = window.Find<TextBox>(b => b.Text == "22");
        port.SelectAll();
        window.Type(port, "70000");
        Assert.True(save.IsEffectivelyEnabled);

        window.Click(save);
        await UiHelpers.SettleAsync();
        Assert.Equal("Port must be a number between 1 and 65535.", dialog.Error);
        Assert.Empty(catalog.Hosts);

        port.SelectAll();
        window.Type(port, "2222");
        window.Click(save);
        await UiHelpers.SettleAsync();

        var host = Assert.Single(catalog.Hosts);
        Assert.Equal(("Production web", "web.example.com", 2222, "deploy"), (host.Name, host.Address, host.Port, host.Username));
    }
}