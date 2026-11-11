using System.Buffers.Binary;

namespace Sshelfy.Core.Terminal;

/// <summary>
/// A picture placed in the terminal (sixel or the iTerm2 inline image protocol). It covers
/// <see cref="Columns"/>×<see cref="Rows"/> cells from <see cref="Column"/> on line <see cref="Line"/>
/// (an absolute line number, like search matches) and scrolls with the text.
/// </summary>
public sealed class TerminalImage
{
    public required int Id { get; init; }

    public required long Line { get; init; }

    public required int Column { get; init; }

    public required int Columns { get; init; }

    public required int Rows { get; init; }

    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    /// <summary>Decoded sixel pixels, 0xAARRGGBB, row by row; null for encoded files.</summary>
    public uint[]? Pixels { get; init; }

    /// <summary>A PNG, JPEG or GIF file (iTerm2 protocol); null for sixel.</summary>
    public byte[]? Encoded { get; init; }

    public bool OnAlternateScreen { get; init; }

    internal long ByteSize => (Pixels?.LongLength * 4 ?? 0) + (Encoded?.LongLength ?? 0);

    /// <summary>The same picture at another line number (after the scrollback was cleared).</summary>
    internal TerminalImage MovedTo(long line) => new()
    {
        Id = Id,
        Line = line,
        Column = Column,
        Columns = Columns,
        Rows = Rows,
        PixelWidth = PixelWidth,
        PixelHeight = PixelHeight,
        Pixels = Pixels,
        Encoded = Encoded,
        OnAlternateScreen = OnAlternateScreen,
    };
}

/// <summary>Decodes DEC sixel graphics into ARGB pixels.</summary>
public static class SixelDecoder
{
    public const int MaxSize = 4096;

    /// <summary>VT340 default palette (percent RGB).</summary>
    private static readonly (int R, int G, int B)[] DefaultPalette =
    [
        (0, 0, 0), (20, 20, 80), (80, 13, 13), (20, 80, 20), (80, 20, 80), (20, 80, 80), (80, 80, 20), (53, 53, 53),
        (26, 26, 26), (33, 33, 60), (60, 26, 26), (33, 60, 33), (60, 33, 60), (33, 60, 60), (60, 60, 33), (80, 80, 80),
    ];

    /// <param name="data">The DCS string after "ESC P": "P1;P2;P3q" followed by sixel data.</param>
    /// <returns>Null when the data holds no pixels or the image is too large.</returns>
    public static (uint[] Pixels, int Width, int Height)? Decode(string data)
    {
        var start = data.IndexOf('q');
        if (start < 0)
            return null;

        var palette = new uint[256];
        for (var i = 0; i < palette.Length; i++)
        {
            var (r, g, b) = DefaultPalette[i % DefaultPalette.Length];
            palette[i] = Rgb(r, g, b);
        }

        // Pixels are collected per band; the canvas grows as the image is drawn.
        var width = 0;
        var height = 0;
        var capacityWidth = 256;
        var capacityHeight = 256;
        var pixels = new uint[capacityWidth * capacityHeight];
        var x = 0;
        var y = 0;
        var color = palette[0];
        var i2 = start + 1;

        void Ensure(int needWidth, int needHeight)
        {
            if (needWidth <= capacityWidth && needHeight <= capacityHeight)
                return;
            var newWidth = Math.Max(capacityWidth, Math.Min(MaxSize, Math.Max(needWidth, capacityWidth * 2)));
            var newHeight = Math.Max(capacityHeight, Math.Min(MaxSize, Math.Max(needHeight, capacityHeight * 2)));
            var grown = new uint[newWidth * newHeight];
            for (var row = 0; row < capacityHeight; row++)
                Array.Copy(pixels, row * capacityWidth, grown, row * newWidth, capacityWidth);
            pixels = grown;
            capacityWidth = newWidth;
            capacityHeight = newHeight;
        }

        void Paint(int bits, int repeat)
        {
            if (x >= MaxSize || y >= MaxSize)
                return;
            repeat = Math.Min(repeat, MaxSize - x);
            if (bits != 0)
            {
                Ensure(x + repeat, Math.Min(MaxSize, y + 6));
                for (var bit = 0; bit < 6; bit++)
                {
                    if ((bits & (1 << bit)) == 0 || y + bit >= MaxSize)
                        continue;
                    var offset = (y + bit) * capacityWidth + x;
                    pixels.AsSpan(offset, repeat).Fill(color);
                    height = Math.Max(height, y + bit + 1);
                }

                width = Math.Max(width, x + repeat);
            }

            x += repeat;
        }

        int ReadNumber(ref int index)
        {
            var value = 0;
            var any = false;
            while (index < data.Length && data[index] is >= '0' and <= '9')
            {
                value = Math.Min(value * 10 + (data[index] - '0'), 1_000_000);
                index++;
                any = true;
            }

            return any ? value : -1;
        }

        while (i2 < data.Length)
        {
            var ch = data[i2];
            switch (ch)
            {
                case >= '?' and <= '~':
                    Paint(ch - '?', 1);
                    i2++;
                    break;
                case '!':
                {
                    i2++;
                    var count = Math.Max(1, ReadNumber(ref i2));
                    if (i2 < data.Length && data[i2] is >= '?' and <= '~')
                    {
                        Paint(data[i2] - '?', count);
                        i2++;
                    }

                    break;
                }
                case '$':
                    x = 0;
                    i2++;
                    break;
                case '-':
                    x = 0;
                    y += 6;
                    i2++;
                    break;
                case '"':
                {
                    // Raster attributes: aspect numerator;denominator;width;height (the size pre-allocates).
                    i2++;
                    var values = new List<int>();
                    do
                    {
                        if (i2 < data.Length && data[i2] == ';')
                            i2++;
                        values.Add(ReadNumber(ref i2));
                    }
                    while (i2 < data.Length && data[i2] == ';');
                    if (values.Count >= 4 && values[2] > 0 && values[3] > 0)
                        Ensure(Math.Min(MaxSize, values[2]), Math.Min(MaxSize, values[3]));
                    break;
                }
                case '#':
                {
                    i2++;
                    var index = ReadNumber(ref i2);
                    var values = new List<int>();
                    while (i2 < data.Length && data[i2] == ';')
                    {
                        i2++;
                        values.Add(ReadNumber(ref i2));
                    }

                    if (index < 0)
                        break;
                    index %= palette.Length;
                    if (values.Count >= 4)
                    {
                        palette[index] = values[0] == 1
                            ? Hls(values[1], values[2], values[3])
                            : Rgb(values[1], values[2], values[3]);
                    }

                    color = palette[index];
                    break;
                }
                default:
                    i2++;
                    break;
            }
        }

        if (width == 0 || height == 0)
            return null;

        var result = new uint[width * height];
        for (var row = 0; row < height; row++)
            Array.Copy(pixels, row * capacityWidth, result, row * width, width);
        return (result, width, height);
    }

