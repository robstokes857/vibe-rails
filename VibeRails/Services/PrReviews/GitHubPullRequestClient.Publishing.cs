using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeRails.DB;
using VibeRails.Services.Cli;

namespace VibeRails.Services.PrReviews;

public sealed partial class GitHubPullRequestClient
{
    /// <inheritdoc />
    public async Task<string> GetViewerAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await RunAsync(workingDirectory, ["api", "--hostname", "github.com", "--method", "GET", "user"], ct);
        if (!result.IsSuccess) throw new GitHubCliException("Cannot authenticate GitHub publishing. Check `gh auth status` and sign in with `gh auth login`.");
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var login = ReadString(json.RootElement, "login");
            if (login is not null && Regex.IsMatch(login, "^[A-Za-z0-9-]{1,100}(\\[bot\\])?$")) return login;
        }
        catch (JsonException) { }
        throw new GitHubCliException("GitHub did not confirm the publishing identity.");
    }

    /// <inheritdoc />
    public async Task<long?> FindReviewAsync(GitHubRepository repository, string workingDirectory, PrReviewDelivery delivery,
        CancellationToken ct = default)
    {
        ValidateTarget(repository, delivery);
        // Bound reconciliation. An incomplete search can never authorize a second POST.
        for (var page = 1; page <= 20; page++)
        {
            var result = await RunAsync(workingDirectory,
                ["api", "--hostname", "github.com", "--method", "GET", $"repos/{repository.FullName}/pulls/{delivery.PrNumber}/reviews?per_page=100&page={page}"], ct);
            if (!result.IsSuccess) throw new GitHubCliException("Cannot check the GitHub review receipt. Check `gh auth status` and repository permissions.");
            try
            {
                using var json = JsonDocument.Parse(result.StandardOutput);
                if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
                foreach (var review in json.RootElement.EnumerateArray())
                    if (Receipt(review, delivery) is { } id) return id;
                if (json.RootElement.GetArrayLength() < 100) return null;
            }
            catch (JsonException) { throw new GitHubCliException("GitHub returned an unexpected review receipt list."); }
        }
        throw new GitHubCliException("The GitHub receipt search limit was reached. Delivery remains unconfirmed; no review was resent.");
    }

    /// <inheritdoc />
    public async Task<long> PublishReviewAsync(GitHubRepository repository, string workingDirectory, PrReviewDelivery delivery,
        CancellationToken ct = default)
    {
        ValidateTarget(repository, delivery);
        var payload = new JsonObject { ["commit_id"] = delivery.HeadSha, ["event"] = delivery.Event, ["body"] = delivery.Body };
        var result = await cli.RunAsync(new CliRequest("gh",
            ["api", "--hostname", "github.com", "--method", "POST", $"repos/{repository.FullName}/pulls/{delivery.PrNumber}/reviews", "--include", "--input", "-"],
            workingDirectory, GhEnvironment, Timeout, payload.ToJsonString()), cancellationToken: ct);
        var output = result.StandardOutput.Replace("\r\n", "\n");
        var split = output.IndexOf("\n\n", StringComparison.Ordinal);
        var firstLine = output.Split('\n', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var status = firstLine.Length > 1 && firstLine[0].StartsWith("HTTP/", StringComparison.Ordinal)
            && int.TryParse(firstLine[1], out var code) ? code : 0;
        if (!result.IsSuccess || status != 201)
        {
            // A received 4xx refusal did not create a review. Transport failures/5xx may have.
            var uncertain = result.TimedOut || result.Cancelled || ((status is < 400 or >= 500 or 408)
                && result.ExitCode is not (GhAuthRequiredExitCode or TerminalScriptBuilder.LaunchFailedExitCode));
            var message = status is 401 or 403
                ? "GitHub refused review publishing. Check `gh auth status` and pull-request write permission."
                : status == 422
                    ? "GitHub rejected the review. Check the reviewed commit and review permission; GitHub forbids requesting changes on your own PR."
                    : "GitHub did not confirm review publishing. Check authentication, repository permission and connectivity.";
            throw new GitHubCliException(message, uncertain);
        }
        try
        {
            using var json = JsonDocument.Parse(split >= 0 ? output[(split + 2)..] : "");
            if (Receipt(json.RootElement, delivery) is { } id) return id;
        }
        catch (JsonException) { }
        throw new GitHubCliException("GitHub did not return a matching review receipt. Retry checks for it without resending.", true);
    }

    private static long? Receipt(JsonElement review, PrReviewDelivery delivery)
    {
        if (review.ValueKind != JsonValueKind.Object
            || !review.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number <= 0
            || ReadString(review, "commit_id") != delivery.HeadSha
            || !MatchesState(ReadString(review, "state"), delivery.Event)
            || ReadString(review, "body")?.Replace("\r\n", "\n") != delivery.Body.Replace("\r\n", "\n")
            || !review.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
            || !string.Equals(ReadString(user, "login"), delivery.Login, StringComparison.OrdinalIgnoreCase)) return null;
        return number;
    }

    // Dismissal changes GitHub's current state but does not undo the original delivery.
    private static bool MatchesState(string? state, string reviewEvent) => reviewEvent == "REQUEST_CHANGES"
        ? state is "CHANGES_REQUESTED" or "DISMISSED"
        : state == "COMMENTED";

    private static void ValidateTarget(GitHubRepository repository, PrReviewDelivery delivery)
    {
        if (!string.Equals(repository.FullName, delivery.Repository, StringComparison.OrdinalIgnoreCase)
            || delivery.PrNumber <= 0 || !Regex.IsMatch(delivery.HeadSha, "^[0-9a-fA-F]{40}$")
            || delivery.Event is not ("COMMENT" or "REQUEST_CHANGES"))
            throw new GitHubCliException("A valid repository, PR and full reviewed commit SHA are required for publishing.");
    }
}
