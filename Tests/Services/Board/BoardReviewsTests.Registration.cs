using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardReviewsTests
{
    [Fact]
    public async Task ExplicitBeginRegistersAnOrdinarySessionAndPreservesItsHistory()
    {
        var session = Guid.NewGuid().ToString();
        await repository.CreateSessionAsync(session, "codex", null, repo, Environment.ProcessId);
        await store.LinkSessionAsync(repo, card.Id, session, "tab", "base:codex", "codex", "Ordinary work", "mcp", Ct);
        var original = await store.AddCommentAsync(repo, card.Id, BoardAuthor.Agent("Codex", "codex", session), "Earlier work", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.cs"), "class Reviewed {}\n", Ct);

        var begun = await reviews.BeginAsync(repo, card.Id, session, repo, "working-tree", "Requested review", null, null, true, Ct);

        Assert.Equal("Codex", begun.Provider);
        Assert.Equal("Codex", begun.Reviewer);
        Assert.Equal(session, begun.SessionId);
        Assert.Equal("tab", begun.TabId);
        Assert.Null(begun.RunId);
        Assert.Null(begun.Routing);
        Assert.NotNull(begun.SnapshotHash);
        Assert.Contains(begun.ScopeFiles!, file => file.Contains("sample.cs"));
        Assert.Contains(session, await store.GetReviewSessionIdsAsync(repo, [session], Ct));
        var detail = (await service.GetCardAsync(repo, card.Id, Ct))!;
        Assert.True(Assert.Single(detail.Sessions).IsReview);
        Assert.Equal("mcp", Assert.Single(await store.GetAgentSessionsAsync(repo, card.Id, session, Ct)).Session.Origin);
        Assert.Equivalent(original, Assert.Single((await store.GetCardDetailAsync(repo, card.Id, Ct))!.Comments));

        await reviews.SaveAsync(repo, card.Id, session, begun.Id, "No findings reported", "None", "Inspected sample.cs", "No runtime tests", Ct);
        var reopened = new BoardReviewService(new BoardStore(boardPath, state), service);
        Assert.Equal(begun.Id, (await reopened.ReadAsync(repo, card.Id, 0, Ct))!.Latest!.Id);
        Assert.StartsWith("Current", (await reopened.ReportAsync(repo, card.Id, begun.Id, true, repo, Ct))!.Freshness);
    }

    [Fact]
    public async Task ConcurrentRegistrationKeepsOneCaptureAndTheFirstSavedReport()
    {
        var session = Guid.NewGuid().ToString();
        await store.LinkSessionAsync(repo, card.Id, session, null, "base:codex", "codex", "Codex", "launch", Ct);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            reviews.BeginAsync(repo, card.Id, session, repo, "repository", $"Review request {i}", null, null, true, Ct)));

        var first = Assert.Single(await store.GetReviewsAsync(repo, card.Id, cancellationToken: Ct));
        Assert.NotNull(first.CapturedUtc);
        Assert.NotNull(first.SnapshotHash);
        Assert.All(attempts, attempt => Assert.Equivalent(first, attempt));
        await reviews.SaveAsync(repo, card.Id, session, first.Id, "Findings", "sample.cs:1 — finding", "Read code", "None", Ct);
        var repeated = await reviews.BeginAsync(repo, card.Id, session, repo, "unknown", "A later request", null, null, true, Ct);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.ScopeDescription, repeated.ScopeDescription);
        Assert.Equal("Findings", repeated.Result);
        Assert.Single(await store.GetReviewsAsync(repo, card.Id, cancellationToken: Ct));
        Assert.Single((await store.GetCardDetailAsync(repo, card.Id, Ct))!.Comments);
    }
}
