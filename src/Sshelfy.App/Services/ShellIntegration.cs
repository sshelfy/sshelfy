using System.ComponentModel;
using System.Diagnostics;

namespace Sshelfy.App.Services;

/// <summary>Opens folders and files in the operating system's file manager.</summary>
public interface IShellIntegration
{
    void OpenFolder(string path);

    /// <summary>Opens the file manager with <paramref name="path"/> selected.</summary>
    void Reveal(string path);

    /// <summary>Opens a file with its default application.</summary>
    void OpenFile(string path);

    /// <summary>Opens a file in <paramref name="editorCommand"/> ("code --wait", "notepad++.exe -multiInst {file}"), or the default application when empty.</summary>
    /// <returns>Null on success, otherwise why the editor did not start.</returns>
    string? OpenInEditor(string path, string? editorCommand) { OpenFile(path); return null; }

    /// <summary>Opens a web page in the default browser (http and https only).</summary>
    void OpenUrl(string url)
    {
    }
}

internal sealed class ShellIntegration : IShellIntegration
{
    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Start(OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open", path);
    }

    public void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
            Start("explorer.exe", $"/select,\"{path}\"");
        else if (OperatingSystem.IsMacOS())
            Start("open", "-R", path);
        else
            Start("xdg-open", Path.GetDirectoryName(path) ?? path);
    }

    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No application is associated with the file type.
            Reveal(path);
        }
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No browser registered; nothing to do.
        }
    }

    public string? OpenInEditor(string path, string? editorCommand)
    {
        if (string.IsNullOrWhiteSpace(editorCommand))
        {
            // The "default app" of a script or program is to run it: a server file must only be opened as text.
            if (OperatingSystem.IsWindows() && EditorCommand.RunsWhenOpened(path))
            {
                Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), path);
                return null;
            }

            OpenFile(path);
            return null;
        }

        var (program, arguments) = EditorCommand.Split(editorCommand, path);
        try
        {
            // Shell execute finds editors on PATH and in App Paths (code, notepad++).
            Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"Could not start {program}: {ex.Message}";
        }
    }

    private static void Start(string file, params string[] arguments)
    {
        try
        {
            if (arguments.Length == 1 && file == "explorer.exe")
                Process.Start(file, arguments[0]);
            else
                Process.Start(file, arguments);
        }
        catch (Win32Exception)
        {
            // No file manager available; nothing to do.
        }
    }
}

/// <summary>Turns an editor setting into a program and its arguments with the file filled in.</summary>
internal static class EditorCommand
{
    /// <summary>Types Windows runs, installs or follows when opened, rather than showing them.</summary>
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".cpl", ".msi", ".msp", ".msc", ".hta", ".lnk", ".url",
        ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1", ".psd1", ".reg", ".jar", ".chm", ".scf",
        ".application", ".appref-ms", ".settingcontent-ms", ".library-ms", ".search-ms", ".appx", ".msix",
    };

    public static bool RunsWhenOpened(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
            return false;
        if (Executable.Contains(extension))
            return true;
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty;
        return pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"{file}" marks where the path goes; without it the path is appended.</summary>
    public static (string Program, string Arguments) Split(string command, string path)
    {
        command = command.Trim();
        string program, rest;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            program = end < 0 ? command.Trim('"') : command[1..end];
            rest = end < 0 ? string.Empty : command[(end + 1)..].Trim();
        }
        else
        {
            var space = command.IndexOf(' ');
            program = space < 0 ? command : command[..space];
            rest = space < 0 ? string.Empty : command[(space + 1)..].Trim();
        }

        var quoted = $"\"{path}\"";
        var arguments = rest.Contains("{file}", StringComparison.Ordinal)
            ? rest.Replace("\"{file}\"", quoted, StringComparison.Ordinal).Replace("{file}", quoted, StringComparison.Ordinal)
            : (rest.Length > 0 ? rest + " " : string.Empty) + quoted;
        return (program, arguments);
    }
}
