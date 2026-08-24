using System.Text.RegularExpressions;

namespace VibeRails.Services.VCA;

/// <summary>One rule line as written in a vc.rules.md rules section.</summary>
/// <param name="RuleText">The rule as the user wrote it, with any enforcement marker removed.</param>
/// <param name="Enforcement">Upper-case enforcement token; <c>WARN</c> when the line declared none.</param>
public sealed record AgentRuleLine(string RuleText, string Enforcement);

/// <summary>
/// Parser output used by the rules-page writer. Keeping the source line and owning section next
/// to the parsed rule lets CRUD target exactly the same live Markdown that discovery reports,
/// without teaching a second parser about headings or code fences.
/// </summary>
internal sealed record LocatedAgentRuleLine(
    AgentRuleLine Rule,
    int LineIndex,
    int SectionHeadingLineIndex);

internal sealed record AgentRuleSection(int HeadingLineIndex);

internal sealed record AgentRuleDocument(
    List<AgentRuleSection> Sections,
    List<LocatedAgentRuleLine> Rules);

/// <summary>
/// The one answer to "which rules does this vc.rules.md declare".
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
    public static List<AgentRuleLine> Read(string? content) =>
        ParseDocument(content).Rules.Select(rule => rule.Rule).ToList();

    /// <summary>
    /// Reads the same rule-discovery contract while retaining source locations for safe edits.
    /// This is intentionally internal: callers that only enforce or display rules should use
    /// <see cref="Read"/> and remain independent of the Markdown layout.
    /// </summary>
    internal static AgentRuleDocument ParseDocument(string? content)
    {
        List<AgentRuleSection> sections = [];
        List<LocatedAgentRuleLine> rules = [];
        if (string.IsNullOrEmpty(content))
        {
            return new AgentRuleDocument(sections, rules);
        }

        var insideFence = false;
        var insideRulesSection = false;
        var sectionHeadingLineIndex = -1;

        var lines = content.Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var rawLine = lines[lineIndex];
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
                sectionHeadingLineIndex = insideRulesSection ? lineIndex : -1;
                if (insideRulesSection)
                {
                    sections.Add(new AgentRuleSection(lineIndex));
                }
                continue;
            }

            if (!insideRulesSection)
            {
                continue;
            }

            var bracket = BracketRule().Match(trimmed);
            if (bracket.Success)
            {
                Add(rules, bracket.Groups[2].Value, bracket.Groups[1].Value, lineIndex, sectionHeadingLineIndex);
                continue;
            }

            var suffix = SuffixRule().Match(trimmed);
            if (suffix.Success)
            {
                Add(rules, suffix.Groups[1].Value, suffix.Groups[2].Value, lineIndex, sectionHeadingLineIndex);
                continue;
            }

            if (BareRule().IsMatch(trimmed))
            {
                Add(rules, trimmed[1..], "WARN", lineIndex, sectionHeadingLineIndex);
            }
        }

        return new AgentRuleDocument(sections, rules);
    }

    /// <summary>True when <paramref name="headingLine"/> opens a rules section.</summary>
    public static bool IsRulesHeading(string headingLine) =>
        RulesHeadings.Contains(
            headingLine.TrimStart('#').Trim(),
            StringComparer.OrdinalIgnoreCase);

    private static void Add(
        List<LocatedAgentRuleLine> rules,
        string ruleText,
        string enforcement,
        int lineIndex,
        int sectionHeadingLineIndex)
    {
        var text = ruleText.Trim();
        if (text.Length > 0)
        {
            rules.Add(new LocatedAgentRuleLine(
                new AgentRuleLine(text, enforcement.ToUpperInvariant()),
                lineIndex,
                sectionHeadingLineIndex));
        }
    }
}
