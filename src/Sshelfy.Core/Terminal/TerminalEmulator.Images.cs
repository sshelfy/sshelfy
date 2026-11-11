namespace Sshelfy.Core.Terminal;

/// <summary>Inline images: DEC sixel (DCS … q) and the iTerm2 protocol (OSC 1337 ; File=…).</summary>
public sealed partial class TerminalEmulator
{
    private const int MaxImages = 64;
    private const long MaxImageBytes = 128L * 1024 * 1024;
    private readonly List<TerminalImage> _images = [];
    private int _nextImageId;

    /// <summary>Size of one cell in device-independent pixels; the view keeps it current so images get the right number of cells.</summary>
    public (double Width, double Height) CellPixelSize { get; set; } = (8, 16);

    /// <summary>Images on the current screen and in the scrollback, oldest first.</summary>
    public IReadOnlyList<TerminalImage> GetImages()
    {
        lock (SyncRoot)
        {
            PruneImages();
            return _images.Where(i => i.OnAlternateScreen == _altActive).ToList();
        }
    }

    void IVtHandler.DcsDispatch(string data)
    {
        // Sixel: optional numeric parameters, then 'q'.
        var final = 0;
        while (final < data.Length && (char.IsAsciiDigit(data[final]) || data[final] == ';'))
            final++;
        if (final >= data.Length || data[final] != 'q')
            return;

        if (SixelDecoder.Decode(data) is { } image)
            PlaceImage(image.Width, image.Height, image.Pixels, null, widthCells: null, heightCells: null);
    }

    /// <summary>OSC 1337 ; File=name=…;size=…;width=…;height=…;inline=1 : base64 file.</summary>
    private void InlineImage(string argument)
    {
        if (!argument.StartsWith("File=", StringComparison.Ordinal))
            return;

        var colon = argument.IndexOf(':');
        if (colon < 0)
            return;

        var options = argument[5..colon].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(o => o.Split('=', 2))
            .Where(o => o.Length == 2)
            .GroupBy(o => o[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);
        if (options.GetValueOrDefault("inline") != "1")
            return; // a download request, which a terminal tab does not take

        byte[] file;
        try
        {
            file = Convert.FromBase64String(argument[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return;
        }

        if (ImageHeader.ReadSize(file) is not { } size)
            return;

        var (cellWidth, cellHeight) = CellPixelSize;
        var widthCells = CellCount(options.GetValueOrDefault("width"), size.Width, cellWidth, Columns);
        var heightCells = CellCount(options.GetValueOrDefault("height"), size.Height, cellHeight, Rows);

        // With one dimension given, the other follows the picture's aspect ratio (the default preserveAspectRatio=1).
        if (options.GetValueOrDefault("preserveAspectRatio") != "0")
        {
            if (widthCells is { } w && heightCells is null)
                heightCells = Math.Max(1, (int)Math.Ceiling(w * cellWidth * size.Height / size.Width / cellHeight));
            else if (heightCells is { } h && widthCells is null)
                widthCells = Math.Max(1, (int)Math.Ceiling(h * cellHeight * size.Width / size.Height / cellWidth));
        }

        PlaceImage(size.Width, size.Height, null, file, widthCells, heightCells);
    }

    /// <summary>"N" cells, "Npx", "N%" of the screen or "auto" (null: from the picture's own size).</summary>
    private static int? CellCount(string? value, int pixels, double cellPixels, int screenCells)
    {
        if (string.IsNullOrEmpty(value) || value == "auto")
            return null;
        if (value.EndsWith("px", StringComparison.Ordinal) && int.TryParse(value[..^2], out var px))
            return Math.Max(1, (int)Math.Ceiling(px / cellPixels));
        if (value.EndsWith('%') && int.TryParse(value[..^1], out var percent))
            return Math.Max(1, screenCells * Math.Clamp(percent, 1, 100) / 100);
        return int.TryParse(value, out var cells) && cells > 0 ? cells : null;
    }

    /// <summary>Puts the image at the cursor, scaled down to fit the width, and moves the cursor below it.</summary>
    private void PlaceImage(int pixelWidth, int pixelHeight, uint[]? pixels, byte[]? encoded, int? widthCells, int? heightCells)
    {
        var (cellWidth, cellHeight) = CellPixelSize;
        var available = Columns - _cursorX;
        var columns = widthCells ?? (int)Math.Ceiling(pixelWidth / cellWidth);
        var rows = heightCells ?? (int)Math.Ceiling(pixelHeight / cellHeight);
        if (columns > available)
        {
            rows = Math.Max(1, (int)Math.Ceiling(rows * (double)available / columns));
            columns = available;
        }

        columns = Math.Max(1, columns);
        rows = Math.Clamp(rows, 1, 1000);

        // Make room first so the image's top line is known after any scrolling.
        var column = _cursorX;
        for (var i = 1; i < rows; i++)
            LineFeed();
        var bottom = ScreenLineNumber(_cursorY);

        _images.Add(new TerminalImage
        {
            Id = ++_nextImageId,
            Line = bottom - (rows - 1),
            Column = column,
            Columns = columns,
            Rows = rows,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            Pixels = pixels,
            Encoded = encoded,
            OnAlternateScreen = _altActive,
        });

        // The text continues on the line below the picture.
        LineFeed();
        _cursorX = 0;
        PruneImages();
    }

    private long ScreenLineNumber(int row) => FirstLineNumber + ScrollbackCount + row;

    /// <summary>Drops images that scrolled out of the scrollback and the oldest ones beyond the limits.</summary>
    private void PruneImages()
    {
        var first = _main.Evicted;
        _images.RemoveAll(i => !i.OnAlternateScreen && i.Line + i.Rows <= first);

        var bytes = _images.Sum(i => i.ByteSize);
        while (_images.Count > MaxImages || (bytes > MaxImageBytes && _images.Count > 1))
        {
            bytes -= _images[0].ByteSize;
            _images.RemoveAt(0);
        }
    }

    /// <summary>Removes images that touch screen rows <paramref name="fromRow"/>…<paramref name="toRow"/> of the current screen.</summary>
    private void ClearImages(int fromRow, int toRow)
    {
        if (_images.Count == 0)
            return;
        var from = ScreenLineNumber(fromRow);
        var to = ScreenLineNumber(toRow);
        _images.RemoveAll(i => i.OnAlternateScreen == _altActive && i.Line <= to && i.Line + i.Rows - 1 >= from);
    }
}
