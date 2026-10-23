using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// VB-50: an approval a reviewer already gave over the staged tree, stored in the review-request
/// ledger, satisfies <c>Signed Approval</c> and <c>Human verified</c> through the real commit-msg
/// hook without the popup. The hook still verifies the applied signature with (a fake) viberails.ai.
/// </summary>
public sealed class StoredReviewApprovalTests
{
    [Fact]
    public async Task ApprovalFromAListedReviewerIsAppliedAndPassesWithoutThePopup()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/basic");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var prompt = UseDecliningPrompt(repo);
        var tree = await StagedTreeAsync(repo);
        await SeedApprovalAsync(repo, tree, "owner@example.com");

        // No --prompt-acknowledgment: this is how an agent's commit runs, and it passes on the person's approval.
        var run = await repo.RunCommitMessageAsync("Change db\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.Contains("Applied the signed approval from owner@example.com", run.Transcript);
        Assert.Contains("Checking the commit again", run.Transcript);
        Assert.Empty(prompt.Requests);
        var message = await File.ReadAllTextAsync(repo.CommitMessagePath, TestContext.Current.CancellationToken);
        var signature = Assert.Single(CommitSignatureFormat.Parse(message));
        Assert.Equal(tree, signature.TreeId);
        Assert.Equal(tree, Assert.Single(verifier.Calls).TreeId);
    }

    [Fact]
    public async Task HumanVerifiedAcceptsAnyStoredVerifiedApproval()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        Use(repo, FakeCommitSignatureVerifier.ValidFor("anyone@example.com"));
        await SeedApprovalAsync(repo, await StagedTreeAsync(repo), "anyone@example.com");

        var run = await repo.RunCommitMessageAsync("Fix bug\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.Contains("Applied the signed approval from anyone@example.com", run.Transcript);
    }

    [Theory]
    [InlineData("intruder@example.com", "open", false)]
    [InlineData("owner@example.com", "cancelled", false)]
    [InlineData("owner@example.com", "open", true)]
    public async Task ApprovalsThatDoNotFitAreLeftAlone(string reviewer, string status, bool otherTree)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/basic");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor(reviewer));
        var prompt = UseDecliningPrompt(repo);
        var tree = otherTree ? new string('0', 40) : await StagedTreeAsync(repo);
        await SeedApprovalAsync(repo, tree, reviewer, status);

        var run = await repo.RunCommitMessageAsync("Change db\n");

        Assert.Equal(1, run.ExitCode);
        Assert.DoesNotContain("Applied the signed approval", run.Transcript);
        Assert.Contains("did not open the signing popup", run.Transcript);
        Assert.Empty(verifier.Calls);
        Assert.Empty(prompt.Requests);
    }

    [Fact]
    public async Task UnavailableVerificationAppliesTheApprovalOnceAndStops()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/basic");
        Use(repo, new FakeCommitSignatureVerifier(_ =>
            new CommitSignatureVerification(CommitSignatureStatus.Unavailable, "viberails.ai could not be reached")));
        await SeedApprovalAsync(repo, await StagedTreeAsync(repo), "owner@example.com");

        var run = await repo.RunCommitMessageAsync("Change db\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(1, Count(run.Transcript, "Applied the signed approval"));
        Assert.Contains("could not verify the signature", run.Transcript);
    }

    [Fact]
    public async Task PopupIsStillOfferedWhenTheLedgerHasNothing()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/basic");
        Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var prompt = UseDecliningPrompt(repo);
        UseStore(repo);

        var run = await repo.RunCommitMessageAsync("Change db\n", "--prompt-acknowledgment");

        Assert.Equal(1, run.ExitCode);
        Assert.Single(prompt.Requests);
        Assert.Contains("No signature was added", run.Transcript);
    }

    private static async Task SeedApprovalAsync(VcaRegressionRepository repo, string tree, string reviewer, string status = "open")
    {
        var store = UseStore(repo);
        var now = DateTime.UtcNow;
        var request = new ReviewRequestRecord(Guid.NewGuid().ToString("D"), repo.RepositoryPath, ReviewScope.Staged, tree, null,
            null, status, "{}", now, now);
        await store.AddAsync(request, TestContext.Current.CancellationToken);
        await store.AddDecisionAsync(new ReviewDecisionRecord(0, request.Id, reviewer, ReviewDecisionKind.Approved, null,
            HumanSignatureRuleTests.KeyId, HumanSignatureRuleTests.Fingerprint, tree, HumanSignatureRuleTests.SignatureBase64, now, now),
            TestContext.Current.CancellationToken);
    }

    private static ReviewRequestStore UseStore(VcaRegressionRepository repo)
    {
        // The same per-repository file the harness gives the job store, so the hook never opens the live state.db.
        var store = new ReviewRequestStore(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(repo.RepositoryPath, ".git", "vca-regression-reviews.db"),
            Pooling = false
        }.ToString());
        var previous = repo.ConfigureServices;
        repo.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IReviewRequestStore>(store));
        };
        return store;
    }

    private static FakeCommitSignatureVerifier Use(VcaRegressionRepository repo, FakeCommitSignatureVerifier verifier)
    {
        var previous = repo.ConfigureServices;
        repo.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<ICommitSignatureVerifier>(verifier));
        };
        return verifier;
    }

    private static DecliningPrompt UseDecliningPrompt(VcaRegressionRepository repo)
    {
        var prompt = new DecliningPrompt();
        var previous = repo.ConfigureServices;
        repo.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IHumanSignaturePrompt>(prompt));
        };
        return prompt;
    }

    private static async Task<string> StagedTreeAsync(VcaRegressionRepository repo) =>
        (await repo.GitAsync("write-tree")).Trim();

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>Records every popup request and never signs, so a pass proves the ledger did the work.</summary>
    private sealed class DecliningPrompt : IHumanSignaturePrompt
    {
        public List<HumanSignatureRequest> Requests { get; } = [];

        public Task<bool> RequestAsync(VcaHookInvocation invocation, string workingDirectory,
            IReadOnlyList<VcaRuleFinding> pendingSignatures, CancellationToken cancellationToken)
        {
            Requests.Add(pendingSignatures[0].SignatureRequest!);
            return Task.FromResult(false);
        }
    }
}
