using System.Globalization;

namespace Sshelfy.Core.Ssh;

/// <summary>One reading of a Linux server's counters, taken by <see cref="ServerStatsProbe.Command"/>.</summary>
public sealed record ServerSample(
    long CpuBusyTicks,
    long CpuTotalTicks,
    long MemoryTotalKb,
    long MemoryAvailableKb,
    long DiskTotalKb,
    long DiskUsedKb,
    TimeSpan Uptime,
    double Load1);

/// <summary>What the status bar shows: CPU usage needs two samples, the rest comes from the latest one.</summary>
public sealed record ServerStats(double? CpuPercent, long MemoryUsedKb, long MemoryTotalKb, double DiskPercent, TimeSpan Uptime, double Load1)
{
    public static ServerStats From(ServerSample current, ServerSample? previous)
    {
        double? cpu = null;
        if (previous is not null && current.CpuTotalTicks > previous.CpuTotalTicks)
        {
            var busy = current.CpuBusyTicks - previous.CpuBusyTicks;
            var total = current.CpuTotalTicks - previous.CpuTotalTicks;
            cpu = Math.Clamp(100.0 * busy / total, 0, 100);
        }

        var disk = current.DiskTotalKb > 0 ? 100.0 * current.DiskUsedKb / current.DiskTotalKb : 0;
        return new ServerStats(cpu, current.MemoryTotalKb - current.MemoryAvailableKb, current.MemoryTotalKb, disk, current.Uptime, current.Load1);
    }

    /// <summary>"CPU 12%  ·  RAM 1.2/3.8 GB  ·  Disk 45%  ·  up 3d 4h".</summary>
    public string Format()
    {
        var c = CultureInfo.InvariantCulture;
        var cpu = CpuPercent is { } p ? string.Create(c, $"CPU {p:0}%") : string.Create(c, $"Load {Load1:0.00}");
        return string.Create(c, $"{cpu}  ·  RAM {Gigabytes(MemoryUsedKb)}/{Gigabytes(MemoryTotalKb)} GB  ·  Disk {DiskPercent:0}%  ·  up {FormatUptime(Uptime)}");
    }

    private static string Gigabytes(long kb) => (kb / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture);

    internal static string FormatUptime(TimeSpan uptime) =>
        uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}d {uptime.Hours}h"
        : uptime.TotalHours >= 1 ? $"{uptime.Hours}h {uptime.Minutes}m"
        : $"{uptime.Minutes}m";
}

/// <summary>Reads CPU, memory, disk and uptime from /proc over an exec channel, leaving no trace in the terminal.</summary>
public static class ServerStatsProbe
{
    private const string Separator = "--sshelfy--";

    /// <summary>POSIX command printing the sections <see cref="Parse"/> reads; fails on servers without /proc.</summary>
    public const string Command =
        "LC_ALL=C; head -n1 /proc/stat && echo " + Separator +
        " && cat /proc/meminfo && echo " + Separator +
        " && df -kP / | tail -n1 && echo " + Separator +
        " && cat /proc/uptime /proc/loadavg";

    /// <summary>Null when the output is not what a Linux server prints (e.g. a BSD or Windows host).</summary>
    public static ServerSample? Parse(string output)
    {
        var sections = output.Replace("\r", string.Empty).Split(Separator + "\n");
        if (sections.Length != 4)
            return null;

        // cpu  user nice system idle iowait irq softirq steal …
        var cpu = Fields(sections[0]);
        if (cpu.Length < 5 || cpu[0] != "cpu")
            return null;
        var ticks = cpu.Skip(1).Take(8).Select(Long).ToArray();
        if (ticks.Any(t => t is null))
            return null;
        var total = ticks.Sum(t => t!.Value);
        var idle = ticks[3]!.Value + (ticks.Length > 4 ? ticks[4]!.Value : 0);

        long? memTotal = null, memAvailable = null, memFree = null, buffers = null, cached = null;
        foreach (var line in sections[1].Split('\n'))
        {
            var parts = Fields(line);
            if (parts.Length < 2)
                continue;
            var value = Long(parts[1]);
            switch (parts[0])
            {
                case "MemTotal:": memTotal = value; break;
                case "MemAvailable:": memAvailable = value; break;
                case "MemFree:": memFree = value; break;
                case "Buffers:": buffers = value; break;
                case "Cached:": cached = value; break;
            }
        }

        if (memTotal is null)
            return null;
        memAvailable ??= (memFree ?? 0) + (buffers ?? 0) + (cached ?? 0); // kernels before 3.14

        // Filesystem 1024-blocks Used Available Capacity Mounted-on
        var disk = Fields(sections[2]);
        if (disk.Length < 4 || Long(disk[1]) is not { } diskTotal || Long(disk[2]) is not { } diskUsed)
            return null;

        var tail = Fields(sections[3]);
        if (tail.Length < 3
            || !double.TryParse(tail[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var uptime)
            || !double.TryParse(tail[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var load))
            return null;

        return new ServerSample(total - idle, total, memTotal.Value, memAvailable.Value, diskTotal, diskUsed, TimeSpan.FromSeconds(uptime), load);
    }

    private static string[] Fields(string text) => text.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static long? Long(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
