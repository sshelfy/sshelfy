using System.Text;
using Sshelfy.Core.Terminal;
using Sshelfy.Infrastructure.Windows;

namespace Sshelfy.App.Tests;

/// <summary>Runs a real cmd.exe behind ConPTY (Windows only).</summary>
public sealed class ConPtyTests
{
    [Fact]
    public async Task Detects_shells_and_runs_a_command_in_cmd()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var token = TestContext.Current.CancellationToken;
        var launcher = new ConPtyShellLauncher();
        var shells = await launcher.DetectAsync(token);
        var cmd = Assert.Single(shells, s => s.Id == "cmd");

        await using var shell = await launcher.StartAsync(cmd, 80, 25, Path.GetTempPath(), token);
        var output = new StringBuilder();
        var closed = new TaskCompletionSource();
        var seen = new TaskCompletionSource();
        shell.DataReceived += data =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data));
                if (output.ToString().Contains("sshelfy-ok-42", StringComparison.Ordinal) && output.ToString().Contains("echo", StringComparison.Ordinal))
                    seen.TrySetResult();
            }
        };
        shell.Closed += (_, _) => closed.TrySetResult();

        // cmd strips the carets, so only the command's output (not the echoed input) contains the whole marker.
        shell.Write(Encoding.UTF8.GetBytes("echo sshelfy-ok-^4^2\r"));
        if (await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(15), token)) != seen.Task)
        {
            lock (output)
                Assert.Fail("No output from cmd. Received: " + output.ToString().Replace("\u001b", "\\e"));
        }

        shell.Resize(100, 30);
        shell.Write(Encoding.UTF8.GetBytes("exit\r"));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
    }

    [Fact]
    public async Task Extra_environment_variables_reach_the_process()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var token = TestContext.Current.CancellationToken;
        var profile = new LocalShellProfile("cmd-env", "cmd", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c echo display=%DISPLAY%",
            new Dictionary<string, string> { ["DISPLAY"] = "localhost:7.0" });

        await using var shell = await new ConPtyShellLauncher().StartAsync(profile, 80, 25, cancellationToken: token);
        var output = new StringBuilder();
        var closed = new TaskCompletionSource();
        shell.DataReceived += data =>
        {
            lock (output)
                output.Append(Encoding.UTF8.GetString(data));
        };
        shell.Closed += (_, _) => closed.TrySetResult();

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        lock (output)
            Assert.Contains("display=localhost:7.0", output.ToString());
    }

    [Fact]
    public async Task Starting_a_missing_program_reports_a_readable_error()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var launcher = new ConPtyShellLauncher();
        var missing = new LocalShellProfile("nope", "Nope", @"C:\does\not\exist.exe");

        var error = await Assert.ThrowsAsync<LocalShellException>(() => launcher.StartAsync(missing, 80, 25, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Nope", error.Message);
    }
}
