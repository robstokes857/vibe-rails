using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.VcaRegression;

/// <summary>
/// Human verified and Signed Approval through the real hook host. viberails.ai and the signing popup
/// are replaced by fakes; everything else (staging, the staged tree id, policy from HEAD and the
/// index, the WARN/COMMIT/STOP contract, the runner's popup rounds) is the production path.
/// </summary>
public sealed class HumanSignatureRuleTests
{
    private const string ApprovalRule = "Signed Approval('db.cs', 'owner@example.com')";

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task HumanVerified_MissingSignatureHonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertCommitMessageViolationHonorsLevelAsync(
            "human-verified/basic", level, HumanSignatureRule.HumanVerifiedName, "Fix bug\n");

    [Theory]
    [InlineData("WARN")]
    [InlineData("COMMIT")]
    [InlineData("STOP")]
    public Task SignedApproval_MissingApprovalHonorsEnforcementLevel(string level) =>
        EnforcementLevels.AssertCommitMessageViolationHonorsLevelAsync(
            "signed-approval/basic", level, ApprovalRule, "Change the database layer\n");

    [Fact]
    public async Task PreCommitAndMcpDeferWithPopupGuidance()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var run = await repo.RunPreCommitAndCrossCheckAsync();
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[DEFERRED] " + HumanSignatureRule.HumanVerifiedName, run.Transcript);
        Assert.Contains("Git Guard popup", run.Transcript);
        Assert.Contains("Agents cannot create this signature", run.Transcript);
    }

    [Fact]
    public async Task ValidSignatureOverTheStagedTreePasses()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        var tree = await StagedTreeAsync(repo);

        var run = await repo.RunCommitMessageAsync($"Fix bug\n\n{SignatureLine(tree)}\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
        var checkedSignature = Assert.Single(verifier.Calls);
        Assert.Equal(tree, checkedSignature.TreeId);
    }

    [Fact]
    public async Task SignatureSurvivesTrailerCleanupAndCrlf()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        var tree = await StagedTreeAsync(repo);

        var run = await repo.RunCommitMessageAsync(
            $"Fix bug\r\n\r\nCo-authored-by: Agent <agent@example.invalid>\r\n{SignatureLine(tree)}\r\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
    }

    [Fact]
    public async Task SignatureForADifferentTreeIsRejectedWithoutAskingTheServer()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));

        var run = await repo.RunCommitMessageAsync($"Fix bug\n\n{SignatureLine(new string('0', 40))}\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("changed after signing", run.Transcript);
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task CommentedSignatureDoesNotCount()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        var tree = await StagedTreeAsync(repo);

        var run = await repo.RunCommitMessageAsync($"Fix bug\n\n# {SignatureLine(tree)}\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task UnreachableServiceFailsClosed()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        Use(repo, new FakeCommitSignatureVerifier(_ =>
            new CommitSignatureVerification(CommitSignatureStatus.Unavailable, "viberails.ai could not be reached")));
        var tree = await StagedTreeAsync(repo);

        var run = await repo.RunCommitMessageAsync($"Fix bug\n\n{SignatureLine(tree)}\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("could not verify the signature", run.Transcript);
    }

    [Fact]
    public async Task RejectedSignatureBlocks()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        Use(repo, new FakeCommitSignatureVerifier(_ =>
            new CommitSignatureVerification(CommitSignatureStatus.Rejected, "the signing key has been revoked")));
        var tree = await StagedTreeAsync(repo);

        var run = await repo.RunCommitMessageAsync($"Fix bug\n\n{SignatureLine(tree)}\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("the signing key has been revoked", run.Transcript);
    }

    [Fact]
    public async Task SignedApproval_RequiresAListedApprover()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/basic");
        var tree = await StagedTreeAsync(repo);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("intruder@example.com"));
        var wrongSigner = await repo.RunCommitMessageAsync($"Change db\n\n{SignatureLine(tree)}\n");
        Assert.Equal(1, wrongSigner.ExitCode);
        Assert.Contains("signed only by intruder@example.com", wrongSigner.Transcript);

        // viberails.ai returns the account's email as stored; approvers compare without case.
        Use(repo, FakeCommitSignatureVerifier.ValidFor("Owner@Example.com"));
        var approved = await repo.RunCommitMessageAsync($"Change db\n\n{SignatureLine(tree)}\n");
        Assert.True(approved.ExitCode == 0, approved.Transcript);
    }

    [Fact]
    public async Task SignedApproval_DirectoryAcceptsAnyListedApprover()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/directory");
        Use(repo, FakeCommitSignatureVerifier.ValidFor("second@example.com"));
        var tree = await StagedTreeAsync(repo);

        var run = await repo.RunCommitMessageAsync($"Change db\n\n{SignatureLine(tree)}\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
    }

    [Fact]
    public async Task SignedApproval_UntouchedPathNeedsNothing()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/untouched");
        var preCommit = await repo.RunPreCommitAndCrossCheckAsync();
        Assert.Equal(0, preCommit.ExitCode);
        Assert.DoesNotContain("[DEFERRED]", preCommit.Transcript);

        var commit = await repo.RunCommitMessageAsync("Edit other\n");
        Assert.True(commit.ExitCode == 0, commit.Transcript);
    }

    [Fact]
    public async Task RemovingHumanVerifiedStillNeedsASignature()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/remove-rule");
        var blocked = await repo.RunCommitMessageAsync("Drop the rule\n");
        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains("[STOP] " + HumanSignatureRule.HumanVerifiedName, blocked.Transcript);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        var signed = await repo.RunCommitMessageAsync($"Drop the rule\n\n{SignatureLine(await StagedTreeAsync(repo))}\n");
        Assert.True(signed.ExitCode == 0, signed.Transcript);
    }

    [Fact]
    public async Task DowngradingHumanVerifiedKeepsTheCommittedLevel()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/downgrade-rule");
        var run = await repo.RunCommitMessageAsync("Make it a warning\n");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[STOP] " + HumanSignatureRule.HumanVerifiedName, run.Transcript);
    }

    [Fact]
    public async Task DeletingTheRuleFileStillNeedsASignature()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/delete-rule-file");
        var run = await repo.RunCommitMessageAsync("Delete the policy\n");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("[STOP] " + HumanSignatureRule.HumanVerifiedName, run.Transcript);
    }

    /// <summary>
    /// PR #87 review P1: a staged vc.rules.md the snapshot cannot read as text used to drop the
    /// committed signature rules with it, and the hook passed with "No vc.rules.md files found".
    /// </summary>
    [Theory]
    [InlineData("human-verified/remove-rule", HumanSignatureRule.HumanVerifiedName, "binary")]
    [InlineData("human-verified/remove-rule", HumanSignatureRule.HumanVerifiedName, "oversized")]
    [InlineData("human-verified/remove-rule", HumanSignatureRule.HumanVerifiedName, "gitlink")]
    [InlineData("signed-approval/basic", ApprovalRule, "binary")]
    [InlineData("signed-approval/basic", ApprovalRule, "oversized")]
    [InlineData("signed-approval/basic", ApprovalRule, "gitlink")]
    public async Task UnreadableStagedPolicyStillNeedsTheCommittedSignature(string scenario, string rule, string replacement)
    {
        await using var repo = await VcaRegressionRepository.CreateAsync(scenario);
        var policy = Path.Combine(repo.RepositoryPath, "vc.rules.md");
        switch (replacement)
        {
            case "binary":
                await File.WriteAllBytesAsync(policy, [0], TestContext.Current.CancellationToken);
                await repo.GitAsync("add", "--", "vc.rules.md");
                break;
            case "oversized":
                // Text, but over the snapshot's 5 MiB text limit, so it is classified like a binary.
                await File.WriteAllTextAsync(policy, "## Vibe Rails Rules\n" + new string('x', 5 * 1024 * 1024) + "\n",
                    TestContext.Current.CancellationToken);
                await repo.GitAsync("add", "--", "vc.rules.md");
                break;
            case "gitlink":
                var head = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
                await repo.GitAsync("update-index", "--cacheinfo", $"160000,{head},vc.rules.md");
                break;
        }

        var blocked = await repo.RunCommitMessageAsync("Replace the policy\n");
        Assert.True(blocked.ExitCode == 1, blocked.Transcript);
        Assert.Contains("[STOP] " + rule, blocked.Transcript);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var signed = await repo.RunCommitMessageAsync($"Replace the policy\n\n{SignatureLine(await StagedTreeAsync(repo))}\n");
        Assert.True(signed.ExitCode == 0, signed.Transcript);
    }

    /// <summary>PR #87 review P2: the five-signature budget used to be spent before the tree filter.</summary>
    [Fact]
    public async Task FreshSignatureAfterFiveStaleOnesPasses()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        var tree = await StagedTreeAsync(repo);
        var stale = Enumerable.Range(1, 5).Select(i => SignatureLine(new string('0', 40), (byte)i));

        var run = await repo.RunCommitMessageAsync($"Retry signing\n\n{string.Join('\n', stale)}\n{SignatureLine(tree)}\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.Equal(tree, Assert.Single(verifier.Calls).TreeId);
    }

    [Fact]
    public async Task OnlyTheNewestCurrentSignaturesAreVerified()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var verifier = Use(repo, new FakeCommitSignatureVerifier(_ =>
            new CommitSignatureVerification(CommitSignatureStatus.Rejected, "not a valid signature")));
        var tree = await StagedTreeAsync(repo);
        var lines = Enumerable.Range(1, CommitSignatureFormat.MaxVerifiedSignatures + 2)
            .Select(i => SignatureLine(tree, (byte)i))
            .ToList();

        var run = await repo.RunCommitMessageAsync($"Fix bug\n\n{string.Join('\n', lines)}\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(
            CommitSignatureFormat.Parse(string.Join('\n', lines)).TakeLast(CommitSignatureFormat.MaxVerifiedSignatures),
            verifier.Calls);
    }

    [Fact]
    public async Task PopupReplacesStaleSignaturesAndPasses()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        UsePrompt(repo, sign: true);
        var stale = Enumerable.Range(1, 5).Select(i => SignatureLine(new string('0', 40), (byte)i));

        var run = await repo.RunCommitMessageAsync($"Amended\n\n{string.Join('\n', stale)}\n", "--prompt-acknowledgment");

        Assert.True(run.ExitCode == 0, run.Transcript);
        var message = await File.ReadAllTextAsync(repo.CommitMessagePath, TestContext.Current.CancellationToken);
        Assert.Equal(await StagedTreeAsync(repo), Assert.Single(CommitSignatureFormat.Parse(message)).TreeId);
    }

    [Fact]
    public async Task RemovingAnApprovalNeedsTheApprover()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/remove-rule");
        var blocked = await repo.RunCommitMessageAsync("Drop the approval\n");
        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains("Removing or weakening the signed approval for 'db.cs'", blocked.Transcript);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var approved = await repo.RunCommitMessageAsync($"Drop the approval\n\n{SignatureLine(await StagedTreeAsync(repo))}\n");
        Assert.True(approved.ExitCode == 0, approved.Transcript);
    }

    /// <summary>
    /// VIBE-200: adding an approval, even one over vc.rules.md and beside a change to its path, needs
    /// no signature under it. The next commit that touches either path does.
    /// </summary>
    [Fact]
    public async Task NewApprovalTakesEffectFromTheNextCommit()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/introduce");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var prompt = UsePrompt(repo, sign: true);

        var preCommit = await repo.RunPreCommitAndCrossCheckAsync();
        Assert.True(preCommit.ExitCode == 0, preCommit.Transcript);
        Assert.Contains("New in this commit", preCommit.Transcript);
        Assert.DoesNotContain("[DEFERRED]", preCommit.Transcript);

        var introduce = await repo.RunCommitMessageAsync("Add approvals\n", "--prompt-acknowledgment");
        Assert.True(introduce.ExitCode == 0, introduce.Transcript);
        Assert.Empty(prompt.Requests);
        Assert.Empty(verifier.Calls);
        Assert.False((await repo.ValidateWithMcpToolAsync("Add approvals\n")).HasStopViolation);
        await repo.GitAsync("commit", "-qm", "Add approvals", "--no-verify");

        await File.WriteAllTextAsync(Path.Combine(repo.RepositoryPath, "db.cs"), "class Db { int Version = 3; }\n",
            TestContext.Current.CancellationToken);
        await repo.GitAsync("add", "--", "db.cs");
        var protectedChange = await repo.RunCommitMessageAsync("Change db\n");
        Assert.Equal(1, protectedChange.ExitCode);
        Assert.Contains("Changes to 'db.cs'", protectedChange.Transcript);
        var signed = await repo.RunCommitMessageAsync($"Change db\n\n{SignatureLine(await StagedTreeAsync(repo))}\n");
        Assert.True(signed.ExitCode == 0, signed.Transcript);
        await repo.GitAsync("commit", "-qm", "Change db", "--no-verify");

        // The approval over the policy file now covers edits that leave the db.cs approval alone.
        var policy = Path.Combine(repo.RepositoryPath, "vc.rules.md");
        await File.AppendAllTextAsync(policy, "- Package file changes (WARN)\n", TestContext.Current.CancellationToken);
        await repo.GitAsync("add", "--", "vc.rules.md");
        var policyEdit = await repo.RunCommitMessageAsync("Add a rule\n");
        Assert.Equal(1, policyEdit.ExitCode);
        Assert.Contains("Changes to 'vc.rules.md'", policyEdit.Transcript);
    }

    [Fact]
    public async Task NewHumanVerifiedTakesEffectFromTheNextCommit()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/introduce");
        var prompt = UsePrompt(repo, sign: true);

        var introduce = await repo.RunCommitMessageAsync("Require signatures\n", "--prompt-acknowledgment");
        Assert.True(introduce.ExitCode == 0, introduce.Transcript);
        Assert.Contains("New in this commit", introduce.Transcript);
        Assert.Empty(prompt.Requests);
        await repo.GitAsync("commit", "-qm", "Require signatures", "--no-verify");

        await File.WriteAllTextAsync(Path.Combine(repo.RepositoryPath, "app.txt"), "hello once more\n",
            TestContext.Current.CancellationToken);
        await repo.GitAsync("add", "--", "app.txt");
        var next = await repo.RunCommitMessageAsync("Edit app\n");
        Assert.Equal(1, next.ExitCode);
        Assert.Contains("[STOP] " + HumanSignatureRule.HumanVerifiedName, next.Transcript);
    }

    [Fact]
    public async Task RaisingALevelAppliesFromTheNextCommit()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/raise-level");

        var run = await repo.RunCommitMessageAsync("Make it a gate\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.Contains("raises it from WARN to STOP", run.Transcript);
        Assert.Contains("needs a person's signature", run.Transcript);
        Assert.DoesNotContain("[STOP] " + HumanSignatureRule.HumanVerifiedName, run.Transcript);
    }

    [Fact]
    public async Task ChangingTheApproversNeedsTheCommittedApprover()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/change-approvers");
        var tree = await StagedTreeAsync(repo);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("intruder@example.com"));
        var newApprover = await repo.RunCommitMessageAsync($"Hand over db\n\n{SignatureLine(tree)}\n");
        Assert.Equal(1, newApprover.ExitCode);
        Assert.Contains("Removing or weakening the signed approval for 'db.cs'", newApprover.Transcript);
        Assert.Contains("signed only by intruder@example.com", newApprover.Transcript);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var committedApprover = await repo.RunCommitMessageAsync($"Hand over db\n\n{SignatureLine(tree)}\n");
        Assert.True(committedApprover.ExitCode == 0, committedApprover.Transcript);
    }

    [Fact]
    public async Task RewordingAnApprovalIsNotAChangeToIt()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/reformat-rule");

        var run = await repo.RunCommitMessageAsync("Tidy the policy\n");

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.DoesNotContain("New in this commit", run.Transcript);
    }

    [Fact]
    public async Task MovingThePolicyFileNeedsTheApprover()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/move-rule-file");

        var blocked = await repo.RunCommitMessageAsync("Move the policy\n");
        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains("Removing or weakening the signed approval for 'db.cs'", blocked.Transcript);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var approved = await repo.RunCommitMessageAsync($"Move the policy\n\n{SignatureLine(await StagedTreeAsync(repo))}\n");
        Assert.True(approved.ExitCode == 0, approved.Transcript);
    }

    [Fact]
    public async Task MessageOnlyCommitStillNeedsASignature()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        await repo.GitAsync("commit", "-qm", "baseline", "--no-verify");
        var blocked = await repo.RunCommitMessageAsync("Amend message\n");
        Assert.Equal(1, blocked.ExitCode);

        Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        var signed = await repo.RunCommitMessageAsync($"Amend message\n\n{SignatureLine(await StagedTreeAsync(repo))}\n");
        Assert.True(signed.ExitCode == 0, signed.Transcript);
    }

    [Fact]
    public async Task PopupSignatureIsRecheckedAndAccepted()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var verifier = Use(repo, FakeCommitSignatureVerifier.ValidFor("person@example.com"));
        var prompt = UsePrompt(repo, sign: true);

        var run = await repo.RunCommitMessageAsync("Fix bug\n", "--prompt-acknowledgment");

        Assert.True(run.ExitCode == 0, run.Transcript);
        var request = Assert.Single(prompt.Requests);
        Assert.Equal(await StagedTreeAsync(repo), request.TreeId);
        Assert.Contains("Checking the commit again", run.Transcript);
        Assert.Contains(CommitSignatureFormat.Trailer, await File.ReadAllTextAsync(repo.CommitMessagePath, TestContext.Current.CancellationToken));
        Assert.Single(verifier.Calls);
    }

    [Fact]
    public async Task PopupPassesTheApproversForASignedApproval()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("signed-approval/basic");
        Use(repo, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var prompt = UsePrompt(repo, sign: true);

        var run = await repo.RunCommitMessageAsync("Change db\n", "--prompt-acknowledgment");

        Assert.True(run.ExitCode == 0, run.Transcript);
        Assert.Equal(["owner@example.com"], Assert.Single(prompt.Requests).Approvers);
    }

    [Fact]
    public async Task DeclinedPopupKeepsTheCommitBlocked()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var prompt = UsePrompt(repo, sign: false);

        var run = await repo.RunCommitMessageAsync("Fix bug\n", "--prompt-acknowledgment");

        Assert.Equal(1, run.ExitCode);
        Assert.Single(prompt.Requests);
        Assert.Contains("No signature was added", run.Transcript);
    }

    [Fact]
    public async Task PopupIsNotOfferedWithoutSomeoneToAnswerIt()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        var prompt = UsePrompt(repo, sign: true);

        var run = await repo.RunCommitMessageAsync("Fix bug\n");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(prompt.Requests);
        // VIBE-200: the block says why no popup opened, so nobody has to run the hook by hand.
        Assert.Contains("did not open the signing popup", run.Transcript);
    }

    [Fact]
    public async Task PopupIsNotOfferedWhileAnotherRuleBlocks()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/with-lock");
        var prompt = UsePrompt(repo, sign: true);

        var run = await repo.RunCommitMessageAsync("Fix bug\n", "--prompt-acknowledgment");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(prompt.Requests);
        Assert.Contains("other blocking violations must be fixed first", run.Transcript);
    }

    [Fact]
    public async Task UnavailableVerificationDoesNotReopenThePopup()
    {
        await using var repo = await VcaRegressionRepository.CreateAsync("human-verified/basic");
        Use(repo, new FakeCommitSignatureVerifier(_ =>
            new CommitSignatureVerification(CommitSignatureStatus.Unavailable, "viberails.ai could not be reached")));
        var prompt = UsePrompt(repo, sign: true);

        var run = await repo.RunCommitMessageAsync("Fix bug\n", "--prompt-acknowledgment");

        // One signing round; the recheck cannot reach the service, which no signature can fix.
        Assert.Equal(1, run.ExitCode);
        Assert.Single(prompt.Requests);
        Assert.Contains("could not verify the signature", run.Transcript);
    }

    internal static readonly Guid KeyId = Guid.Parse("8b3f6a52-6c43-4f6e-9a1e-2d7f1c0b9e44");
    internal static readonly string Fingerprint = new('a', 64);
    internal static readonly string SignatureBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 512).ToArray());

    internal static string SignatureLine(string tree) =>
        CommitSignatureFormat.Format(new CommitSignature(KeyId, Fingerprint, tree, SignatureBase64));

    /// <summary>A distinct signature per <paramref name="fill"/>, so lines do not collapse as duplicates.</summary>
    private static string SignatureLine(string tree, byte fill) =>
        CommitSignatureFormat.Format(new CommitSignature(Guid.NewGuid(), Fingerprint, tree,
            Convert.ToBase64String(Enumerable.Repeat(fill, 256).ToArray())));

    private static async Task<string> StagedTreeAsync(VcaRegressionRepository repo) =>
        (await repo.GitAsync("write-tree")).Trim();

    private static FakeCommitSignatureVerifier Use(VcaRegressionRepository repo, FakeCommitSignatureVerifier verifier)
    {
        var prompt = repo.ConfigureServices;
        repo.ConfigureServices = services =>
        {
            prompt?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<ICommitSignatureVerifier>(verifier));
        };
        return verifier;
    }

    private static FakeSignaturePrompt UsePrompt(VcaRegressionRepository repo, bool sign)
    {
        var prompt = new FakeSignaturePrompt(sign);
        var previous = repo.ConfigureServices;
        repo.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            services.Replace(ServiceDescriptor.Singleton<IHumanSignaturePrompt>(prompt));
        };
        return prompt;
    }

    /// <summary>Stands in for the popup: appends a signature over the tree the hook asked for.</summary>
    private sealed class FakeSignaturePrompt(bool sign) : IHumanSignaturePrompt
    {
        public List<HumanSignatureRequest> Requests { get; } = [];

        public async Task<bool> RequestAsync(VcaHookInvocation invocation, string workingDirectory,
            IReadOnlyList<VcaRuleFinding> pendingSignatures, CancellationToken cancellationToken)
        {
            var request = pendingSignatures[0].SignatureRequest!;
            Requests.Add(request);
            if (!sign) return false;
            await HumanSignaturePopup.AppendSignatureAsync(invocation.CommitMessagePath!,
                new CommitSignature(KeyId, Fingerprint, request.TreeId, SignatureBase64), cancellationToken);
            return true;
        }
    }
}

/// <summary>Answers verification from a function and records every signature it was asked about.</summary>
internal sealed class FakeCommitSignatureVerifier(Func<CommitSignature, CommitSignatureVerification> answer)
    : ICommitSignatureVerifier
{
    public List<CommitSignature> Calls { get; } = [];

    public static FakeCommitSignatureVerifier ValidFor(string email) =>
        new(_ => new CommitSignatureVerification(CommitSignatureStatus.Valid, "valid", email));

    public Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add(signature);
        return Task.FromResult(answer(signature));
    }
}
