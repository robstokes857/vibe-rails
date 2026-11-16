using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class StopBypassTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "stop-bypass-" + Guid.NewGuid().ToString("N"));
    private readonly RSA rsa = RSA.Create(2048);
    private readonly Guid keyId = Guid.NewGuid();
    private readonly StopBypassStore store = new();
    private StopBypassService service = null!;
    private RulesTool tool = null!;
    private IGitStagedSnapshotProvider provider = null!;
    private IStopBypassVerifier verifier = null!;
    private Mock<IStopBypassPromptLauncher> launcher = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Rule = "File Lock('protected.txt')";
    private const string Reason = "Emergency repair for incident 42; normal maintenance follows tomorrow.";
    private string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(root);
        await Git("init", "-q");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "user.name", "Test");
        await Git("config", "core.hooksPath", ".git/hooks");
        await Git("config", "commit.gpgsign", "false");
        await Stage();
        var snapshots = new Mock<IGitStagedSnapshotProvider>();
        snapshots.Setup(x => x.CaptureAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) => new GitStagedSnapshotProvider().CaptureAsync(root, ct));
        provider = snapshots.Object;
        launcher = new();
        launcher.Setup(x => x.Launch(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
        var verification = new Mock<IStopBypassVerifier>();
        verification.Setup(x => x.VerifyAsync(It.IsAny<StopBypassApproval>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StopBypassApproval approval, CancellationToken _) =>
                approval.Signature.KeyId == keyId && approval.Signature.Fingerprint == Fingerprint
                && rsa.VerifyData(StopBypassFormat.BuildPayload(approval.Scope), Convert.FromBase64String(approval.Signature.SignatureBase64),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
                    ? new(CommitSignatureStatus.Valid, "valid", "owner@example.com")
                    : new CommitSignatureVerification(CommitSignatureStatus.Rejected, "Invalid signature"));
        verifier = verification.Object;
        service = new(provider, launcher.Object, verifier);
        tool = new(provider, stopBypasses: service);
    }

    private async Task Git(params string[] args)
    {
        var result = await GitCli.RunAsync(root, args, Ct);
        Assert.True(result.Succeeded, result.StdErr);
    }
    private async Task Stage(string rule = Rule, string level = "STOP")
    {
        await File.WriteAllTextAsync(Path.Combine(root, "vc.rules.md"), $"## Vibe Rails Rules\n- {rule} ({level})\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "protected.txt"), "repair\n", Ct);
        await Git("add", "vc.rules.md", "protected.txt");
    }
    private CommitSignature Sign(StopBypassScope scope) => new(keyId, Fingerprint, scope.StagedTree,
        Convert.ToBase64String(rsa.SignData(StopBypassFormat.BuildPayload(scope), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    private async Task<StopBypassApproval> Approve(string? message = null)
    {
        Assert.Contains("pending", await tool.RequestStopBypass(Reason, message, Ct));
        var request = (await store.ReadAsync(root, Ct))!;
        var approval = new StopBypassApproval(request.Scope, Sign(request.Scope));
        await store.SaveAsync(root, request with { Status = "approved", Approval = approval }, request.Id, Ct);
        return approval;
    }
    private async Task<(int Code, string Output, string Message)> Hook(string kind = "pre-commit", string message = "Repair",
        string[]? extraArgs = null, Action<IServiceCollection>? extraServices = null)
    {
        var path = Path.Combine(root, ".git", "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(path, message, Ct);
        string[] args = ["--vca-hook", kind, "--enqueue-automations", "--workdir", root, "--commit-message", path, .. extraArgs ?? []];
        var launches = new Mock<ILaunchStore>();
        launches.Setup(x => x.ListBindingsAsync(It.IsAny<string?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LaunchTriggerBinding>());
        using var output = new StringWriter();
        var code = await VcaHookProcessHost.RunCoreAsync(args, services =>
        {
            services.AddSingleton(verifier);
            services.AddSingleton(new LaunchEventService(launches.Object, new(launches.Object, Mock.Of<IAutomationScriptService>())));
            services.AddSingleton(Mock.Of<IBoardStore>());
            services.AddSingleton(Mock.Of<VibeRails.DB.IJobStore>(MockBehavior.Strict));
            services.AddSingleton(Mock.Of<VibeRails.DB.IReviewRequestStore>(MockBehavior.Strict));
            extraServices?.Invoke(services);
        }, output, output, cancellationToken: Ct);
        return (code, output.ToString(), await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task McpRequestIsPendingUntilHumanSignsAndRealCommitRecordsTheReason()
    {
        Assert.Equal(1, (await Hook()).Code);
        Assert.Contains("pending", await tool.RequestStopBypass(Reason, cancellationToken: Ct));
        Assert.Contains("pending", await tool.GetStopBypassStatus(Ct));
        Assert.Equal(1, (await Hook()).Code);
        var request = (await store.ReadAsync(root, Ct))!;
        var signer = new Mock<ICommitTreeSigner>();
        signer.Setup(x => x.ListKeysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        { new SigningKeyInfo(keyId, "Test key", rsa.ExportSubjectPublicKeyInfoPem(), Fingerprint, DateTimeOffset.UtcNow, keyId, DateTimeOffset.UtcNow) });
        signer.Setup(x => x.SignAsync(keyId, "private password", request.Scope.StagedTree, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sign(request.Scope));
        var console = new Mock<IHumanSignatureConsole>();
        console.Setup(x => x.ReadLineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("APPROVE");
        console.Setup(x => x.ReadSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("private password");
        using var output = new StringWriter();
        Assert.Equal(0, await new StopBypassPopup(output, console.Object, _ => signer.Object, verifier, provider).RunAsync(root, request.Id, Ct));
        Assert.DoesNotContain("private password", output.ToString());
        Assert.Contains(Reason, output.ToString());
        Assert.Contains(Rule, output.ToString());
        Assert.Contains("approved by owner@example.com", await tool.GetStopBypassStatus(Ct));
        Assert.Equal(0, (await Hook()).Code);
        var commit = await Hook("commit-msg", "Repair\r\n\r\n# ------------------------ >8 ------------------------\r\nignored diff\r\n");
        Assert.Equal(0, commit.Code);
        Assert.Contains("STOP BYPASSED", commit.Output);
        Assert.Contains(StopBypassFormat.ReasonTrailer + " " + Reason, commit.Message);
        Assert.True(commit.Message.IndexOf(StopBypassFormat.Trailer, StringComparison.Ordinal) < commit.Message.IndexOf(" >8 ", StringComparison.Ordinal));
        Assert.DoesNotContain(root, commit.Message);
        Assert.NotNull(StopBypassFormat.Parse(await VcaCommitMessageCleaner.StripCommentsAsync(commit.Message, root, Ct)));
        launcher.Verify(x => x.Launch(root, request.Id), Times.Once);
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("signed-reason")]
    [InlineData("rule")]
    [InlineData("key")]
    [InlineData("missing-reason")]
    public async Task AlteredEvidenceCannotBypass(string change)
    {
        var approval = await Approve();
        var changed = change switch
        {
            "signed-reason" => approval with { Scope = approval.Scope with { Reason = "Different rationale" } },
            "rule" => approval with { Scope = approval.Scope with { Rules = [new("other/vc.rules.md", Rule)] } },
            "key" => approval with { Signature = approval.Signature with { KeyId = Guid.NewGuid() } },
            _ => approval
        };
        var message = "Repair\n\n" + StopBypassFormat.Format(changed);
        if (change == "reason") message = message.Replace(StopBypassFormat.ReasonTrailer + " " + Reason, StopBypassFormat.ReasonTrailer + " Changed");
        if (change == "missing-reason") message = message.Replace(StopBypassFormat.ReasonTrailer + " " + Reason, "");
        Assert.Equal(1, (await Hook("commit-msg", message)).Code);
    }

    [Fact]
    public async Task ChangedSnapshotRequiresNewApprovalAndOrdinarySignatureIsInsufficient()
    {
        var approval = await Approve();
        await File.AppendAllTextAsync(Path.Combine(root, "protected.txt"), "another change\n", Ct);
        await Git("add", "protected.txt");
        Assert.Equal(1, (await Hook()).Code);
        Assert.Contains("changed", await tool.GetStopBypassStatus(Ct));
        Assert.Equal(1, (await Hook("commit-msg", "Repair\n" + StopBypassFormat.Format(approval))).Code);
        Assert.Equal(1, (await Hook("commit-msg", "Repair\n" + CommitSignatureFormat.Format(approval.Signature))).Code);
    }

    [Fact]
    public async Task DeferredMessageRuleCanBeApprovedAndOrdinaryValidationHasNoSideEffects()
    {
        await Stage("Check commit message for: wip");
        var approval = await Approve("WIP necessary checkpoint");
        var check = await new VcaRulesHookValidationService(provider, stopBypassVerifier: verifier).ValidateAsync(
            new(VcaHookKind.PreCommit, null, root, false, TimeSpan.Zero, false), root, Ct);
        Assert.False(check.Summary.HasStopViolation); // Still deferred, no request mutation.
        Assert.Equal(approval.Scope.Reason, (await store.ReadAsync(root, Ct))!.Scope.Reason);
        Assert.Equal(0, (await Hook("commit-msg", "WIP necessary checkpoint")).Code);
    }

    [Fact]
    public async Task FreshApprovalReplacesGenuineStaleTrailersWhenAmending()
    {
        var prior = await Approve();
        await File.AppendAllTextAsync(Path.Combine(root, "protected.txt"), "additional repair\n", Ct);
        await Git("add", "protected.txt");
        var current = await Approve();
        var result = await Hook("commit-msg", "Repair\n\n" + StopBypassFormat.Format(prior));
        Assert.Equal(0, result.Code);
        var recorded = StopBypassFormat.Parse(result.Message);
        Assert.Equal(current.Scope.StagedTree, recorded!.Scope.StagedTree);
        Assert.DoesNotContain(prior.Signature.SignatureBase64, result.Message);
    }

    [Fact]
    public async Task NamedApproverCannotBeOverriddenByAnotherKeyHolder()
    {
        const string rule = "Signed Approval('protected.txt', 'other@example.com')";
        await Stage(rule);
        await Git("commit", "-qm", "Install approval policy");
        await File.AppendAllTextAsync(Path.Combine(root, "protected.txt"), "change\n", Ct);
        await Git("add", "protected.txt");
        await Approve();
        Assert.Equal(1, (await Hook("commit-msg")).Code);
    }

    [Fact]
    public async Task DeclinedPromptNeverReadsAPasswordOrSigns()
    {
        await tool.RequestStopBypass(Reason, cancellationToken: Ct);
        var request = (await store.ReadAsync(root, Ct))!;
        var console = new Mock<IHumanSignatureConsole>();
        console.Setup(x => x.ReadLineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("no");
        var signer = new Mock<ICommitTreeSigner>(MockBehavior.Strict);
        using var output = new StringWriter();
        Assert.Equal(1, await new StopBypassPopup(output, console.Object, _ => signer.Object, verifier, provider).RunAsync(root, request.Id, Ct));
        Assert.Equal("cancelled", (await store.ReadAsync(root, Ct))!.Status);
        console.Verify(x => x.ReadSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, (await Hook()).Code);
    }

    [Fact]
    public async Task EmptyReasonAndNonStopChecksCannotRequestApproval()
    {
        Assert.Contains("not requested", await tool.RequestStopBypass(" ", cancellationToken: Ct));
        await Stage(level: "WARN");
        Assert.Contains("No STOP violations", await tool.RequestStopBypass(Reason, cancellationToken: Ct));
        Assert.Null(await store.ReadAsync(root, Ct));
        launcher.Verify(x => x.Launch(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReplacedRequestsCannotBeApprovedByOldPopups()
    {
        await tool.RequestStopBypass(Reason, cancellationToken: Ct);
        var first = (await store.ReadAsync(root, Ct))!;
        await tool.RequestStopBypass("New reason", cancellationToken: Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(root, first with { Status = "approved" }, first.Id, Ct));
        Assert.Equal("New reason", (await store.ReadAsync(root, Ct))!.Scope.Reason);
    }

    [Theory]
    [InlineData(CommitSignatureStatus.Unavailable)]
    [InlineData(CommitSignatureStatus.Rejected)]
    public async Task UnavailableOrRevokedKeysNeverOpenTheGate(CommitSignatureStatus status)
    {
        await Approve();
        var unavailable = new Mock<IStopBypassVerifier>();
        unavailable.Setup(x => x.VerifyAsync(It.IsAny<StopBypassApproval>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommitSignatureVerification(status, "Not verified"));
        verifier = unavailable.Object;
        Assert.Equal(1, (await Hook()).Code);
        Assert.Equal(1, (await Hook("commit-msg")).Code);
    }

    [Fact]
    public async Task AddedReasonCannotSilentlyViolateAnUncoveredMessageRule()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "vc.rules.md"),
            $"## Vibe Rails Rules\n- {Rule} (STOP)\n- Check commit message for: emergency (STOP)\n", Ct);
        await Git("add", "vc.rules.md");
        await Approve("Repair"); // The reason contains Emergency; the original message does not.
        Assert.Equal(0, (await Hook()).Code);
        var result = await Hook("commit-msg", "Repair");
        Assert.Equal(1, result.Code);
        Assert.Contains(StopBypassFormat.ReasonTrailer, result.Message);
    }

    [Fact]
    public async Task MessageViolationAfterScissorsStillBlocksWithAnUnrelatedSignedException()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "vc.rules.md"),
            $"## Vibe Rails Rules\n- {Rule} (STOP)\n- Check commit message for: wip (STOP)\n", Ct);
        await Git("add", "vc.rules.md");
        await Approve("Repair");
        var result = await Hook("commit-msg", "Repair\n# ------------------------ >8 ------------------------\nWIP retained text\n");
        Assert.Equal(1, result.Code);
        Assert.Contains("Check commit message for: wip", result.Output);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("whitespace", true)]
    [InlineData("whitespace", false)]
    [InlineData("verbatim", true)]
    [InlineData("verbatim", false)]
    [InlineData("scissors", true)]
    [InlineData("scissors", false)]
    public async Task ScissorsCannotHideRuleViolationsInNonEditorCommits(string? cleanup, bool fromFile)
    {
        await Stage("Check commit message for: wip");
        if (cleanup is not null) await Git("config", "commit.cleanup", cleanup);
        const string message = "Complete change\n# ------------------------ >8 ------------------------\nWIP should block\n";
        var result = await Hook("commit-msg", message);
        Assert.Equal(1, result.Code);
        Assert.Contains("Check commit message for: wip", result.Output);

        // This fixture has no installed hooks: independently establish what Git would retain.
        var inputPath = Path.Combine(root, ".git", "message-input");
        await File.WriteAllTextAsync(inputPath, message, Ct);
        await Git("commit", fromFile ? "-F" : "-m", fromFile ? inputPath : message);
        var recorded = await GitCli.RunAsync(root, ["log", "-1", "--format=%B"], Ct);
        Assert.True(recorded.Succeeded, recorded.StdErr);
        Assert.Contains("WIP should block", recorded.StdOut);
    }

    [Fact]
    public async Task CustomCommentScissorsKeepsEvidenceAboveTheDiscardedDiff()
    {
        await Approve();
        await Git("config", "core.commentChar", ";");
        var result = await Hook("commit-msg", "Repair\n; ------------------------ >8 ------------------------\ndiff text\n");
        Assert.Equal(0, result.Code);
        Assert.NotNull(StopBypassFormat.Parse(StopBypassFormat.BeforeScissors(result.Message)));
        var inputPath = Path.Combine(root, ".git", "message-input");
        await File.WriteAllTextAsync(inputPath, result.Message, Ct);
        // ':' is the shell's no-op builtin: exercise a real edited/scissors commit without a UI.
        await Git("-c", "core.editor=:", "commit", "--edit", "--cleanup=scissors", "-F", inputPath);
        var recorded = await GitCli.RunAsync(root, ["log", "-1", "--format=%B"], Ct);
        Assert.True(recorded.Succeeded, recorded.StdErr);
        Assert.DoesNotContain("diff text", recorded.StdOut);
        Assert.NotNull(StopBypassFormat.Parse(recorded.StdOut));
    }

    [Fact]
    public async Task EvidenceOnlyBelowScissorsCannotApproveACommit()
    {
        var approval = await Approve();
        var request = (await store.ReadAsync(root, Ct))!;
        await store.SaveAsync(root, request with { Status = "cancelled", Approval = null }, request.Id, Ct);
        var result = await Hook("commit-msg", "Repair\n# ------------------------ >8 ------------------------\n" + StopBypassFormat.Format(approval));
        Assert.Equal(1, result.Code);
    }

    [Fact]
    public async Task GitCommentSettingsCannotDiscardTheSignedReason()
    {
        await Approve();
        await Git("config", "core.commentChar", "V");
        var result = await Hook("commit-msg", "Repair");
        Assert.Equal(1, result.Code);
        Assert.Contains("message cleanup", result.Output);
    }

    [Fact]
    public async Task PopupModeRefusesRedirectedAgentExecution()
    {
        using var output = new StringWriter();
        Assert.Equal(1, await VcaHookProcessHost.RunCoreAsync(
            ["--vca-hook", "sign-stop-bypass", "--workdir", root, "--bypass-request", "test"], null,
            output, output, cancellationToken: Ct));
        Assert.Contains("separate human popup", output.ToString());
    }

    public ValueTask DisposeAsync()
    {
        rsa.Dispose();
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing non-temp cleanup.");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
