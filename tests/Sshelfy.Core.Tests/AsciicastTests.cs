using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class AsciicastTests
{
    private const string Sample = """
        {"version": 2, "width": 100, "height": 30, "timestamp": 1700000000, "title": "web-01"}
        [0.5, "o", "hello "]
        [1.0, "o", "world\r\n"]
        [31.0, "o", "after a long pause"]
        [31.5, "r", "120x40"]
        [32.0, "o", "trunc
        """;

    [Fact]
    public void Parses_header_and_events_and_skips_truncated_line()
    {
        var recording = AsciicastParser.Parse(Sample);

        Assert.Equal((100, 30, "web-01"), (recording.Width, recording.Height, recording.Title));
        Assert.Equal(4, recording.Events.Count);
        Assert.Equal('r', recording.Events[3].Type);
        Assert.Equal(31.5, recording.Duration);
    }

    [Fact]
    public void Long_pauses_are_compressed()
    {
        var times = AsciicastParser.Parse(Sample).CompressedTimes(maxPause: 2);

        Assert.Equal([0.5, 1.0, 3.0, 3.5], times);
    }

    [Fact]
    public void Wrong_version_is_rejected()
    {
        Assert.Throws<FormatException>(() => AsciicastParser.Parse("{\"version\": 1}\n"));
        Assert.Throws<FormatException>(() => AsciicastParser.Parse("not json"));
    }
}
