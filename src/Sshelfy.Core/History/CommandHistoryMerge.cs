namespace Sshelfy.Core.History;

public static class CommandHistoryMerge
{
    /// <summary>
    /// Combines stored history with entries recorded while it could not be read (e.g. the vault was
    /// locked): the same command keeps its latest time and the use counts add up.
    /// </summary>
    public static CommandHistoryData Merge(CommandHistoryData stored, CommandHistoryData recent)
    {
        var result = new CommandHistoryData();
        foreach (var key in stored.Hosts.Keys.Union(recent.Hosts.Keys))
        {
            stored.Hosts.TryGetValue(key, out var old);
            recent.Hosts.TryGetValue(key, out var fresh);

            var byCommand = new Dictionary<string, CommandHistoryEntry>(StringComparer.Ordinal);
            foreach (var entry in old?.Entries ?? [])
                byCommand[entry.Command] = entry;
            foreach (var entry in fresh?.Entries ?? [])
            {
                byCommand[entry.Command] = byCommand.TryGetValue(entry.Command, out var existing)
                    ? entry with
                    {
                        LastUsedAt = entry.LastUsedAt > existing.LastUsedAt ? entry.LastUsedAt : existing.LastUsedAt,
                        UseCount = existing.UseCount + entry.UseCount,
                    }
                    : entry;
            }

            result.Hosts[key] = new HostHistory
            {
                HostName = fresh?.HostName ?? old?.HostName ?? string.Empty,
                Entries = byCommand.Values.OrderByDescending(e => e.LastUsedAt).ToList(),
            };
        }

        return result;
    }
}