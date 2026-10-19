using System.Text.RegularExpressions;

namespace VibeRails.Services.AzureDevOps;

/// <summary>
/// The WIQL a team board pull runs. Every literal comes from a value the server returned (work
/// item types, states, team field values), written with <c>'</c> doubled. The one piece of user
/// text, the Advanced narrowing, is wrapped in parentheses and ANDed on, exactly like Jira's
/// narrowing JQL, after <see cref="ValidateNarrowing"/> has refused an ORDER BY or unbalanced
/// parentheses that would let it widen the query.
/// </summary>
public static partial class AdoWiql
{
    public const int MaxNarrowingLength = 2000;

    /// <summary>How long a done work item stays in the pull after its last change.</summary>
    public const int SkipOldDoneDays = 30;

    /// <summary>
    /// A WIQL string literal. This is WIQL, not PowerShell (see <c>PowerShellValueQuotingTests</c>):
    /// a doubled <c>'</c> is WIQL's only escape, and curly quotes do not end a WIQL string.
    /// </summary>
    public static string Literal(string value) => "'" + string.Join("''", value.Split('\'')) + "'";

    /// <summary>Trimmed narrowing, or null when blank. Rejects ORDER BY, unbalanced parentheses and an oversized query.</summary>
    public static string? ValidateNarrowing(string? wiql)
    {
        var text = wiql?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > MaxNarrowingLength)
            throw new AdoConfigException($"Narrowing WIQL is limited to {MaxNarrowingLength} characters.");
        if (!Balanced(text))
            throw new AdoConfigException("Narrowing WIQL has unbalanced parentheses or quotes. It is ANDed onto the board's own query.");
        if (TopLevelOrderBy(text) >= 0)
            throw new AdoConfigException("Narrowing WIQL can't have ORDER BY. The pull keeps the most recently changed work items first.");
        return text;
    }

    /// <summary>
    /// The board's work items: its types, within the team's field values, never Removed, most
    /// recently changed first. With <paramref name="typeCategory"/> (no board configuration) the
    /// types are that category instead. <paramref name="doneStates"/> are the (type, state) pairs of
    /// the board's outgoing column; with <paramref name="skipOldDone"/> those are kept only while
    /// changed in the last 30 days.
    /// </summary>
    public static string BoardQuery(
        IReadOnlyCollection<string> types, string? typeCategory, AdoTeamFieldValues team,
        IReadOnlyCollection<(string Type, string State)> doneStates, bool skipOldDone, string? narrowing)
    {
        if (!IsFieldReference(team.Field))
            throw new AdoConfigException("Azure DevOps returned a team field that can't be queried.");
        if (team.Values.Count == 0)
            throw new AdoConfigException("The team has no area paths, so its board has no work items.");

        var clauses = new List<string> { "[System.TeamProject] = @project" };
        if (types.Count > 0)
            clauses.Add("[System.WorkItemType] IN (" + string.Join(", ", types.Select(Literal)) + ")");
        else if (typeCategory is not null)
            clauses.Add("[System.WorkItemType] IN GROUP " + Literal(typeCategory));
        else
            throw new AdoConfigException("The board has no work item types to pull.");
        clauses.Add(TeamClause(team));
        clauses.Add("[System.State] <> 'Removed'");
        if (skipOldDone && doneStates.Count > 0)
        {
            // Not done (for each done type and state, a different type or a different state), or
            // changed recently. WIQL has no NOT over a group, so the pairs are written out.
            var notDone = string.Join(" AND ", doneStates.Select(pair =>
                $"([System.WorkItemType] <> {Literal(pair.Type)} OR [System.State] <> {Literal(pair.State)})"));
            clauses.Add($"(({notDone}) OR [System.ChangedDate] >= @today - {SkipOldDoneDays})");
        }
        if (!string.IsNullOrWhiteSpace(narrowing))
            clauses.Add("(" + narrowing.Trim() + ")");

        return "SELECT [System.Id] FROM WorkItems WHERE " + string.Join(" AND ", clauses)
            + " ORDER BY [System.ChangedDate] DESC";
    }

    /// <summary>A field reference name such as <c>System.AreaPath</c> or <c>Custom.Team</c>.</summary>
    public static bool IsFieldReference(string? value) =>
        value is { Length: > 0 and <= 128 } && FieldReferencePattern().IsMatch(value);

    // Area paths take UNDER for a value that includes its children; any other team field is exact.
    private static string TeamClause(AdoTeamFieldValues team)
    {
        var field = "[" + team.Field + "]";
        var tree = string.Equals(team.Field, "System.AreaPath", StringComparison.OrdinalIgnoreCase);
        var exact = team.Values.Where(value => !(tree && value.IncludeChildren)).Select(value => Literal(value.Value)).ToList();
        var parts = new List<string>();
        if (exact.Count > 0)
            parts.Add($"{field} IN ({string.Join(", ", exact)})");
        parts.AddRange(team.Values.Where(value => tree && value.IncludeChildren).Select(value => $"{field} UNDER {Literal(value.Value)}"));
        return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
    }

    /// <summary>The index of the last ORDER BY outside a quoted string, or -1.</summary>
    internal static int TopLevelOrderBy(string wiql)
    {
        var found = -1;
        foreach (Match match in OrderByPattern().Matches(wiql))
        {
            if (!InsideQuotes(wiql, match.Index))
                found = match.Index;
        }
        return found;
    }

    // WIQL quotes with ' or ", and doubles the quote to escape it, so a doubled quote closes and
    // reopens the same string: the toggle below reads it correctly.
    private static bool InsideQuotes(string text, int index)
    {
        char? open = null;
        for (var i = 0; i < index; i++)
        {
            var c = text[i];
            if (open is null && c is '"' or '\'')
                open = c;
            else if (open == c)
                open = null;
        }
        return open is not null;
    }

    private static bool Balanced(string text)
    {
        char? open = null;
        var depth = 0;
        foreach (var c in text)
        {
            if (open is not null)
            {
                if (c == open) open = null;
                continue;
            }
            if (c is '"' or '\'') open = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return open is null && depth == 0;
    }

    [GeneratedRegex(@"\border\s+by\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrderByPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex FieldReferencePattern();
}
