using System.Security.Cryptography;
using System.Text;
using VibeRails.Launches;
using VibeRails.Services.VCA;

namespace VibeRails.Services.Launches;

/// <summary>Rule identity is its exact parsed text and declaring Git path, never its display label or slug.</summary>
internal static class RuleLaunchIdentity
{
    internal static string Id(string sourcePath, string ruleText) =>
        Hash(OperatingSystem.IsWindows() ? sourcePath.ToUpperInvariant() : sourcePath, ruleText);

    internal static string Hash(params string[] parts)
    {
        // Length prefixes avoid ambiguity even when a rule or Git path contains separators.
        var value = string.Concat(parts.Select(part => $"{Encoding.UTF8.GetByteCount(part)}:{part}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static async Task<RuleLaunchTrigger> ValidateAsync(string project, RuleLaunchTrigger? rule,
        bool enabled, CancellationToken ct)
    {
        if (rule is null || string.IsNullOrWhiteSpace(rule.SourcePath) || string.IsNullOrWhiteSpace(rule.RuleText)
            || rule.RuleText.Length > AgentRuleSectionReader.MaxLineLength || rule.RuleText.Any(char.IsControl))
            throw new LaunchValidationException("Select a rule and its declaring vc.rules.md file.");
        try
        {
            var path = Path.GetFullPath(rule.SourcePath, Path.GetFullPath(project));
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                || !Path.GetFileName(path).Equals("vc.rules.md", StringComparison.OrdinalIgnoreCase))
                throw new LaunchValidationException("Choose a vc.rules.md file within this repository.");
            // Disabling an association is always possible after its source is moved, removed or linked.
            // Enabling is the filesystem trust boundary and uses the shared containment/link guard.
            if (enabled) path = RuleFilePath.Resolve(project, path);
            if (enabled && (!File.Exists(path) || !AgentRuleSectionReader.Read(await File.ReadAllTextAsync(path, ct))
                    .Any(candidate => candidate.RuleText == rule.RuleText)))
                throw new LaunchValidationException("That rule is no longer declared in the selected file. Refresh the rule list.");
            return new(relative, rule.RuleText);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new LaunchValidationException("Choose a readable vc.rules.md file within this repository.");
        }
    }
}
