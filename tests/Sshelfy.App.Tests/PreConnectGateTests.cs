using Sshelfy.App.Services;
using Sshelfy.Core.Hosts;

namespace Sshelfy.App.Tests;

public sealed class PreConnectGateTests
{
    [Fact]
    public async Task Commands_from_elsewhere_ask_once_and_typed_ones_never()
    {
        var runner = new CountingRunner();
        var dialogs = new CountingDialogs { ConfirmResult = false };
        var settings = new MemorySettings();
        var gate = new PreConnectGate(runner, settings, dialogs);
        var imported = new HostProfile { Address = "h", Username = "u", PreConnectCommand = "calc.exe" };

        Assert.False(await gate.RunAsync(imported, knock: true, TestContext.Current.CancellationToken));
        Assert.Equal((1, 0), (dialogs.Confirms, runner.Runs));

        dialogs.ConfirmResult = true;
        Assert.True(await gate.RunAsync(imported, knock: true, TestContext.Current.CancellationToken));
        Assert.True(await gate.RunAsync(imported, knock: true, TestContext.Current.CancellationToken));
        Assert.Equal((2, 2), (dialogs.Confirms, runner.Runs)); // asked once more, then remembered

        // A changed command is a different command.
        Assert.True(await gate.RunAsync(imported with { PreConnectCommand = "calc.exe /x" }, knock: true, TestContext.Current.CancellationToken));
        Assert.Equal(3, dialogs.Confirms);

        var typed = new HostProfile { Address = "h", Username = "u", PreConnectCommand = "rasdial vpn" };
        gate.Approve(typed);
        Assert.True(await gate.RunAsync(typed, knock: false, TestContext.Current.CancellationToken));
        Assert.Equal(3, dialogs.Confirms);

        // Knocking alone needs no approval.
        Assert.True(await gate.RunAsync(new HostProfile { Address = "h", Username = "u", KnockSequence = [new(7000, false)] }, knock: true, TestContext.Current.CancellationToken));
        Assert.Equal(3, dialogs.Confirms);
    }

    private sealed class CountingRunner : IPreConnectRunner
    {
        public int Runs { get; private set; }

        public Task RunAsync(HostProfile host, bool knock, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingDialogs : IDialogService
    {
        public bool ConfirmResult { get; set; }

        public int Confirms { get; private set; }

        public Task<TResult?> ShowAsync<TResult>(ViewModels.Dialogs.DialogViewModel<TResult> dialog) => Task.FromResult<TResult?>(default);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool isDestructive = false)
        {
            Confirms++;
            return Task.FromResult(ConfirmResult);
        }

        public Task<string?> PromptAsync(string title, string label, string? initialValue = null, string confirmText = "Save") => Task.FromResult<string?>(null);
    }
}
