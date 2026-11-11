using System.Buffers.Binary;
using Sshelfy.Core.Terminal;

namespace Sshelfy.Core.Tests;

public sealed class TerminalImageTests
{
    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;

    [Fact]
    public void Decodes_sixel_colors_repeats_and_bands()
    {
        // Red 3 columns full height, then a blue band below with one column of the top pixel only.
        var image = SixelDecoder.Decode("0;1;0q\"1;1;3;12#1;2;100;0;0#2;2;0;0;100#1!3~-#2@");

        Assert.NotNull(image);
        var (pixels, width, height) = image.Value;
        Assert.Equal(3, width);
        Assert.Equal(7, height);
        Assert.All(pixels[..(3 * 6)], p => Assert.Equal(Red, p));
        Assert.Equal(Blue, pixels[6 * 3]);
        Assert.Equal(0u, pixels[6 * 3 + 1]); // unset pixels stay transparent
    }

    [Fact]
    public void Sixel_carriage_return_overlays_colors_in_one_band()
    {
        var image = SixelDecoder.Decode("q#1;2;100;0;0#2;2;0;0;100#1~~$#2??~")!.Value;

        Assert.Equal(3, image.Width);
        Assert.Equal(Red, image.Pixels[0]);
        Assert.Equal(Red, image.Pixels[1]);
        Assert.Equal(Blue, image.Pixels[2]);
    }

    [Fact]
    public void Empty_sixel_is_ignored() => Assert.Null(SixelDecoder.Decode("q#0;2;0;0;0"));

    [Fact]
    public void Reads_png_gif_and_jpeg_sizes()
    {
        Assert.Equal((640, 480), ImageHeader.ReadSize(Png(640, 480)));
        Assert.Equal((32, 16), ImageHeader.ReadSize([.. "GIF89a"u8, 32, 0, 16, 0]));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x2C, 0x00, 0xC8, 0x03, 0, 0, 0, 0];
        Assert.Equal((200, 300), ImageHeader.ReadSize(jpeg));
        Assert.Null(ImageHeader.ReadSize("not an image"u8));
    }

    [Fact]
    public void Inline_image_takes_cells_and_moves_the_cursor_below_it()
    {
        var terminal = new TerminalEmulator(80, 24) { CellPixelSize = (10, 20) };
        terminal.Feed("$ imgcat cat.png\r\n");

        terminal.Feed(InlineImage(Png(100, 50)));

        var image = Assert.Single(terminal.GetImages());
        Assert.Equal((1L, 0, 10, 3), (image.Line, image.Column, image.Columns, image.Rows));
        Assert.Equal((0, 4), (terminal.CursorX, terminal.CursorY));
        Assert.NotNull(image.Encoded);
    }

    [Fact]
    public void Inline_image_size_options_keep_the_aspect_ratio_and_fit_the_width()
    {
        var terminal = new TerminalEmulator(40, 24) { CellPixelSize = (10, 20) };

        terminal.Feed(InlineImage(Png(100, 100), "width=10"));
        terminal.Feed(InlineImage(Png(1000, 100)));

        var images = terminal.GetImages();
        Assert.Equal((10, 5), (images[0].Columns, images[0].Rows));
        Assert.Equal((40, 2), (images[1].Columns, images[1].Rows)); // 100×5 cells scaled to 40×2
    }

    [Fact]
    public void Downloads_and_garbage_are_not_shown()
    {
        var terminal = new TerminalEmulator(80, 24);

        terminal.Feed("\x1b]1337;File=name=eC5wbmc=:" + Convert.ToBase64String(Png(10, 10)) + "\a");
        terminal.Feed("\x1b]1337;File=inline=1:!!!not base64\a");
        terminal.Feed("\x1b]1337;File=inline=1:" + Convert.ToBase64String("hello"u8.ToArray()) + "\a");

        Assert.Empty(terminal.GetImages());
        Assert.Equal((0, 0), (terminal.CursorX, terminal.CursorY));
    }

    [Fact]
    public void Images_scroll_away_and_are_cleared_with_the_screen()
    {
        var terminal = new TerminalEmulator(20, 5, scrollbackLimit: 3) { CellPixelSize = (10, 20) };
        terminal.Feed("\x1bPq#1;2;100;0;0#1!20~-!20~-!20~-!20~\x1b\\"); // 20×24 px = 2 columns, 2 rows
        Assert.Single(terminal.GetImages());

        terminal.Feed(string.Concat(Enumerable.Repeat("line\r\n", 6)));
        Assert.Single(terminal.GetImages()); // in the scrollback

        terminal.Feed(string.Concat(Enumerable.Repeat("line\r\n", 6)));
        Assert.Empty(terminal.GetImages()); // evicted with its lines

        terminal.Feed("\x1bPq#1;2;100;0;0#1~\x1b\\");
        terminal.Feed("\x1b[2J");
        Assert.Empty(terminal.GetImages());
    }

    [Fact]
    public void Clearing_the_scrollback_keeps_images_on_screen()
    {
        var terminal = new TerminalEmulator(20, 5, scrollbackLimit: 50) { CellPixelSize = (10, 20) };
        terminal.Feed(string.Concat(Enumerable.Repeat("old\r\n", 10)));
        terminal.Feed("\x1bPq#1;2;100;0;0#1~\x1b\\");

        terminal.Feed("\x1b[3J");

        var image = Assert.Single(terminal.GetImages());
        Assert.InRange(image.Line - terminal.FirstLineNumber - terminal.ScrollbackCount, 0, terminal.Rows - 1);
    }

    [Fact]
    public void Full_screen_apps_get_their_own_images()
    {
        var terminal = new TerminalEmulator(80, 24);
        terminal.Feed("\x1bPq#1;2;100;0;0#1~\x1b\\");

        terminal.Feed("\x1b[?1049h\x1bPq#1;2;100;0;0#1~\x1b\\");
        Assert.Single(terminal.GetImages());
        Assert.True(terminal.GetImages()[0].OnAlternateScreen);

        terminal.Feed("\x1b[?1049l");
        Assert.False(Assert.Single(terminal.GetImages()).OnAlternateScreen);
    }

    private static string InlineImage(byte[] file, string options = "") =>
        $"\x1b]1337;File=inline=1;size={file.Length}{(options.Length > 0 ? ";" + options : "")}:{Convert.ToBase64String(file)}\a";

    /// <summary>A PNG signature and IHDR chunk: enough for the header reader.</summary>
    private static byte[] Png(int width, int height)
    {
        var file = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        signature.CopyTo(file, 0);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(20), height);
        return file;
    }
}
