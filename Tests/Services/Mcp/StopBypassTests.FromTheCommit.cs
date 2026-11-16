using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// VIBE-331: a person whose deliberate change is blocked by STOP is offered the signed exception
/// by Git Guard itself. Before, only an agent could ask for one, through request_stop_bypass.
/// These run the real hook host on a real repository; only the person (console answers, key) is faked.
/// </summary>
public sealed partial class StopBypassTests
{
    private const string TypedReason = "Retargeting the terminal projects to net11.0 on purpose; the lock predates it.";
    private static readonly string[] PersonCanAnswer = ["--prompt-acknowledgment"];

    /// <summary>Git Guard's offer, with a person who types <paramref name="typed"/> as the reason.</summary>
    private StopBypassHookPrompt PersonAtGitGuard(TextWriter said, string? typed, Func<string, Task<int?>> approve)
    {
        var console = new Mock<IHumanSignatureConsole>(MockBehavior.Strict);
        console.Setup(x => x.ReadLineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(typed);
        return new StopBypassHookPrompt(said, said, new StopBypassService(provider, verifier: verifier), console.Object,
            canPrompt: () => true, approve: (_, _, requestId, _) => approve(requestId));
    }

    /// <summary>The real approval popup, answered by the key holder: APPROVE, then the key password.</summary>
    private async Task<int?> OwnerApproves(string requestId)
    {
        var request = (await store.ReadAsync(root, Ct))!;
        Assert.Equal(requestId, request.Id);
        var signer = new Mock<ICommitTreeSigner>();
        signer.Setup(x => x.ListKeysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        { new SigningKeyInfo(keyId, "Test key", rsa.ExportSubjectPublicKeyInfoPem(), Fingerprint, DateTimeOffset.UtcNow, keyId, DateTimeOffset.UtcNow) });
        signer.Setup(x => x.SignAsync(keyId, "private password", request.Scope.StagedTree, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sign(request.Scope));
        var console = new Mock<IHumanSignatureConsole>();
        console.Setup(x => x.ReadLineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("APPROVE");
        console.Setup(x => x.ReadSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("private password");
        using var popup = new StringWriter();
        return await new StopBypassPopup(popup, console.Object, _ => signer.Object, verifier, provider).RunAsync(root, requestId, Ct);
    }

    private static Action<IServiceCollection> Offering(IStopBypassPrompt prompt) =>
        services => services.AddSingleton(prompt);

    [Fact]
    public async Task PersonBlockedByStopSignsAnExceptionAtTheCommitAndItsReasonIsRecorded()
    {
        using var said = new StringWriter();
        var prompt = PersonAtGitGuard(said, "  " + TypedReason + " ", OwnerApproves);

        var preCommit = await Hook(extraArgs: PersonCanAnswer, extraServices: Offering(prompt));

        Assert.Equal(0, preCommit.Code);
        Assert.Contains("A STOP rule blocks this commit. If the change is deliberate", said.ToString());
        Assert.Contains("Exception approved. Checking the commit again...", preCommit.Output);
        Assert.Contains("The signed exception covers this commit.", preCommit.Output);
        var request = (await store.ReadAsync(root, Ct))!;
        Assert.Equal("approved", request.Status);
        Assert.Equal(TypedReason, request.Scope.Reason);
        Assert.Equal(new[] { new StopBypassRule("vc.rules.md", Rule) }, request.Scope.Rules);
        // The person's own route: the agent-facing launcher never ran.
        launcher.Verify(x => x.Launch(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        // commit-msg needs no person: the approved exception is stored, and the hook records it.
        var commitMessage = await Hook("commit-msg", "Retarget the terminal projects");
        Assert.Equal(0, commitMessage.Code);
        Assert.Contains(StopBypassFormat.ReasonTrailer + " " + TypedReason, commitMessage.Message);
        Assert.Contains(StopBypassFormat.RuleTrailer + " vc.rules.md | " + Rule, commitMessage.Message);
        Assert.NotNull(StopBypassFormat.Parse(commitMessage.Message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)] // Esc, or nobody answered before the idle timeout.
    public async Task ClosingTheOfferRequestsNothingAndKeepsTheCommitBlocked(string? typed)
    {
        using var said = new StringWriter();
        var windows = 0;
        var prompt = PersonAtGitGuard(said, typed, _ => { windows++; return Task.FromResult<int?>(0); });

        var result = await Hook(extraArgs: PersonCanAnswer, extraServices: Offering(prompt));

        Assert.Equal(1, result.Code);
        Assert.True(prompt.Declined);
        Assert.Equal(0, windows);
        Assert.Null(await store.ReadAsync(root, Ct));
        Assert.Contains("No exception requested. The commit stays blocked.", said.ToString());
    }

    [Fact]
    public async Task AnApprovalWindowsExitCodeIsNotTheGate()
    {
        // Something that types in the window can end it with 0. Without a verified signature in the
        // store, the recheck still blocks, and the request stays pending.
        var prompt = PersonAtGitGuard(TextWriter.Null, TypedReason, _ => Task.FromResult<int?>(0));

        var result = await Hook(extraArgs: PersonCanAnswer, extraServices: Offering(prompt));

        Assert.Equal(1, result.Code);
        Assert.Contains("The commit is still blocked.", result.Output);
        Assert.Equal("pending", (await store.ReadAsync(root, Ct))!.Status);
    }

    [Fact]
    public async Task NobodyIsAskedWithoutSomewhereToAnswerAndTheGuidanceStillSaysWhatToDo()
    {
        var console = new Mock<IHumanSignatureConsole>(MockBehavior.Strict);
        var prompt = new StopBypassHookPrompt(TextWriter.Null, TextWriter.Null, new StopBypassService(provider, verifier: verifier),
            console.Object, canPrompt: () => true, approve: null);

        // CI or a redirected commit: the hook script passes no prompt flag.
        var result = await Hook(extraServices: Offering(prompt));

        Assert.Equal(1, result.Code);
        console.VerifyNoOtherCalls();
        Assert.Null(await store.ReadAsync(root, Ct));
        Assert.Contains("edit or lower the rule at vc.rules.md line 2", result.Output);
        Assert.Contains("have a person sign an exception with a reason", result.Output);
        Assert.Contains("request_stop_bypass", result.Output);
        Assert.DoesNotContain("no-verify", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommitLevelFindingsNeverOpenTheOffer()
    {
        await Stage(level: "COMMIT");
        var console = new Mock<IHumanSignatureConsole>(MockBehavior.Strict);
        var prompt = new StopBypassHookPrompt(TextWriter.Null, TextWriter.Null, new StopBypassService(provider, verifier: verifier),
            console.Object, canPrompt: () => true, approve: null);

        var result = await Hook(extraArgs: PersonCanAnswer, extraServices: Offering(prompt));

        Assert.Equal(0, result.Code);
        console.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheExceptionCoversPreCommitRulesAndTheFinalMessageIsStillJudged()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "vc.rules.md"),
            $"## Vibe Rails Rules\n- {Rule} (STOP)\n- Check commit message for: wip (STOP)\n", Ct);
        await Git("add", "vc.rules.md");
        var prompt = PersonAtGitGuard(TextWriter.Null, TypedReason, OwnerApproves);

        Assert.Equal(0, (await Hook(extraArgs: PersonCanAnswer, extraServices: Offering(prompt))).Code);
        Assert.Equal(new[] { new StopBypassRule("vc.rules.md", Rule) }, (await store.ReadAsync(root, Ct))!.Scope.Rules);

        var blocked = await Hook("commit-msg", "WIP retarget");
        Assert.Equal(1, blocked.Code);
        Assert.Contains("Check commit message for: wip", blocked.Output);
        Assert.Equal(0, (await Hook("commit-msg", "Retarget the terminal projects")).Code);
    }
}