    private static uint Rgb(int r, int g, int b) =>
        0xFF000000u | (Percent(r) << 16) | (Percent(g) << 8) | Percent(b);

    private static uint Percent(int value) => (uint)Math.Round(Math.Clamp(value, 0, 100) * 255 / 100.0);

    /// <summary>Sixel HLS: hue in degrees with blue at 0, lightness and saturation in percent.</summary>
    private static uint Hls(int hue, int lightness, int saturation)
    {
        var h = ((hue + 240) % 360) / 360.0; // sixel puts blue at 0°, standard HLS puts red there
        var l = Math.Clamp(lightness, 0, 100) / 100.0;
        var s = Math.Clamp(saturation, 0, 100) / 100.0;
        if (s == 0)
            return Rgb((int)(l * 100), (int)(l * 100), (int)(l * 100));

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Rgb((int)Math.Round(Channel(p, q, h + 1 / 3.0) * 100), (int)Math.Round(Channel(p, q, h) * 100), (int)Math.Round(Channel(p, q, h - 1 / 3.0) * 100));

        static double Channel(double p, double q, double t)
        {
            t = (t + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t
                : t < 0.5 ? q
                : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6
                : p;
        }
    }
}

/// <summary>Reads the pixel size from PNG, GIF and JPEG headers (inline images arrive as files).</summary>
public static class ImageHeader
{
    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> file)
    {
        // PNG: signature, then the IHDR chunk with big-endian width and height.
        if (file.Length >= 24 && file[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return Valid(BinaryPrimitives.ReadInt32BigEndian(file[16..]), BinaryPrimitives.ReadInt32BigEndian(file[20..]));

        // GIF87a / GIF89a: little-endian logical screen size.
        if (file.Length >= 10 && file[0] == 'G' && file[1] == 'I' && file[2] == 'F')
            return Valid(BinaryPrimitives.ReadUInt16LittleEndian(file[6..]), BinaryPrimitives.ReadUInt16LittleEndian(file[8..]));

        // JPEG: walk the segments to a start-of-frame marker.
        if (file.Length >= 4 && file[0] == 0xFF && file[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < file.Length)
            {
                if (file[i] != 0xFF)
                    return null;
                var marker = file[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(file[(i + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return Valid(BinaryPrimitives.ReadUInt16BigEndian(file[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(file[(i + 5)..]));
                i += 2 + length;
            }
        }

        return null;
    }

    /// <summary>Decoded pictures cost 4 bytes per pixel: a tiny compressed file must not ask for gigabytes.</summary>
    public const long MaxPixels = 4096L * 4096;

    private static (int, int)? Valid(int width, int height) =>
        width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height <= MaxPixels ? (width, height) : null;
}
