using System.Text.Json;

namespace Sshelfy.Core.Terminal;

public sealed record AsciicastEvent(double Time, char Type, string Data);

/// <summary>A parsed asciicast v2 recording.</summary>
public sealed record AsciicastRecording(int Width, int Height, string? Title, IReadOnlyList<AsciicastEvent> Events)
{
    public double Duration => Events.Count == 0 ? 0 : Events[^1].Time;

    /// <summary>Event times with long pauses shortened to <paramref name="maxPause"/> seconds.</summary>
    public IReadOnlyList<double> CompressedTimes(double maxPause)
    {
        var times = new double[Events.Count];
        double previous = 0, shifted = 0;
        for (var i = 0; i < Events.Count; i++)
        {
            var gap = Events[i].Time - previous;
            shifted += Math.Min(Math.Max(gap, 0), maxPause);
            times[i] = shifted;
            previous = Events[i].Time;
        }

        return times;
    }
}

public static class AsciicastParser
{
    /// <exception cref="FormatException">Not an asciicast v2 file.</exception>
    public static AsciicastRecording Parse(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            throw new FormatException("The recording is empty.");

        int width, height;
        string? title;
        try
        {
            using var header = JsonDocument.Parse(lines[0]);
            var root = header.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 2)
                throw new FormatException("Only asciicast version 2 is supported.");
            width = root.GetProperty("width").GetInt32();
            height = root.GetProperty("height").GetInt32();
            title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("The recording header is not valid asciicast.", ex);
        }

        var events = new List<AsciicastEvent>(lines.Length - 1);
        foreach (var line in lines.Skip(1))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3)
                    continue;
                var type = e[1].GetString();
                if (string.IsNullOrEmpty(type))
                    continue;
                events.Add(new AsciicastEvent(e[0].GetDouble(), type[0], e[2].GetString() ?? string.Empty));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // A truncated last line (recording interrupted) is skipped.
            }
        }

        return new AsciicastRecording(width, height, title, events);
    }
}
