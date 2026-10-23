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

    /// <summary>
    /// Codex review F1: the hook host registers the ledger against the user's state.db; under the
    /// harness it must resolve to the per-repository file, and only when a signature is pending.
    /// </summary>
    [Fact]
    public async Task HarnessKeepsTheLedgerInsideTheRepository()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/basic");
        Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var ledger = Path.Combine(repo.RepositoryPath, ".git", "vca-regression-state.db");

        // pre-commit defers signature rules, so nothing pending: the ledger is never opened.
        var preCommit = await repo.RunPreCommitAsync();
        Assert.Equal(0, preCommit.ExitCode);
        Assert.False(TableExists(ledger, "ReviewRequests"));

        // commit-msg has a pending approval, consults the ledger, and finds the harness's file.
        var run = await repo.RunCommitMessageAsync("Change db\n");
        Assert.Equal(1, run.ExitCode);
        Assert.True(TableExists(ledger, "ReviewRequests"));
    }

    private static bool TableExists(string databasePath, string table)
    {
        if (!File.Exists(databasePath)) return false;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Codex review N1: four rules with four stored approvals used to spend the three popup rounds
    /// and block on the fourth. The ledger pass is now outside those rounds and applies them all.
    /// </summary>
    [Fact]
    public async Task EveryStoredApprovalIsAppliedInOnePass()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/four-rules");
        var keys = new Dictionary<Guid, string>
        {
            [Guid.NewGuid()] = "one@example.com",
            [Guid.NewGuid()] = "two@example.com",
            [Guid.NewGuid()] = "three@example.com",
            [Guid.NewGuid()] = "four@example.com"
        };
        var verifier = Use(repo, new FakeCommitSignatureVerifier(signature =>
            keys.TryGetValue(signature.KeyId, out var email)
                ? new CommitSignatureVerification(CommitSignatureStatus.Valid, "valid", email)
                : new CommitSignatureVerification(CommitSignatureStatus.Rejected, "unknown key")));
        var prompt = UseDecliningPrompt(repo);
        foreach (var name in new[] { "a.cs", "b.cs", "c.cs", "d.cs" })
            await File.WriteAllTextAsync(Path.Combine(repo.RepositoryPath, name), $"class {name[0]} {{ }}\n", TestContext.Current.CancellationToken);
        await repo.GitAsync("add", "--", "a.cs", "b.cs", "c.cs", "d.cs");
        var tree = await StagedTreeAsync(repo);
        var fill = 1;
        foreach (var (keyId, email) in keys)
        {
            // Distinct keys and distinct signature bytes: the trailer writer drops earlier lines from
            // the same key, and the ledger pass adds each signature once.
            await SeedApprovalAsync(repo, tree, email, keyId: keyId,
                signatureBase64: Convert.ToBase64String(Enumerable.Repeat((byte)fill++, 256).ToArray()));
        }

        var run = await repo.RunCommitMessageAsync("Change all four\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.Equal(4, Count(run.Transcript, "Applied the signed approval from"));
        Assert.Equal(1, Count(run.Transcript, "Checking the commit again"));
        Assert.Empty(prompt.Requests);
        var message = await File.ReadAllTextAsync(repo.CommitMessagePath, TestContext.Current.CancellationToken);
        Assert.Equal(keys.Keys.Order(), CommitSignatureFormat.Parse(message).Select(signature => signature.KeyId).Order());
        Assert.Equal(4, verifier.Calls.DistinctBy(signature => signature.KeyId).Count());
    }

    private static async Task SeedApprovalAsync(VcaRegressionRepository repo, string tree, string reviewer, string status = "open",
        Guid? keyId = null, string? signatureBase64 = null)
    {
        var store = UseStore(repo);
        var now = DateTime.UtcNow;
        var request = new ReviewRequestRecord(Guid.NewGuid().ToString("D"), repo.RepositoryPath, ReviewScope.Staged, tree, null,
            null, ReviewRequestStatus.Open, "{}", now, now);
        await store.AddAsync(request, TestContext.Current.CancellationToken);
        var outcome = await store.RecordDecisionAsync(new ReviewDecisionRecord(0, request.Id, reviewer, ReviewDecisionKind.Approved, null,
            keyId ?? HumanSignatureRuleTests.KeyId, HumanSignatureRuleTests.Fingerprint, tree,
            signatureBase64 ?? HumanSignatureRuleTests.SignatureBase64, now, now),
            now, TestContext.Current.CancellationToken);
        Assert.NotNull(outcome);
        if (status != ReviewRequestStatus.Open)
            Assert.True(await store.UpdateStatusAsync(request.Id, status, now, TestContext.Current.CancellationToken));
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
