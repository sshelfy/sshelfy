using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Sshelfy.Core.Terminal;

namespace Sshelfy.App.Controls;

/// <summary>
/// Renders a <see cref="TerminalEmulator"/> on a fixed character grid and turns keyboard,
/// mouse and clipboard interaction into terminal input. All protocol logic lives in the emulator;
/// this control only draws and translates input.
/// </summary>
public sealed class TerminalView : Control
{
    private const double Padding = 8;
    private const int WheelLines = 3;

    public static readonly StyledProperty<TerminalEmulator?> TerminalProperty =
        AvaloniaProperty.Register<TerminalView, TerminalEmulator?>(nameof(Terminal));

    public static readonly StyledProperty<TerminalPalette?> PaletteProperty =
        AvaloniaProperty.Register<TerminalView, TerminalPalette?>(nameof(Palette));

    /// <summary>Rules that color matching text (see Settings).</summary>
    public static readonly StyledProperty<TerminalHighlighter?> HighlighterProperty =
        AvaloniaProperty.Register<TerminalView, TerminalHighlighter?>(nameof(Highlighter));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<TerminalView>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<TerminalView>();

    /// <summary>When false the view keeps the emulator's size instead of fitting it to the control (replays).</summary>
    public static readonly StyledProperty<bool> AutoResizeProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(AutoResize), true);

    /// <summary>Bind to the tab's active state: the view takes focus when it becomes active.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TerminalView, bool>(nameof(IsActive));

    /// <summary>Search hits to highlight, sorted from the oldest line.</summary>
    public static readonly StyledProperty<IReadOnlyList<TerminalMatch>?> SearchMatchesProperty =
        AvaloniaProperty.Register<TerminalView, IReadOnlyList<TerminalMatch>?>(nameof(SearchMatches));

    /// <summary>The selected search hit; the view scrolls to it when it changes.</summary>
    public static readonly StyledProperty<TerminalMatch?> ActiveMatchProperty =
        AvaloniaProperty.Register<TerminalView, TerminalMatch?>(nameof(ActiveMatch));

    /// <summary>Runs from the context menu's "Find" item.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> FindCommandProperty =
        AvaloniaProperty.Register<TerminalView, System.Windows.Input.ICommand?>(nameof(FindCommand));

    private static readonly Color MatchColor = Color.FromArgb(0x70, 0xE8, 0xB3, 0x39);
    private static readonly Color ActiveMatchColor = Color.FromArgb(0xD0, 0xF5, 0x8A, 0x1F);

    private readonly DispatcherTimer _blinkTimer;
    private readonly Dictionary<(int Rune, int Face), (GlyphTypeface Face, ushort Glyph)> _glyphs = new();
    private readonly GlyphTypeface?[] _faces = new GlyphTypeface?[4];
    private readonly Typeface[] _typefaces = new Typeface[4];

    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _baseline = 12;
    private int _invalidatePending;
    private bool _cursorOn = true;

    // Scrollback viewport: number of lines scrolled up from the bottom.
    private int _scrollOffset;
    private long _lastScreenTop;

    // Selection endpoints in stable line numbers; end column is exclusive.
    private (long Line, int Column)? _selectionAnchor;
    private (long Line, int Column)? _selectionEnd;
    private bool _selecting;
    private bool _mouseReporting;

    // Link under the pointer (underlined; Ctrl+click opens it) and where the context menu was opened.
    private TerminalLink? _hoverLink;
    private Point _menuPoint;
    private readonly Cursor _textCursor = new(StandardCursorType.Ibeam);
    private readonly Cursor _linkCursor = new(StandardCursorType.Hand);

    /// <summary>The underlined link, for tests.</summary>
    internal TerminalLink? HoverLink => _hoverLink;

    /// <summary>Middle of a screen cell in control coordinates, for tests.</summary>
    internal Point CellCenter(int column, int row) => new(Padding + (column + 0.5) * _cellWidth, Padding + (row + 0.5) * _cellHeight);

    static TerminalView()
    {
        FocusableProperty.OverrideDefaultValue<TerminalView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<TerminalView>(true);
        AffectsRender<TerminalView>(PaletteProperty, SearchMatchesProperty, ActiveMatchProperty, HighlighterProperty);
        AffectsMeasure<TerminalView>(FontFamilyProperty, FontSizeProperty);
    }

    public TerminalView()
    {
        Cursor = _textCursor;
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };

        ContextMenu = BuildContextMenu();
    }

    public TerminalEmulator? Terminal
    {
        get => GetValue(TerminalProperty);
        set => SetValue(TerminalProperty, value);
    }

    public TerminalPalette? Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
    }

    public TerminalHighlighter? Highlighter
    {
        get => GetValue(HighlighterProperty);
        set => SetValue(HighlighterProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public bool AutoResize
    {
        get => GetValue(AutoResizeProperty);
        set => SetValue(AutoResizeProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public IReadOnlyList<TerminalMatch>? SearchMatches
    {
        get => GetValue(SearchMatchesProperty);
        set => SetValue(SearchMatchesProperty, value);
    }

    public TerminalMatch? ActiveMatch
    {
        get => GetValue(ActiveMatchProperty);
        set => SetValue(ActiveMatchProperty, value);
    }

    public System.Windows.Input.ICommand? FindCommand
    {
        get => GetValue(FindCommandProperty);
        set => SetValue(FindCommandProperty, value);
    }

    public bool HasSelection => _selectionAnchor is { } a && _selectionEnd is { } b && a != b;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TerminalProperty)
        {
            if (change.OldValue is TerminalEmulator old)
                old.Changed -= OnTerminalChanged;
            ClearBitmaps();
            if (change.NewValue is TerminalEmulator terminal)
            {
                terminal.Changed += OnTerminalChanged;
                terminal.CellPixelSize = (_cellWidth, _cellHeight);
                ResizeTerminal(Bounds.Size);
            }

            ClearSelection();
            InvalidateVisual();
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            UpdateMetrics();
            ResizeTerminal(Bounds.Size);
            InvalidateVisual();
        }
        else if (change.Property == ActiveMatchProperty && change.NewValue is TerminalMatch match)
        {
            RevealLine(match.StartLine);
        }
        else if (change.Property == IsActiveProperty && change.NewValue is true)
        {
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateMetrics();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blinkTimer.Stop();
        ClearBitmaps();
    }

    /// <summary>Image ids belong to one emulator, and the native bitmaps should not wait for finalizers.</summary>
    private void ClearBitmaps()
    {
        foreach (var bitmap in _bitmaps.Values)
            bitmap?.Dispose();
        _bitmaps.Clear();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeTerminal(e.NewSize);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        RestartBlink();
        Terminal?.SendFocus(true);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _blinkTimer.Stop();
        _cursorOn = true;
        InvalidateVisual();
        Terminal?.SendFocus(false);
    }

    private void OnTerminalChanged(object? sender, EventArgs e)
    {
        // Output arrives on a background thread; coalesce into one render per UI frame.
        if (Interlocked.Exchange(ref _invalidatePending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _invalidatePending, 0);
                RestartBlink();
                InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    private void RestartBlink()
    {
        _cursorOn = true;
        if (IsFocused)
        {
            _blinkTimer.Stop();
            _blinkTimer.Start();
        }
    }

    // ---------------------------------------------------------------- metrics

    private void UpdateMetrics()
    {
        try
        {
            UpdateMetricsCore();
        }
        finally
        {
            if (Terminal is { } terminal)
                terminal.CellPixelSize = (_cellWidth, _cellHeight);
        }
    }

    private void UpdateMetricsCore()
    {
        var family = FontFamily ?? FontFamily.Default;
        _typefaces[0] = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        _typefaces[1] = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _typefaces[2] = new Typeface(family, FontStyle.Italic, FontWeight.Normal);
        _typefaces[3] = new Typeface(family, FontStyle.Italic, FontWeight.Bold);
        for (var i = 0; i < 4; i++)
            _faces[i] = _typefaces[i].GlyphTypeface;
        _glyphs.Clear();

        var face = _faces[0];
        if (face is null)
            return;

        var metrics = face.Metrics;
        var scale = FontSize / metrics.DesignEmHeight;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        var advance = 0.6 * FontSize;
        if (face.CharacterToGlyphMap.TryGetGlyph('M', out var glyph) && face.TryGetHorizontalGlyphAdvance(glyph, out var designAdvance))
            advance = designAdvance * scale;

        var ascent = -metrics.Ascent * scale;
        var lineHeight = (-metrics.Ascent + metrics.Descent + metrics.LineGap) * scale;

        // Snap to device pixels so the grid stays crisp.
        _cellWidth = Math.Max(1, Math.Round(advance * scaling) / scaling);
        _cellHeight = Math.Max(1, Math.Ceiling(lineHeight * 1.08 * scaling) / scaling);
        _baseline = Math.Round((ascent + (_cellHeight - lineHeight) / 2) * scaling) / scaling;
    }

    private void ResizeTerminal(Size size)
    {
        if (!AutoResize || Terminal is not { } terminal || size.Width <= 0 || size.Height <= 0)
            return;

        var columns = (int)((size.Width - Padding * 2) / _cellWidth);
        var rows = (int)((size.Height - Padding * 2) / _cellHeight);
        if (columns >= 2 && rows >= 1)
            terminal.Resize(columns, rows);
    }

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext context)
    {
        var palette = Palette ?? FallbackPalette;
        var defaultBackground = palette.IsReverse(Terminal) ? palette.Foreground : palette.Background;
        context.FillRectangle(palette.Brush(defaultBackground), new Rect(Bounds.Size));

        if (Terminal is not { } terminal || _faces[0] is null)
            return;

        lock (terminal.SyncRoot)
        {
            var rows = terminal.Rows;
            var scrollback = terminal.ScrollbackCount;

            // Keep the viewport anchored to the same content while new output scrolls in.
            var screenTop = terminal.FirstLineNumber + scrollback;
            if (_scrollOffset > 0)
                _scrollOffset += (int)(screenTop - _lastScreenTop);
            _lastScreenTop = screenTop;
            _scrollOffset = Math.Clamp(_scrollOffset, 0, scrollback);

            var firstIndex = scrollback - _scrollOffset;
            var selection = NormalizedSelection();
            var reverse = terminal.IsReverseVideo;
            var firstLine = terminal.FirstLineNumber + firstIndex;
            var matches = VisibleMatches(firstLine, firstLine + rows - 1);

            for (var row = 0; row < rows; row++)
            {
                var index = firstIndex + row;
                if (index >= terminal.TotalLines)
                    break;
                var lineNumber = terminal.FirstLineNumber + index;
                DrawLine(context, palette, terminal.GetLine(index), row, lineNumber, selection, matches, reverse);
            }

            DrawImages(context, terminal, firstLine, rows);

            if (_hoverLink is { } link)
                DrawLinkUnderline(context, palette, link, firstLine, rows);

            if (_scrollOffset == 0)
                DrawCursor(context, palette, terminal);
            else
                DrawScrollIndicator(context, palette, scrollback, rows);
        }
    }

    private void DrawLine(
        DrawingContext context,
        TerminalPalette palette,
        TerminalLine line,
        int row,
        long lineNumber,
        ((long Line, int Column) Start, (long Line, int Column) End)? selection,
        List<TerminalMatch> matches,
        bool reverse)
    {
        var y = Padding + row * _cellHeight;
        var cells = line.Cells;
        var columns = Math.Min(cells.Length, Terminal!.Columns);
        var highlights = Highlighter is { IsEmpty: false } highlighter ? highlighter.Find(RowText(cells, columns)) : [];

        // Pass 1: backgrounds, merged into runs of equal color.
        var runStart = 0;
        Color? runColor = null;
        for (var x = 0; x <= columns; x++)
        {
            Color? color = null;
            if (x < columns)
            {
                ResolveColors(palette, cells[x].Style, reverse, out _, out var bg, out var isDefaultBg);
                if (IsSelected(selection, lineNumber, x))
                    color = Blend(bg, palette.Selection);
                else if (MatchAt(matches, lineNumber, x) is { } isActive)
                    color = Blend(bg, isActive ? ActiveMatchColor : MatchColor);
                else if (HighlightAt(highlights, x) is { Background: true } span)
                    color = Blend(bg, WithAlpha(HighlightColorOf(palette, span), 0x60));
                else if (!isDefaultBg)
                    color = bg;
            }

            if (x == columns || color != runColor)
            {
                if (runColor is { } fill)
                    context.FillRectangle(palette.Brush(fill), new Rect(Padding + runStart * _cellWidth, y, (x - runStart) * _cellWidth, _cellHeight));
                runStart = x;
                runColor = color;
            }
        }

        // Pass 2: glyphs, merged into runs that share brush and font face.
        var glyphs = new List<GlyphInfo>(columns);
        var chars = new List<char>(columns);
        GlyphTypeface? runFace = null;
        Color runForeground = default;
        var runX = 0;

        void Flush()
        {
            if (glyphs.Count > 0 && runFace is not null)
            {
                var run = new GlyphRun(runFace, FontSize, chars.ToArray(), glyphs.ToArray(), new Point(Padding + runX * _cellWidth, y + _baseline));
                context.DrawGlyphRun(palette.Brush(runForeground), run);
            }

            glyphs.Clear();
            chars.Clear();
        }

        for (var x = 0; x < columns; x++)
        {
            ref readonly var cell = ref cells[x];
            var flags = cell.Style.Flags;
            if ((flags & CellFlags.WideContinuation) != 0 || cell.IsEmpty || (flags & CellFlags.Hidden) != 0)
            {
                Flush();
                runFace = null;
                continue;
            }

            ResolveColors(palette, cell.Style, reverse, out var fg, out _, out _);
            if (HighlightAt(highlights, x) is { Background: false } highlight)
                fg = HighlightColorOf(palette, highlight);
            var faceIndex = ((flags & CellFlags.Bold) != 0 ? 1 : 0) + ((flags & CellFlags.Italic) != 0 ? 2 : 0);
            var (face, glyph) = GlyphFor(cell.Rune, faceIndex);
            var width = (flags & CellFlags.Wide) != 0 ? 2 : 1;

            if (face != runFace || fg != runForeground)
            {
                Flush();
                runFace = face;
                runForeground = fg;
                runX = x;
            }

            // Fixed advances keep every glyph on the grid regardless of the font's own metrics.
            glyphs.Add(new GlyphInfo(glyph, chars.Count, width * _cellWidth));
            AppendRune(chars, cell.Rune);

            if ((flags & (CellFlags.Underline | CellFlags.Strikethrough)) != 0)
            {
                var pen = new Pen(palette.Brush(fg), Math.Max(1, FontSize / 14));
                var left = Padding + x * _cellWidth;
                var right = left + width * _cellWidth;
                if ((flags & CellFlags.Underline) != 0)
                    context.DrawLine(pen, new Point(left, y + _baseline + 2), new Point(right, y + _baseline + 2));
                if ((flags & CellFlags.Strikethrough) != 0)
                    context.DrawLine(pen, new Point(left, y + _cellHeight / 2), new Point(right, y + _cellHeight / 2));
            }

            if (width == 2)
            {
                // Glyph runs advance by glyph; skip the continuation cell explicitly.
                Flush();
                runFace = null;
                x++;
            }
        }

        Flush();
    }

    /// <summary>One character per column, so regex match positions are columns.</summary>
    private static string RowText(TerminalCell[] cells, int columns)
    {
        var text = new char[columns];
        for (var x = 0; x < columns; x++)
        {
            var rune = cells[x].Rune;
            text[x] = cells[x].IsEmpty || (cells[x].Style.Flags & CellFlags.WideContinuation) != 0 ? ' '
                : rune < 0x10000 ? (char)rune : '?';
        }

        return new string(text);
    }

    private static HighlightSpan? HighlightAt(IReadOnlyList<HighlightSpan> spans, int column)
    {
        foreach (var span in spans)
        {
            if (column >= span.Start && column < span.Start + span.Length)
                return span;
        }

        return null;
    }

    /// <summary>The bright variant of the rule's ANSI color, so highlights follow the color scheme.</summary>
    private static Color HighlightColorOf(TerminalPalette palette, HighlightSpan span) =>
        palette.Resolve(TerminalColor.Indexed((int)span.Color + 8), foreground: true);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    // ---------------------------------------------------------------- images

    private readonly Dictionary<int, Avalonia.Media.Imaging.Bitmap?> _bitmaps = new();

    /// <summary>Inline images over their cells, scaled into the area they were given.</summary>
    private void DrawImages(DrawingContext context, TerminalEmulator terminal, long firstLine, int rows)
    {
        var images = terminal.GetImages();
        if (images.Count == 0 && _bitmaps.Count == 0)
            return;

        foreach (var stale in _bitmaps.Keys.Where(id => images.All(i => i.Id != id)).ToList())
        {
            _bitmaps[stale]?.Dispose();
            _bitmaps.Remove(stale);
        }

        using var clip = context.PushClip(new Rect(Bounds.Size));
        foreach (var image in images)
        {
            if (image.Line + image.Rows <= firstLine || image.Line >= firstLine + rows)
                continue;

            if (!_bitmaps.TryGetValue(image.Id, out var bitmap))
            {
                bitmap = CreateBitmap(image);
                _bitmaps[image.Id] = bitmap;
            }

            if (bitmap is null)
                continue;

            // Fit the picture into its cells, keeping the aspect ratio.
            var areaWidth = image.Columns * _cellWidth;
            var areaHeight = image.Rows * _cellHeight;
            var scale = Math.Min(areaWidth / image.PixelWidth, areaHeight / image.PixelHeight);
            var size = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
            var origin = new Point(Padding + image.Column * _cellWidth, Padding + (image.Line - firstLine) * _cellHeight);
            context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(origin, size));
        }
    }

    private static Avalonia.Media.Imaging.Bitmap? CreateBitmap(TerminalImage image)
    {
        try
        {
            if (image.Encoded is { } file)
                return new Avalonia.Media.Imaging.Bitmap(new MemoryStream(file));

            if (image.Pixels is not { } pixels)
                return null;

            var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                new PixelSize(image.PixelWidth, image.PixelHeight), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
            using var buffer = bitmap.Lock();
            var row = new byte[image.PixelWidth * 4];
            for (var y = 0; y < image.PixelHeight; y++)
            {
                // 0xAARRGGBB as little-endian bytes is B, G, R, A.
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan(y * image.PixelWidth, image.PixelWidth)).CopyTo(row);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
            }

            return bitmap;
        }
        catch (Exception)
        {
            // Not a picture Avalonia can decode: leave the cells empty.
            return null;
        }
    }

    private static void AppendRune(List<char> chars, int rune)
    {
        if (rune < 0x10000)
        {
            chars.Add((char)rune);
        }
        else
        {
            var s = char.ConvertFromUtf32(rune);
            chars.Add(s[0]);
        }
    }

    private (GlyphTypeface Face, ushort Glyph) GlyphFor(int rune, int faceIndex)
    {
        if (_glyphs.TryGetValue((rune, faceIndex), out var cached))
            return cached;

        var face = _faces[faceIndex] ?? _faces[0]!;
        (GlyphTypeface, ushort) result;
        if (face.CharacterToGlyphMap.TryGetGlyph(rune, out var glyph) && glyph != 0)
        {
            result = (face, glyph);
        }
        else if (FontManager.Current.TryMatchCharacter(rune, _typefaces[faceIndex].Style, _typefaces[faceIndex].Weight,
                     FontStretch.Normal, FontFamily, CultureInfo.CurrentUICulture, out var fallback)
                 && fallback.GlyphTypeface is { } fallbackFace
                 && fallbackFace.CharacterToGlyphMap.TryGetGlyph(rune, out var fallbackGlyph))
        {
            result = (fallbackFace, fallbackGlyph);
        }
        else
        {
            result = (face, face.CharacterToGlyphMap.TryGetGlyph('?', out var q) ? q : (ushort)0);
        }

        _glyphs[(rune, faceIndex)] = result;
        return result;
    }

    private static void ResolveColors(TerminalPalette palette, CellStyle style, bool reverseScreen, out Color fg, out Color bg, out bool isDefaultBackground)
    {
        var fgColor = style.Foreground;
        // Bold text in one of the 8 base colors uses the bright variant, as most terminals do.
        if ((style.Flags & CellFlags.Bold) != 0 && fgColor.Kind == ColorKind.Indexed && fgColor.Index < 8)
            fgColor = TerminalColor.Indexed(fgColor.Index + 8);

        fg = palette.Resolve(fgColor, foreground: true);
        bg = palette.Resolve(style.Background, foreground: false);
        isDefaultBackground = style.Background.Kind == ColorKind.Default;

        var inverse = ((style.Flags & CellFlags.Inverse) != 0) ^ reverseScreen;
        if (inverse)
        {
            (fg, bg) = (bg, fg);
            isDefaultBackground = false;
        }

        if ((style.Flags & CellFlags.Dim) != 0)
            fg = Blend(fg, Color.FromArgb(128, bg.R, bg.G, bg.B));
    }

    private static Color Blend(Color baseColor, Color overlay)
    {
        var a = overlay.A / 255.0;
        return Color.FromRgb(
            (byte)(baseColor.R + (overlay.R - baseColor.R) * a),
            (byte)(baseColor.G + (overlay.G - baseColor.G) * a),
            (byte)(baseColor.B + (overlay.B - baseColor.B) * a));
    }

    private void DrawCursor(DrawingContext context, TerminalPalette palette, TerminalEmulator terminal)
    {
        if (!terminal.IsCursorVisible)
            return;

        var x = Padding + terminal.CursorX * _cellWidth;
        var y = Padding + terminal.CursorY * _cellHeight;
        var brush = palette.Brush(palette.Cursor);
        var cell = new Rect(x, y, _cellWidth, _cellHeight);

        if (!IsFocused)
        {
            context.DrawRectangle(null, new Pen(brush, 1), cell.Deflate(0.5));
            return;
        }

        if (!_cursorOn && terminal.IsCursorBlinking)
            return;

        switch (terminal.CursorShape)
        {
            case CursorShape.Bar:
                context.FillRectangle(brush, new Rect(x, y, Math.Max(2, _cellWidth / 6), _cellHeight));
                break;
            case CursorShape.Underline:
                context.FillRectangle(brush, new Rect(x, y + _cellHeight - 2, _cellWidth, 2));
                break;
            default:
                context.FillRectangle(brush, cell);
                var line = terminal.GetScreenLine(terminal.CursorY);
                if (terminal.CursorX < line.Length && !line.Cells[terminal.CursorX].IsEmpty)
                {
                    var rune = line.Cells[terminal.CursorX].Rune;
                    var (face, glyph) = GlyphFor(rune, 0);
                    var chars = new List<char>();
                    AppendRune(chars, rune);
                    var run = new GlyphRun(face, FontSize, chars.ToArray(), [new GlyphInfo(glyph, 0, _cellWidth)], new Point(x, y + _baseline));
                    context.DrawGlyphRun(palette.Brush(palette.CursorText), run);
                }

                break;
        }
    }

    private void DrawScrollIndicator(DrawingContext context, TerminalPalette palette, int scrollback, int rows)
    {
        var total = scrollback + rows;
        var trackHeight = Bounds.Height - 8;
        var thumbHeight = Math.Max(24, trackHeight * rows / total);
        var position = (double)(scrollback - _scrollOffset) / Math.Max(1, scrollback);
        var top = 4 + (trackHeight - thumbHeight) * position;
        var color = Color.FromArgb(110, palette.Foreground.R, palette.Foreground.G, palette.Foreground.B);
        context.DrawRectangle(palette.Brush(color), null, new Rect(Bounds.Width - 6, top, 3, thumbHeight), 1.5, 1.5);
    }

    // ---------------------------------------------------------------- links

    private void DrawLinkUnderline(DrawingContext context, TerminalPalette palette, TerminalLink link, long firstLine, int rows)
    {
        var pen = new Pen(palette.Brush(palette.Foreground), 1);
        for (var line = Math.Max(link.StartLine, firstLine); line <= Math.Min(link.EndLine, firstLine + rows - 1); line++)
        {
            var from = line == link.StartLine ? link.StartColumn : 0;
            var to = line == link.EndLine ? link.EndColumn : Terminal!.Columns;
            var y = Padding + (line - firstLine) * _cellHeight + _baseline + 2.5;
            context.DrawLine(pen, new Point(Padding + from * _cellWidth, y), new Point(Padding + to * _cellWidth, y));
        }
    }

    private TerminalLink? LinkAt(Point point)
    {
        if (Terminal is not { } terminal)
            return null;
        lock (terminal.SyncRoot)
        {
            var (line, column, _) = HitTest(point, terminal, roundToNearest: false);
            return TerminalLinks.FindAt(terminal, line, column);
        }
    }

    private void UpdateHoverLink(Point point, KeyModifiers modifiers)
    {
        var link = LinkAt(point);
        if (link != _hoverLink)
        {
            _hoverLink = link;
            ToolTip.SetTip(this, link is null ? null : $"{link.Uri}\nCtrl+click to open");
            InvalidateVisual();
        }

        Cursor = link is not null && (modifiers & KeyModifiers.Control) != 0 ? _linkCursor : _textCursor;
    }

    private void OpenLink(TerminalLink link)
    {
        if (link.IsSafeToOpen && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(new Uri(link.Uri));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverLink is not null)
        {
            _hoverLink = null;
            ToolTip.SetTip(this, null);
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------- search

    /// <summary>Matches that touch lines [first, last]; the list is sorted, so a binary search finds the start.</summary>
    private List<TerminalMatch> VisibleMatches(long first, long last)
    {
        var result = new List<TerminalMatch>();
        if (SearchMatches is not { Count: > 0 } all)
            return result;

        int lo = 0, hi = all.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (all[mid].EndLine < first)
                lo = mid + 1;
            else
                hi = mid;
        }

        for (var i = lo; i < all.Count && all[i].StartLine <= last; i++)
            result.Add(all[i]);
        return result;
    }

    /// <summary>Null when no match covers the cell, otherwise whether it is the active one.</summary>
    private bool? MatchAt(List<TerminalMatch> matches, long line, int column)
    {
        foreach (var match in matches)
        {
            if (match.Contains(line, column))
                return match == ActiveMatch;
        }

        return null;
    }

    /// <summary>Scrolls so that the line is on screen, centering it when it was off screen.</summary>
    private void RevealLine(long lineNumber)
    {
        if (Terminal is not { } terminal)
            return;

        lock (terminal.SyncRoot)
        {
            var scrollback = terminal.ScrollbackCount;
            var rows = terminal.Rows;
            var index = (int)(lineNumber - terminal.FirstLineNumber);
            var firstIndex = scrollback - _scrollOffset;
            if (index >= firstIndex && index < firstIndex + rows)
                return;

            _scrollOffset = Math.Clamp(scrollback - (index - rows / 2), 0, scrollback);
            _lastScreenTop = terminal.FirstLineNumber + scrollback;
        }

        InvalidateVisual();
    }

    // ---------------------------------------------------------------- selection

    private ((long Line, int Column) Start, (long Line, int Column) End)? NormalizedSelection()
    {
        if (_selectionAnchor is not { } a || _selectionEnd is not { } b || a == b)
            return null;
        return a.Line < b.Line || (a.Line == b.Line && a.Column <= b.Column) ? (a, b) : (b, a);
    }

    private static bool IsSelected(((long Line, int Column) Start, (long Line, int Column) End)? selection, long line, int column)
    {
        if (selection is not var (start, end))
            return false;
        if (line < start.Line || line > end.Line)
            return false;
        if (line == start.Line && column < start.Column)
            return false;
        return line != end.Line || column < end.Column;
    }

    private void ClearSelection()
    {
        _selectionAnchor = _selectionEnd = null;
        _selecting = false;
    }

    /// <summary>Maps a pointer position to a stable line number and column.</summary>
    private (long Line, int Column, int ScreenRow) HitTest(Point point, TerminalEmulator terminal, bool roundToNearest)
    {
        var column = (point.X - Padding) / _cellWidth;
        var col = roundToNearest ? (int)Math.Round(column) : (int)Math.Floor(column);
        var row = (int)Math.Floor((point.Y - Padding) / _cellHeight);
        col = Math.Clamp(col, 0, terminal.Columns);
        row = Math.Clamp(row, 0, terminal.Rows - 1);
        var index = terminal.ScrollbackCount - _scrollOffset + row;
        return (terminal.FirstLineNumber + index, col, row);
    }

    public async Task CopySelectionAsync()
    {
        if (Terminal is not { } terminal || NormalizedSelection() is not var (start, end))
            return;

        string text;
        lock (terminal.SyncRoot)
        {
            text = terminal.GetText(start.Line, start.Column, end.Line, end.Column);
        }

        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public async Task PasteAsync()
    {
        if (Terminal is not { } terminal || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            ScrollToBottom();
            terminal.Paste(text);
        }
    }

    public void SelectAll()
    {
        if (Terminal is not { } terminal)
            return;

        lock (terminal.SyncRoot)
        {
            _selectionAnchor = (terminal.FirstLineNumber, 0);
            _selectionEnd = (terminal.FirstLineNumber + terminal.TotalLines - 1, terminal.Columns);
        }

        InvalidateVisual();
    }

    public void ClearScrollback()
    {
        Terminal?.Feed("\x1b[3J");
        _scrollOffset = 0;
        ClearSelection();
    }

    private ContextMenu BuildContextMenu()
    {
        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
        copy.Click += async (_, _) => await CopySelectionAsync();
        var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) };
        paste.Click += async (_, _) => await PasteAsync();
        var find = new MenuItem { Header = "Find…", InputGesture = new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift) };
        find.Click += (_, _) => FindCommand?.Execute(null);
        var openLink = new MenuItem { Header = "Open link" };
        var copyLink = new MenuItem { Header = "Copy link" };
        var linkSeparator = new Separator();
        TerminalLink? menuLink = null;
        openLink.Click += (_, _) =>
        {
            if (menuLink is not null)
                OpenLink(menuLink);
        };
        copyLink.Click += async (_, _) =>
        {
            if (menuLink is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(menuLink.Uri);
        };
        var selectAll = new MenuItem { Header = "Select all" };
        selectAll.Click += (_, _) => SelectAll();
        var clear = new MenuItem { Header = "Clear scrollback" };
        clear.Click += (_, _) => ClearScrollback();

        var menu = new ContextMenu { ItemsSource = new object[] { openLink, copyLink, linkSeparator, copy, paste, new Separator(), find, selectAll, clear } };
        menu.Opening += (_, _) =>
        {
            copy.IsEnabled = HasSelection;
            find.IsVisible = FindCommand is not null;
            menuLink = LinkAt(_menuPoint);
            openLink.IsVisible = copyLink.IsVisible = linkSeparator.IsVisible = menuLink is not null;
            openLink.IsEnabled = menuLink?.IsSafeToOpen == true;
        };
        return menu;
    }

    private void ScrollToBottom()
    {
        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------- input

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Terminal is not { } terminal || e.Handled)
            return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

        // Clipboard shortcuts that do not collide with shell control keys.
        if (ctrl && shift && !alt)
        {
            switch (e.Key)
            {
                case Key.C:
                    _ = CopySelectionAsync();
                    e.Handled = true;
                    return;
                case Key.V:
                    _ = PasteAsync();
                    e.Handled = true;
                    return;
            }

            if (e.Key is >= Key.A and <= Key.Z || e.Key == Key.Tab)
                return; // Leave Ctrl+Shift+letter to application shortcuts.
        }

        if (shift && !ctrl && e.Key == Key.Insert)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (shift && !ctrl && e.Key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(e.Key == Key.PageUp ? terminal.Rows - 1 : -(terminal.Rows - 1));
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Tab)
            return; // Tab switching belongs to the window.

        var modifiers = (shift ? TerminalModifiers.Shift : 0) | (alt ? TerminalModifiers.Alt : 0) | (ctrl ? TerminalModifiers.Control : 0);
        if (MapKey(e.Key) is { } key)
        {
            terminal.SendKey(key, modifiers);
            AfterInput();
            e.Handled = true;
            return;
        }

        // Ctrl+Alt is AltGr on many layouts (e.g. Polish ą, ę): that is text, not a control key.
        if (ctrl && !alt)
        {
            var symbol = e.Key is >= Key.A and <= Key.Z ? (char)('a' + (e.Key - Key.A)) : e.KeySymbol is { Length: 1 } s ? s[0] : '\0';
            if (symbol != '\0' && terminal.SendControl(symbol))
            {
                AfterInput();
                e.Handled = true;
            }

            return;
        }

        if (alt && !ctrl && e.KeySymbol is { Length: > 0 } text && !char.IsControl(text[0]))
        {
            terminal.SendAlt(text);
            AfterInput();
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Terminal is not { } terminal || string.IsNullOrEmpty(e.Text) || e.Handled)
            return;

        // Control characters arrive through OnKeyDown; ignore their WM_CHAR echoes.
        if (e.Text.All(char.IsControl))
            return;

        terminal.SendText(e.Text);
        AfterInput();
        e.Handled = true;
    }

    private void AfterInput()
    {
        ScrollToBottom();
        if (HasSelection)
        {
            ClearSelection();
            InvalidateVisual();
        }

        RestartBlink();
    }

    private static TerminalKey? MapKey(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Tab => TerminalKey.Tab,
        Key.Back => TerminalKey.Backspace,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Terminal is not { } terminal)
            return;

        var point = e.GetCurrentPoint(this);
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        _menuPoint = point.Position;

        // Ctrl+click opens a link, even in apps that capture the mouse.
        if (point.Properties.IsLeftButtonPressed && (e.KeyModifiers & KeyModifiers.Control) != 0 && LinkAt(point.Position) is { } link)
        {
            OpenLink(link);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed && terminal.MouseTracking == MouseTracking.None)
            return; // context menu

        // Apps with mouse support get the click; Shift forces local selection.
        if (terminal.MouseTracking != MouseTracking.None && !shift && _scrollOffset == 0)
        {
            var (_, col, row) = HitTest(point.Position, terminal, roundToNearest: false);
            var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 : 2;
            _mouseReporting = terminal.SendMouse(TerminalMouseAction.Press, button, col, row, ToModifiers(e.KeyModifiers));
            e.Handled = _mouseReporting;
            if (_mouseReporting)
                return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        lock (terminal.SyncRoot)
        {
            var (line, column, _) = HitTest(point.Position, terminal, roundToNearest: true);
            switch (e.ClickCount)
            {
                case 2:
                    var (_, rawCol, _) = HitTest(point.Position, terminal, roundToNearest: false);
                    var (start, end) = terminal.GetWordBounds(line, rawCol);
                    _selectionAnchor = (line, start);
                    _selectionEnd = (line, end);
                    break;
                case >= 3:
                    _selectionAnchor = (line, 0);
                    _selectionEnd = (line, terminal.Columns);
                    break;
                default:
                    if (shift && _selectionAnchor is not null)
                        _selectionEnd = (line, column);
                    else
                        _selectionAnchor = _selectionEnd = (line, column);
                    break;
            }
        }

        _selecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Terminal is not { } terminal)
            return;

        var point = e.GetCurrentPoint(this);
        if (_mouseReporting || (terminal.MouseTracking == MouseTracking.AnyEvent && !_selecting))
        {
            var (_, col, row) = HitTest(point.Position, terminal, roundToNearest: false);
            var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 : point.Properties.IsRightButtonPressed ? 2 : -1;
            terminal.SendMouse(TerminalMouseAction.Move, button, col, row, ToModifiers(e.KeyModifiers));
            return;
        }

        if (!_selecting)
        {
            UpdateHoverLink(point.Position, e.KeyModifiers);
            return;
        }

        // Dragging above or below the view scrolls through the scrollback.
        if (point.Position.Y < 0)
            ScrollBy(1);
        else if (point.Position.Y > Bounds.Height)
            ScrollBy(-1);

        lock (terminal.SyncRoot)
        {
            var (line, column, _) = HitTest(point.Position, terminal, roundToNearest: true);
            _selectionEnd = (line, column);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Terminal is not { } terminal)
            return;

        if (_mouseReporting)
        {
            var point = e.GetCurrentPoint(this);
            var (_, col, row) = HitTest(point.Position, terminal, roundToNearest: false);
            var button = e.InitialPressMouseButton switch
            {
                MouseButton.Middle => 1,
                MouseButton.Right => 2,
                _ => 0,
            };
            terminal.SendMouse(TerminalMouseAction.Release, button, col, row, ToModifiers(e.KeyModifiers));
            _mouseReporting = false;
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Terminal is not { } terminal || e.Delta.Y == 0)
            return;

        var up = e.Delta.Y > 0;
        var steps = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y)));

        if (terminal.MouseTracking != MouseTracking.None && _scrollOffset == 0)
        {
            var (_, col, row) = HitTest(e.GetPosition(this), terminal, roundToNearest: false);
            for (var i = 0; i < steps; i++)
                terminal.SendMouse(up ? TerminalMouseAction.WheelUp : TerminalMouseAction.WheelDown, 0, col, row, ToModifiers(e.KeyModifiers));
        }
        else if (terminal.IsAlternateScreen)
        {
            // Full-screen apps without mouse support (less, man) scroll with arrow keys.
            for (var i = 0; i < steps * WheelLines; i++)
                terminal.SendKey(up ? TerminalKey.Up : TerminalKey.Down);
        }
        else
        {
            ScrollBy((up ? 1 : -1) * steps * WheelLines);
        }

        e.Handled = true;
    }

    private void ScrollBy(int lines)
    {
        if (Terminal is not { } terminal)
            return;

        int scrollback;
        lock (terminal.SyncRoot)
        {
            scrollback = terminal.ScrollbackCount;
        }

        var offset = Math.Clamp(_scrollOffset + lines, 0, scrollback);
        if (offset != _scrollOffset)
        {
            _scrollOffset = offset;
            InvalidateVisual();
        }
    }

    private static TerminalModifiers ToModifiers(KeyModifiers modifiers) =>
        ((modifiers & KeyModifiers.Shift) != 0 ? TerminalModifiers.Shift : 0) |
        ((modifiers & KeyModifiers.Alt) != 0 ? TerminalModifiers.Alt : 0) |
        ((modifiers & KeyModifiers.Control) != 0 ? TerminalModifiers.Control : 0);

    private static readonly TerminalPalette FallbackPalette = new()
    {
        Ansi = "#1E1E1E,#CD3131,#0DBC79,#E5E510,#2472C8,#BC3FBC,#11A8CD,#E5E5E5,#666666,#F14C4C,#23D18B,#F5F543,#3B8EEA,#D670D6,#29B8DB,#FFFFFF",
    };
}

internal static class TerminalPaletteExtensions
{
    public static bool IsReverse(this TerminalPalette palette, TerminalEmulator? terminal) => terminal?.IsReverseVideo == true;
}
