using System.Globalization;
using System.Text.RegularExpressions;
using VibeRails.Services;

namespace VibeRails.Services.Board;

/// <summary>
/// The LLM-picker key an assignee is stored as: <c>base:{cli}</c> for a bare CLI or
/// <c>env:{id}:{cli}</c> for a saved environment — the same string the picker's
/// <c>LlmPickerPreferenceItem.Key</c> uses, so a card assignee is directly launchable.
/// </summary>
public sealed partial record BoardSelection(string Key, LLM Llm, string Cli, int? EnvironmentId)
{
    public bool IsEnvironment => EnvironmentId is not null;

    public static bool TryParse(string? value, out BoardSelection? selection)
    {
        selection = null;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var match = KeyPattern().Match(trimmed);
        if (!match.Success)
            return false;

        var cliText = match.Groups["cli"].Value;
        var llm = LlmParser.ParseValue(cliText);
        if (llm is LLM.NotSet or LLM.Shell)
            return false;
        // Picker keys carry the lowercase wire name (buildLlmSelectionValue in utils.js).
        var cli = LlmParser.ToWireName(llm).ToLowerInvariant();

        if (match.Groups["id"].Success)
        {
            var id = int.Parse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            selection = new BoardSelection($"env:{id}:{cli}", llm, cli, id);
            return true;
        }

        selection = new BoardSelection($"base:{cli}", llm, cli, null);
        return true;
    }

    public static string Format(LLM llm, int? environmentId)
    {
        var cli = LlmParser.ToWireName(llm).ToLowerInvariant();
        return environmentId is int id ? $"env:{id}:{cli}" : $"base:{cli}";
    }

    [GeneratedRegex(@"^(?:base:(?<cli>[A-Za-z0-9.\-]+)|env:(?<id>\d{1,9}):(?<cli>[A-Za-z0-9.\-]+))$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();
}
