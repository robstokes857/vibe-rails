using System.Text.RegularExpressions;

namespace VibeRails.Services.Jira;

/// <summary>
/// Narrowing JQL (VIBE-102). A board connection passes it as the board issue endpoint's
/// <c>jql</c>, which Jira applies on top of the board's own filter. A JQL connection ANDs it into
/// its query ahead of any ORDER BY. Narrowing never carries its own ORDER BY: the board's rank
/// order, or the query's, stays.
/// </summary>
public static partial class JiraJql
{
    public const int MaxNarrowingLength = 2000;

    /// <summary>The default narrowing: open issues, plus Done issues updated in the last 30 days.</summary>
    public const string SkipOldDoneClause = "statusCategory != Done OR updated >= -30d";

    /// <summary>Trimmed narrowing, or null when blank. Rejects ORDER BY and an oversized query.</summary>
    public static string? ValidateNarrowing(string? jql)
    {
        var text = jql?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > MaxNarrowingLength)
            throw new JiraConfigException($"Narrowing JQL is limited to {MaxNarrowingLength} characters.");
        if (TopLevelOrderBy(text) >= 0)
            throw new JiraConfigException("Narrowing JQL can't have ORDER BY. The board's own order is kept.");
        return text;
    }

    /// <summary>Each non-blank clause in parentheses, joined with AND. Null when every clause is blank.</summary>
    public static string? And(params string?[] clauses)
    {
        var parts = clauses.Where(clause => !string.IsNullOrWhiteSpace(clause)).Select(clause => clause!.Trim()).ToList();
        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join(" AND ", parts.Select(part => "(" + part + ")"))
        };
    }

    /// <summary>ANDs the narrowing into a full query, keeping the query's ORDER BY at the end.</summary>
    public static string Narrow(string source, string? narrowing)
    {
        if (string.IsNullOrWhiteSpace(narrowing))
            return source;
        var orderAt = TopLevelOrderBy(source);
        var body = (orderAt < 0 ? source : source[..orderAt]).Trim();
        var order = orderAt < 0 ? string.Empty : " " + source[orderAt..].Trim();
        return (body.Length == 0 ? narrowing.Trim() : And(body, narrowing)) + order;
    }

    /// <summary>The index of the last ORDER BY outside a quoted string, or -1.</summary>
    internal static int TopLevelOrderBy(string jql)
    {
        var found = -1;
        foreach (Match match in OrderByPattern().Matches(jql))
        {
            if (!InsideQuotes(jql, match.Index))
                found = match.Index;
        }
        return found;
    }

    private static bool InsideQuotes(string text, int index)
    {
        char? open = null;
        for (var i = 0; i < index; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }
            if (open is null && c is '"' or '\'')
                open = c;
            else if (open == c)
                open = null;
        }
        return open is not null;
    }

    [GeneratedRegex(@"\border\s+by\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrderByPattern();
}
