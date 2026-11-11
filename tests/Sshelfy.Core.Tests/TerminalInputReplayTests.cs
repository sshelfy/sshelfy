using System.Text;
using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalInputReplayTests
{
    private static List<string> Capture(TerminalEmulator terminal)
    {
        var sent = new List<string>();
        terminal.Output += data => sent.Add(Encoding.UTF8.GetString(data));
        return sent;
    }

    [Fact]
    public void Replayed_keys_are_encoded_by_the_receiving_terminal()
    {
        var source = new TerminalEmulator(20, 5);
        var target = new TerminalEmulator(20, 5);
        target.Feed("\x1b[?1h"); // application cursor keys only on the target
        source.InputSent += target.Replay;
        var sourceOut = Capture(source);
        var targetOut = Capture(target);

        source.SendText("ls");
        source.SendKey(TerminalKey.Up);
        source.SendControl('c');

        Assert.Equal(["ls", "\x1b[A", "\x03"], sourceOut);
        Assert.Equal(["ls", "\x1bOA", "\x03"], targetOut);
    }

    [Fact]
    public void Replay_does_not_raise_input_sent_again()
    {
        var a = new TerminalEmulator(20, 5);
        var b = new TerminalEmulator(20, 5);
        var raised = 0;
        a.InputSent += b.Replay;
        b.InputSent += _ => raised++;

        a.Paste("echo hi");

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Protocol_replies_are_not_reported_as_input()
    {
        var terminal = new TerminalEmulator(20, 5);
        var inputs = new List<TerminalInput>();
        terminal.InputSent += inputs.Add;

        terminal.Feed("\x1b[c"); // device attributes request: the terminal answers on its own

        Assert.Empty(inputs);
    }
}
