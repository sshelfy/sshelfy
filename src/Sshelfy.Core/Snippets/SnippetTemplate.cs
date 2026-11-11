using System.Text.RegularExpressions;

namespace Sshelfy.Core.Snippets;

/// <summary>A <c>{{name}}</c> or <c>{{name:default}}</c> placeholder in a snippet.</summary>
public sealed record SnippetVariable(string Name, string? DefaultValue);

/// <summary>
/// Placeholders in snippet commands. Names start with a letter or underscore, so Go templates such as
/// <c>docker ps --format "{{.Names}}"</c> are left alone.
/// </summary>
public static partial class SnippetTemplate
{
    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_\-]*)\s*(?::(?<default>[^{}]*))?\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Distinct variables in order of first appearance; the first default given for a name wins.</summary>
    public static IReadOnlyList<SnippetVariable> Variables(string command)
    {
        var result = new List<SnippetVariable>();
        foreach (Match match in Placeholder().Matches(command))
        {
            var name = match.Groups["name"].Value;
            if (result.Exists(v => v.Name == name))
                continue;
            var fallback = match.Groups["default"];
            result.Add(new SnippetVariable(name, fallback.Success ? fallback.Value.Trim() : null));
        }

        return result;
    }

    public static bool HasVariables(string command) => Placeholder().IsMatch(command);

    /// <summary>Replaces every placeholder; names without a value fall back to their default, then to empty.</summary>
    public static string Fill(string command, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(command, match =>
        {
            var name = match.Groups["name"].Value;
            if (values.TryGetValue(name, out var value))
                return value;
            return match.Groups["default"] is { Success: true } fallback ? fallback.Value.Trim() : string.Empty;
        });
}
