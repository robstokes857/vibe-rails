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
    /// <summary>Reads one PR by number, including its current head, even outside the open-list limit.</summary>
    Task<GitHubPullRequest> GetAsync(GitHubRepository repository, string workingDirectory, int number,
        CancellationToken ct = default);

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

    /// <summary><c>gh help exit-codes</c>: 4 is "authentication required".</summary>
    internal const int GhAuthRequiredExitCode = 4;

    private static readonly IReadOnlyDictionary<string, string?> GhEnvironment = new Dictionary<string, string?>
    {
        ["GH_HOST"] = "github.com",
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["NO_COLOR"] = "1",
    };

    public async Task<GitHubPullRequest> GetAsync(GitHubRepository repository, string workingDirectory, int number,
        CancellationToken ct = default)
    {
        var result = await RunAsync(workingDirectory,
            ["pr", "view", number.ToString(CultureInfo.InvariantCulture), "-R", repository.FullName,
             "--json", "number,title,author,createdAt,updatedAt,isDraft,headRefOid"], ct);
        if (!result.IsSuccess)
            throw new GitHubCliException(Failure("read the pull request", repository, result));
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var pr = ParsePullRequest(document.RootElement, repository);
            return pr is not null && pr.Number == number && !string.IsNullOrWhiteSpace(pr.HeadSha)
                ? pr
                : throw new JsonException();
        }
        catch (JsonException)
        {
            throw new GitHubCliException("The GitHub CLI returned an unexpected pull request answer.");
        }
    }

    public async Task<IReadOnlyList<GitHubPullRequest>> ListOpenAsync(GitHubRepository repository, string workingDirectory,
        CancellationToken ct = default)
    {
        var result = await RunAsync(workingDirectory,
            ["pr", "list", "-R", repository.FullName, "--state", "open",
             "--limit", OpenListLimit.ToString(CultureInfo.InvariantCulture),
             "--json", "number,title,author,createdAt,updatedAt,isDraft,headRefOid,url"], ct);
        if (!result.IsSuccess)
            throw new GitHubCliException(Failure("list open pull requests", repository, result));
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
                throw new GitHubCliException(result.IsSuccess
                    ? "The GitHub CLI returned an unexpected pull request state answer."
                    : Failure("read pull request states", repository, result));
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
            if (ParsePullRequest(item, repository) is { } pr) result.Add(pr);
        }
        return result;
    }

    private static GitHubPullRequest? ParsePullRequest(JsonElement item, GitHubRepository repository)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("number", out var numberElement) || numberElement.ValueKind != JsonValueKind.Number
            || !numberElement.TryGetInt32(out var number) || number <= 0)
            return null;
        var opened = ReadTime(item, "createdAt");
        if (opened is null) return null;
        var author = item.TryGetProperty("author", out var authorElement) && authorElement.ValueKind == JsonValueKind.Object
            ? ReadString(authorElement, "login") : null;
        return new GitHubPullRequest(number, ReadString(item, "title") ?? "",
            string.IsNullOrWhiteSpace(author) ? "unknown" : author, opened.Value, ReadTime(item, "updatedAt"),
            item.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            ReadString(item, "headRefOid") ?? "", repository.PullRequestUrl(number));
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

    /// <summary>
    /// Fixed wording chosen by how the run ended. gh's stderr can carry GitHub's own error prose and
    /// this message reaches the page and MCP agents, so stderr is never reflected.
    /// </summary>
    internal static string Failure(string action, GitHubRepository repository, CliResult result) =>
        result.TimedOut
            ? $"The GitHub CLI (gh) did not answer within {Timeout.TotalSeconds:0} seconds while trying to {action} for {repository.FullName}."
        : result.Cancelled
            ? $"The request to {action} for {repository.FullName} was cancelled."
        : result.ExitCode == TerminalScriptBuilder.LaunchFailedExitCode
            ? "The GitHub CLI (gh) could not be started. Install it from https://cli.github.com, then sign in with `gh auth login`."
        : result.ExitCode == GhAuthRequiredExitCode
            ? "The GitHub CLI (gh) is not signed in. Run `gh auth login`, then try again."
        : $"Unable to {action} for {repository.FullName} (gh exited with code {result.ExitCode}). "
            + "Check `gh auth status` and that the signed-in account can read this repository.";

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? ReadTime(JsonElement element, string property) =>
        ReadString(element, property) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
}
