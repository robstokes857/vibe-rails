using System.Globalization;
using System.Text;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Cli;

namespace VibeRails.Services.PrReviews;

/// <summary>The GitHub CLI could not answer. The message is safe to show and says what to fix.</summary>
public sealed class GitHubCliException(string message) : Exception(message);

/// <summary>Read-only pull request queries. Nothing here writes to GitHub.</summary>
public interface IGitHubPullRequestClient
{
    /// <summary>The repository's open pull requests, at most <see cref="GitHubPullRequestClient.OpenListLimit"/>.</summary>
    Task<IReadOnlyList<GitHubPullRequest>> ListOpenAsync(GitHubRepository repository, string workingDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Each number's lifecycle, asked by number: the open list cannot tell merged from closed from
    /// "past the list limit", because all three are simply absent from it.
    /// </summary>
    Task<IReadOnlyList<PrGitHubStateUpdate>> GetStatesAsync(GitHubRepository repository, string workingDirectory,
        IReadOnlyList<int> numbers, CancellationToken ct = default);
}

/// <summary>
/// Runs the user's authenticated <c>gh</c> through <see cref="ICliWrapper"/>: argv only, stdin
/// closed, prompts and update notices off. Every call names the repository explicitly, so the
/// answer never depends on which checkout <c>gh</c> happens to start in.
/// </summary>
public sealed class GitHubPullRequestClient(ICliWrapper cli) : IGitHubPullRequestClient
{
    public const int OpenListLimit = 100;

    /// <summary>One GraphQL request stays well inside the argv limit and GitHub's node budget.</summary>
    internal const int StateBatchSize = 50;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const int MaxErrorDetail = 500;

    private static readonly IReadOnlyDictionary<string, string?> GhEnvironment = new Dictionary<string, string?>
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["NO_COLOR"] = "1",
    };

    public async Task<IReadOnlyList<GitHubPullRequest>> ListOpenAsync(GitHubRepository repository, string workingDirectory,
        CancellationToken ct = default)
    {
        var result = await RunAsync(workingDirectory,
            ["pr", "list", "-R", repository.FullName, "--state", "open",
             "--limit", OpenListLimit.ToString(CultureInfo.InvariantCulture),
             "--json", "number,title,author,createdAt,updatedAt,isDraft,headRefOid,url"], ct);
        if (!result.IsSuccess)
            throw new GitHubCliException($"Unable to list open pull requests for {repository.FullName}: {Detail(result)}");
        try
        {
            return ParseOpenPullRequests(result.StandardOutput, repository);
        }
        catch (JsonException)
        {
            throw new GitHubCliException("The GitHub CLI returned an unexpected open pull request list.");
        }
    }

    public async Task<IReadOnlyList<PrGitHubStateUpdate>> GetStatesAsync(GitHubRepository repository, string workingDirectory,
        IReadOnlyList<int> numbers, CancellationToken ct = default)
    {
        var wanted = numbers.Where(n => n > 0).Distinct().ToList();
        var states = new List<PrGitHubStateUpdate>();
        foreach (var batch in wanted.Chunk(StateBatchSize))
        {
            var result = await RunAsync(workingDirectory,
                ["api", "graphql", "-f", $"query={BuildStatesQuery(batch)}",
                 "-f", $"owner={repository.Owner}", "-f", $"name={repository.Name}"], ct);
            // A number that is not a PR in this repository fails the whole request, but GitHub still
            // returns data for every alias that resolved. Take that partial answer rather than lose
            // the batch to one bad row.
            IReadOnlyList<PrGitHubStateUpdate>? parsed = null;
            try { parsed = ParseStates(result.StandardOutput, batch); }
            catch (JsonException) { }
            if (parsed is null)
                throw new GitHubCliException($"Unable to read pull request states for {repository.FullName}: {Detail(result)}");
            states.AddRange(parsed);
        }
        return states;
    }

    internal static IReadOnlyList<GitHubPullRequest> ParseOpenPullRequests(string json, GitHubRepository repository)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a JSON array of pull requests.");

        var result = new List<GitHubPullRequest>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("number", out var numberElement) || !numberElement.TryGetInt32(out var number) || number <= 0)
                continue;
            var opened = ReadTime(item, "createdAt");
            if (opened is null) continue;
            var author = item.TryGetProperty("author", out var authorElement) && authorElement.ValueKind == JsonValueKind.Object
                ? ReadString(authorElement, "login") : null;
            result.Add(new GitHubPullRequest(
                number,
                ReadString(item, "title") ?? "",
                string.IsNullOrWhiteSpace(author) ? "unknown" : author,
                opened.Value,
                ReadTime(item, "updatedAt"),
                item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                ReadString(item, "headRefOid") ?? "",
                repository.PullRequestUrl(number)));
        }
        return result;
    }

    /// <summary>Aliased by number so one request answers the whole batch.</summary>
    internal static string BuildStatesQuery(IEnumerable<int> numbers)
    {
        var query = new StringBuilder("query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) {");
        foreach (var number in numbers)
            query.Append(CultureInfo.InvariantCulture, $" pr{number}: pullRequest(number: {number}) {{ number state mergedAt closedAt }}");
        return query.Append(" } }").ToString();
    }

    /// <summary>Null when the output carries no repository data at all.</summary>
    internal static IReadOnlyList<PrGitHubStateUpdate>? ParseStates(string json, IReadOnlyList<int> numbers)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object)
            return null;

        var states = new List<PrGitHubStateUpdate>();
        foreach (var number in numbers)
        {
            if (!repository.TryGetProperty($"pr{number}", out var node) || node.ValueKind != JsonValueKind.Object) continue;
            var state = ReadString(node, "state")?.ToLowerInvariant();
            if (!PrGitHubState.IsRecordable(state)) continue;
            // The instant it stopped being open, whichever way it went.
            states.Add(new PrGitHubStateUpdate(number, state!, ReadTime(node, "mergedAt") ?? ReadTime(node, "closedAt")));
        }
        return states;
    }

    private Task<CliResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) =>
        cli.RunAsync(new CliRequest("gh", arguments, workingDirectory, GhEnvironment, Timeout), cancellationToken: ct);

    private static string Detail(CliResult result)
    {
        var detail = (string.IsNullOrWhiteSpace(result.StandardError) ? result.DescribeFailure() : result.StandardError).Trim();
        if (detail.Length > MaxErrorDetail) detail = detail[..MaxErrorDetail] + "…";
        return detail + " Check that the GitHub CLI (gh) is installed and signed in with `gh auth status`.";
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? ReadTime(JsonElement element, string property) =>
        ReadString(element, property) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
}
