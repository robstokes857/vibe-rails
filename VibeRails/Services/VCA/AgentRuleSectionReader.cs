using System.Text.RegularExpressions;

namespace VibeRails.Services.VCA;

/// <summary>One rule line as written in an AGENTS.md rules section.</summary>
/// <param name="RuleText">The rule as the user wrote it, with any enforcement marker removed.</param>
/// <param name="Enforcement">Upper-case enforcement token; <c>WARN</c> when the line declared none.</param>
public sealed record AgentRuleLine(string RuleText, string Enforcement);

/// <summary>
/// The one answer to "which rules does this AGENTS.md declare".
///
/// There used to be two answers. The Rules page read a heading-scoped section and showed what it
/// found; the Git hook regex-scanned the entire file and enforced what it found. On a file that
/// documents its own rule syntax, the hook enforced the documentation — three fenced examples of
/// how to write a rule became a live COMMIT rule, a live STOP rule, and a blocked commit that the
/// Rules page could not explain because its reader saw no rules at all. Both callers now come
/// through here, so the page can always account for what the hook does.
///
/// The contract, in full:
///
/// <list type="bullet">
/// <item>Rules live under a rules heading. <see cref="CanonicalHeading"/> is the one to write;
/// <c>## Vibe Control Rules</c> is accepted for files that predate it.</item>
/// <item>A section ends at the next Markdown heading of any level.</item>
/// <item>Fenced code blocks (<c>```</c> or <c>~~~</c>) are skipped everywhere, including inside a
/// rules section. Prose that shows what a rule looks like is documentation, never policy.</item>
/// <item>A rule is a list item in one of three forms: <c>- [STOP] Rule text</c>,
/// <c>- Rule text (STOP)</c>, or a bare <c>- Rule text</c>, which means WARN. The bare form is not
/// a leniency — it is what the Rules page writes when a rule is added without an explicit level,
/// and the hook ignoring it was its own silent disagreement with the UI.</item>
/// <item>Recognizing a rule's <em>text</em> is not this type's job. It reports what the file
/// declares; deciding whether a validator exists for it belongs to the caller, so that both
/// callers can make the same decision from the same list.</item>
/// </list>
/// </summary>
public static partial class AgentRuleSectionReader
{
    /// <summary>The heading new files should use. Written by the Rules page when it creates a file.</summary>
    public const string CanonicalHeading = "## Vibe Rails Rules";

    /// <summary>
    /// Headings that open a rules section, compared case-insensitively with the leading
    /// <c>#</c> characters stripped. Deliberately specific: a bare <c>## Rules</c> is far too
    /// common a heading in ordinary documentation to be treated as enforceable policy.
    /// </summary>
    private static readonly string[] RulesHeadings =
    [
        "vibe rails rules",
        "vibe control rules"
    ];

    // Enforcement vocabulary understood in rule lines. A deliberate superset of
    // Services.EnforcementParser (WARN/COMMIT/STOP): SKIP and DISABLED are opt-outs the hook
    // honors, and the caller decides what to do with them.
    [GeneratedRegex(@"^-\s*\[(WARN|COMMIT|STOP|SKIP|DISABLED)\]\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BracketRule();

    [GeneratedRegex(@"^-\s*(.+?)\s*\((WARN|COMMIT|STOP|SKIP|DISABLED)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixRule();

    [GeneratedRegex(@"^\s*(?:```|~~~)")]
    private static partial Regex FenceDelimiter();

    [GeneratedRegex(@"^-\s+\S")]
    private static partial Regex BareRule();

    /// <summary>Reads every rule declared by <paramref name="content"/>, in file order.</summary>
    public static List<AgentRuleLine> Read(string? content)
    {
        List<AgentRuleLine> rules = [];
        if (string.IsNullOrEmpty(content))
        {
            return rules;
        }

        var insideFence = false;
        var insideRulesSection = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (FenceDelimiter().IsMatch(line))
            {
                insideFence = !insideFence;
                continue;
            }

            if (insideFence)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                insideRulesSection = IsRulesHeading(trimmed);
                continue;
            }

            if (!insideRulesSection)
            {
                continue;
            }

            var bracket = BracketRule().Match(trimmed);
            if (bracket.Success)
            {
                Add(rules, bracket.Groups[2].Value, bracket.Groups[1].Value);
                continue;
            }

            var suffix = SuffixRule().Match(trimmed);
            if (suffix.Success)
            {
                Add(rules, suffix.Groups[1].Value, suffix.Groups[2].Value);
                continue;
            }

            if (BareRule().IsMatch(trimmed))
            {
                Add(rules, trimmed[1..], "WARN");
            }
        }

        return rules;
    }

    /// <summary>True when <paramref name="headingLine"/> opens a rules section.</summary>
    public static bool IsRulesHeading(string headingLine) =>
        RulesHeadings.Contains(
            headingLine.TrimStart('#').Trim(),
            StringComparer.OrdinalIgnoreCase);

    private static void Add(List<AgentRuleLine> rules, string ruleText, string enforcement)
    {
        var text = ruleText.Trim();
        if (text.Length > 0)
        {
            rules.Add(new AgentRuleLine(text, enforcement.ToUpperInvariant()));
        }
    }
}
