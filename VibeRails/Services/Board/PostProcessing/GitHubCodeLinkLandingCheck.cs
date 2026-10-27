using System.Globalization;
using System.Text.Json;
using VibeRails.Services.Cli;
using VibeRails.Services.Git;
using VibeRails.Services.PrReviews;

namespace VibeRails.Services.Board.PostProcessing;

/// <summary>Checks saved links against this checkout's origin; a URL alone is not landing evidence.</summary>
public interface IBoardCodeLinkLandingCheck
{
    /// <summary>Verifies only the supplied links against the owning repository.</summary>
    Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<BoardCodeLink> links, CancellationToken cancellationToken);
}

/// <summary>Local commit checks and one bounded, noninteractive GitHub query for linked PRs.</summary>
public sealed class GitHubCodeLinkLandingCheck(IBoardDefaultBranchCheck defaultBranch, ICliWrapper cli) : IBoardCodeLinkLandingCheck
{
    /// <inheritdoc />
    public async Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<BoardCodeLink> links, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(projectPath)) return BoardCommitLanding.Unverifiable;
        var remote = await GitCli.RunAsync(projectPath, ["remote", "get-url", "origin"], cancellationToken);
        if (!remote.Succeeded || !GitHubRepository.TryParseRemote(remote.StdOut, out var repository))
            return BoardCommitLanding.Unverifiable;
        var references = links.Select(link => new Uri(BoardCodeLinks.Parse(link.Url).Url).AbsolutePath.Split('/'))
            .Where(parts => string.Equals(parts[1] + "/" + parts[2], repository.FullName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (references.Length == 0) return BoardCommitLanding.NotLanded;
        var shas = references.Where(parts => parts[3] == "commit").Select(parts => parts[4]).ToArray();
        var commitResult = shas.Length == 0 ? BoardCommitLanding.NotLanded
            : await defaultBranch.CheckAsync(projectPath, shas, cancellationToken);
        if (commitResult == BoardCommitLanding.Landed) return commitResult;
        var numbers = references.Where(parts => parts[3] == "pull")
            .Select(parts => int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .Where(number => number > 0).Distinct().ToArray();
        if (numbers.Length == 0) return commitResult;
        var fields = string.Join(' ', numbers.Select((number, index) =>
            $"pr{index}: pullRequest(number: {number.ToString(CultureInfo.InvariantCulture)}) {{ merged baseRefName }}"));
        var query = "query($owner:String!,$name:String!){repository(owner:$owner,name:$name){defaultBranchRef{name} " + fields + "}}";
        var result = await cli.RunAsync(new CliRequest("gh",
            ["api", "graphql", "--hostname", "github.com", "-f", "query=" + query,
             "-f", "owner=" + repository.Owner, "-f", "name=" + repository.Name], projectPath,
            new Dictionary<string, string?> { ["GH_HOST"] = "github.com", ["GH_PROMPT_DISABLED"] = "1", ["GH_NO_UPDATE_NOTIFIER"] = "1" },
            TimeSpan.FromSeconds(20)), cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var pulls = result.TimedOut || result.Cancelled || string.IsNullOrWhiteSpace(result.StandardOutput)
            ? BoardCommitLanding.Unverifiable
            : ReadPullRequests(result.StandardOutput, numbers.Length, result.IsSuccess);
        // A merged PR settles it; otherwise a retryable Git failure outranks unavailable GitHub.
        return pulls == BoardCommitLanding.Landed || commitResult != BoardCommitLanding.Failed
            ? pulls
            : BoardCommitLanding.Failed;
    }

    /// <summary>
    /// Reads the per-PR fields. gh exits non-zero when the reply carries GraphQL errors but still
    /// prints it, so the body is read either way. A deleted or mistyped PR number is a per-field
    /// NOT_FOUND: that link is not evidence, and it must neither hide another linked PR's merge
    /// nor make the whole answer unknown. Any other error leaves an unmerged answer unverifiable.
    /// </summary>
    internal static BoardCommitLanding ReadPullRequests(string output, int count, bool exitedCleanly)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            var repo = root.GetProperty("data").GetProperty("repository");
            var branch = repo.GetProperty("defaultBranchRef").GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(branch)) return BoardCommitLanding.Unverifiable;
            for (var index = 0; index < count; index++)
            {
                if (repo.TryGetProperty("pr" + index, out var pr) && pr.ValueKind == JsonValueKind.Object
                    && pr.GetProperty("merged").GetBoolean() && pr.GetProperty("baseRefName").GetString() == branch)
                    return BoardCommitLanding.Landed;
            }
            var errors = root.TryGetProperty("errors", out var list) ? list.EnumerateArray().ToArray() : [];
            if (errors.Length == 0 ? !exitedCleanly : errors.Any(error => !IsNotFound(error)))
                return BoardCommitLanding.Unverifiable;
            return BoardCommitLanding.NotLanded;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return BoardCommitLanding.Unverifiable;
        }
    }

    private static bool IsNotFound(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == "NOT_FOUND";
}
