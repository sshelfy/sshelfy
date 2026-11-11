using System.Text;
using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class StartupScriptTests
{
    [Fact]
    public void Exports_variables_then_runs_the_command()
    {
        EnvironmentVariable[] environment = [new("APP_ENV", "staging"), new("GREETING", "it's me"), new("bad name", "x")];

        Assert.Equal(
            @"export APP_ENV='staging' GREETING='it'\''s me'; cd /srv && tmux attach",
            StartupScript.Build(environment, "  cd /srv && tmux attach "));
        Assert.Null(StartupScript.Build([], "   "));
        Assert.Equal("ls; exec \"${SHELL:-/bin/sh}\" -l", StartupScript.RemoteCommand([], "ls"));
    }

    [Fact]
    public void Parses_name_value_lines()
    {
        var parsed = StartupScript.Parse("# comment\nAPP_ENV=staging\r\n\nURL=http://x/?a=b\n", out var error);

        Assert.Null(error);
        Assert.Equal([new EnvironmentVariable("APP_ENV", "staging"), new EnvironmentVariable("URL", "http://x/?a=b")], parsed);
        Assert.Equal("APP_ENV=staging\nURL=http://x/?a=b", StartupScript.Format(parsed!));

        Assert.Null(StartupScript.Parse("OK=1\n9LIVES=no", out error));
        Assert.StartsWith("Line 2:", error);
    }

    [Fact]
    public void OpenSsh_runs_the_startup_line_as_a_remote_command_with_a_terminal()
    {
        var host = new HostProfile { Address = "web", Username = "deploy", StartupCommand = "htop", Environment = [new("TERM_PROGRAM", "sshelfy")] };

        Assert.Equal(
            "-o ServerAliveInterval=30 -A -t -l deploy -- web \"export TERM_PROGRAM='sshelfy'; htop; exec \\\"${SHELL:-/bin/sh}\\\" -l\"",
            OpenSshCommandLine.Build(host, [], forwardAgent: true, forwardX11: false));
    }

    [SshFact]
    public async Task The_startup_line_runs_in_a_real_shell()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        var target = new ConnectionTarget { Address = endpoint[0], Port = int.Parse(endpoint[1]), Username = credentials[0], Password = credentials[1] };
        await using var connection = await new SshNetConnectionFactory(new AlwaysTrust()).ConnectAsync(target);
        await using var shell = await connection.OpenShellAsync(120, 30);
        var output = new StringBuilder();
        var seen = new TaskCompletionSource();
        shell.DataReceived += data =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data));
                if (output.ToString().Contains("marker:it's a test:done", StringComparison.Ordinal))
                    seen.TrySetResult();
            }
        };

        var line = StartupScript.Build([new EnvironmentVariable("SF_VALUE", "it's a test")], "echo \"marker:$SF_VALUE:done\"");
        shell.Write(Encoding.UTF8.GetBytes(" " + line + "\r"));

        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class AlwaysTrust : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
