using Sshelfy.Core.Hosts;
using Sshelfy.Core.Ssh;
using Sshelfy.Infrastructure.Ssh;

namespace Sshelfy.Core.Tests;

public sealed class ServerStatsTests
{
    private const string Output =
        "cpu  1000 0 500 8000 500 0 0 0 0 0\n" +
        "--sshelfy--\n" +
        "MemTotal:        4000000 kB\n" +
        "MemFree:          500000 kB\n" +
        "MemAvailable:    3000000 kB\n" +
        "Buffers:          100000 kB\n" +
        "--sshelfy--\n" +
        "overlay  100000000  45000000  55000000  45% /\n" +
        "--sshelfy--\n" +
        "273600.51 1000000.00\n" +
        "0.42 0.30 0.25 1/234 5678\n";

    [Fact]
    public void Parses_proc_output()
    {
        var sample = ServerStatsProbe.Parse(Output);

        Assert.NotNull(sample);
        Assert.Equal(10_000, sample.CpuTotalTicks);
        Assert.Equal(1_500, sample.CpuBusyTicks); // idle and iowait are not busy
        Assert.Equal(4_000_000, sample.MemoryTotalKb);
        Assert.Equal(3_000_000, sample.MemoryAvailableKb);
        Assert.Equal(45_000_000, sample.DiskUsedKb);
        Assert.Equal(TimeSpan.FromSeconds(273600.51), sample.Uptime);
        Assert.Equal(0.42, sample.Load1);
    }

    [Fact]
    public void Cpu_comes_from_the_difference_between_two_samples()
    {
        var first = ServerStatsProbe.Parse(Output)!;
        var second = first with { CpuBusyTicks = first.CpuBusyTicks + 250, CpuTotalTicks = first.CpuTotalTicks + 1000 };

        Assert.Equal("Load 0.42  ·  RAM 1.0/3.8 GB  ·  Disk 45%  ·  up 3d 4h", ServerStats.From(first, null).Format());
        Assert.Equal("CPU 25%  ·  RAM 1.0/3.8 GB  ·  Disk 45%  ·  up 3d 4h", ServerStats.From(second, first).Format());
    }

    [Theory]
    [InlineData("")]
    [InlineData("head: /proc/stat: No such file or directory\n")]
    [InlineData("cpu  1 2 3 4\n--sshelfy--\nnothing\n--sshelfy--\nx\n--sshelfy--\n1 2\n")]
    public void Output_from_other_systems_is_rejected(string output) => Assert.Null(ServerStatsProbe.Parse(output));

    [Theory]
    [InlineData(59, "0m")]
    [InlineData(3_660, "1h 1m")]
    [InlineData(90_000, "1d 1h")]
    public void Uptime_is_short(int seconds, string expected) =>
        Assert.Equal(expected, ServerStats.FormatUptime(TimeSpan.FromSeconds(seconds)));

    [SshFact]
    public async Task Reads_the_test_server()
    {
        var spec = Environment.GetEnvironmentVariable("SSHELFY_TEST_SSH")!;
        var at = spec.LastIndexOf('@');
        var credentials = spec[..at].Split(':', 2);
        var endpoint = spec[(at + 1)..].Split(':');
        var target = new ConnectionTarget { Address = endpoint[0], Port = int.Parse(endpoint[1]), Username = credentials[0], Password = credentials[1] };
        await using var connection = await new SshNetConnectionFactory(new AlwaysTrust()).ConnectAsync(target);

        var result = await connection.RunCommandAsync(ServerStatsProbe.Command);
        var sample = ServerStatsProbe.Parse(result.Output);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(sample);
        Assert.True(sample.MemoryTotalKb > 0 && sample.DiskTotalKb > 0 && sample.CpuTotalTicks > 0);
    }

    private sealed class AlwaysTrust : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(string host, int port, string algorithm, string fingerprint) => Task.FromResult(true);
    }
}
