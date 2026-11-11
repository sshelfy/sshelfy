using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sshelfy.Core.Terminal;

public sealed class TerminalThemeImportException(string message) : Exception(message);

/// <summary>Reads color schemes from Windows Terminal (JSON), iTerm2 (.itermcolors) and base16 (YAML) files.</summary>
public static partial class TerminalThemeImporter
{
    private static readonly string[] WindowsTerminalAnsi =
    [
        "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite",
    ];

    /// <summary>base16 slots for ANSI 0–15, as base16-shell maps them.</summary>
    private static readonly string[] Base16Ansi =
    [
        "base00", "base08", "base0B", "base0A", "base0D", "base0E", "base0C", "base05",
        "base03", "base08", "base0B", "base0A", "base0D", "base0E", "base0C", "base07",
    ];

    /// <param name="fileName">Used for the format hint and as the name when the file has none.</param>
    /// <exception cref="TerminalThemeImportException">The file is not a color scheme this importer knows.</exception>
    public static IReadOnlyList<TerminalTheme> Import(string content, string fileName)
    {
        // Either separator: the name may come from another OS than the one running (e.g. a Windows path in a test).
        fileName = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        var fallbackName = Path.GetFileNameWithoutExtension(fileName);
        var text = content.TrimStart('﻿').TrimStart();

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var themes = extension is ".itermcolors" or ".plist" or ".xml" || text.StartsWith('<') ? FromITerm(text, fallbackName)
            : extension is ".json" or ".jsonc" || text.StartsWith('{') || text.StartsWith('[') || text.StartsWith("//") ? FromWindowsTerminal(text, fallbackName)
            : FromBase16(text, fallbackName);

        return themes.Count > 0 ? themes
            : throw new TerminalThemeImportException($"No color scheme found in {Path.GetFileName(fileName)}. Use a Windows Terminal scheme (JSON), an .itermcolors file or a base16 YAML file.");
    }

    // ---------------------------------------------------------------- Windows Terminal

    private static List<TerminalTheme> FromWindowsTerminal(string text, string fallbackName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new TerminalThemeImportException($"The file is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            IEnumerable<JsonElement> schemes =
                root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
                : root.TryGetProperty("schemes", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() // a whole settings.json
                : [root];

            var themes = new List<TerminalTheme>();
            foreach (var scheme in schemes.Where(s => s.ValueKind == JsonValueKind.Object))
            {
                string? Get(string key) => scheme.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Hex(v.GetString()) : null;

                var ansi = WindowsTerminalAnsi.Select(Get).ToList();
                if (Get("background") is not { } background || Get("foreground") is not { } foreground || ansi.Any(c => c is null))
                    continue;

                var name = scheme.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s ? s : fallbackName;
                themes.Add(new TerminalTheme(name, background, foreground, Get("cursorColor") ?? foreground, ansi!, null, Get("selectionBackground")));
            }

            return themes;
        }
    }

    // ---------------------------------------------------------------- iTerm2

    private static List<TerminalTheme> FromITerm(string text, string fallbackName)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new TerminalThemeImportException($"The file is not a valid property list: {ex.Message}");
        }

        if (document.Root?.Element("dict") is not { } dict)
            return [];

        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in Pairs(dict))
        {
            if (value.Name == "dict" && ITermColor(value) is { } color)
                colors[key] = color;
        }

        var ansi = Enumerable.Range(0, 16).Select(i => colors.GetValueOrDefault($"Ansi {i} Color")).ToList();
        if (!colors.TryGetValue("Background Color", out var background) || !colors.TryGetValue("Foreground Color", out var foreground) || ansi.Any(c => c is null))
            return [];

        return [new TerminalTheme(fallbackName, background, foreground, colors.GetValueOrDefault("Cursor Color") ?? foreground, ansi!,
            colors.GetValueOrDefault("Cursor Text Color"), colors.GetValueOrDefault("Selection Color"))];
    }

    /// <summary>Key/value pairs of a plist &lt;dict&gt; (alternating &lt;key&gt; and value elements).</summary>
    private static IEnumerable<(string Key, XElement Value)> Pairs(XElement dict)
    {
        string? key = null;
        foreach (var element in dict.Elements())
        {
            if (element.Name == "key")
                key = element.Value;
            else if (key is not null)
            {
                yield return (key, element);
                key = null;
            }
        }
    }

    private static string? ITermColor(XElement dict)
    {
        double? r = null, g = null, b = null;
        foreach (var (key, value) in Pairs(dict))
        {
            if (!double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var component))
                continue;
            switch (key)
            {
                case "Red Component": r = component; break;
                case "Green Component": g = component; break;
                case "Blue Component": b = component; break;
            }
        }

        return r is null || g is null || b is null ? null
            : $"#{Channel(r.Value):X2}{Channel(g.Value):X2}{Channel(b.Value):X2}";

        static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    }

    // ---------------------------------------------------------------- base16

    private static List<TerminalTheme> FromBase16(string text, string fallbackName)
    {
        var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Base16Slot().Matches(text))
            slots.TryAdd(match.Groups[1].Value, "#" + match.Groups[2].Value.ToUpperInvariant());

        if (Base16Ansi.Any(slot => !slots.ContainsKey(slot)))
            return [];

        var name = Base16Name().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : fallbackName;
        return [new TerminalTheme(name, slots["base00"], slots["base05"], slots["base05"], Base16Ansi.Select(s => slots[s]).ToList(), null, slots.GetValueOrDefault("base02"))];
    }

    [GeneratedRegex(@"^\s*(base0[0-9A-Fa-f])\s*:\s*[""']?#?([0-9A-Fa-f]{6})[""']?", RegexOptions.Multiline)]
    private static partial Regex Base16Slot();

    [GeneratedRegex(@"^(?:scheme|name)\s*:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.Multiline)]
    private static partial Regex Base16Name();

    /// <summary>"#rgb", "#rrggbb" or "#rrggbbaa" to "#RRGGBB"; null when it is not a color.</summary>
    private static string? Hex(string? value)
    {
        if (value is null)
            return null;
        var s = value.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length == 8)
            s = s[..6];
        return s.Length == 6 && s.All(Uri.IsHexDigit) ? "#" + s.ToUpperInvariant() : null;
    }
}
