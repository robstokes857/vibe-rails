using Microsoft.Data.Sqlite;
using System.Text.Json;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Services.Workspaces;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class SwitchReviewerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "switch-reviewer-" + Guid.NewGuid().ToString("N"));
    private string project = null!, state = null!;
    private Repository repository = null!;
    private BoardStore boards = null!;
    private ReviewRoutingService routing = null!;
    private JobStore jobs = null!;
    private BoardCardRecord card = null!;
    private Mock<IJobExecutableResolver> executables = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        project = Path.Combine(root, "repo"); Directory.CreateDirectory(project);
        await Git("init"); await Git("config", "user.email", "tests@local"); await Git("config", "user.name", "Tests");
        await File.WriteAllTextAsync(Path.Combine(project, "file.txt"), "baseline", Ct);
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "baseline");
        state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        repository = new Repository(state);
        boards = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", state);
        executables = new Mock<IJobExecutableResolver>();
        executables.Setup(e => e.Resolve(It.IsAny<LLM>())).Returns("test-cli");
        routing = new(boards, repository, executables.Object);
        jobs = new JobStore(state, boards, new SnapshotFactory(routing));
        await boards.EnsureDefaultColumnsAsync(project, Ct);
        // Preserve coverage for recipes installed by older releases; new boards no longer seed them.
        await using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "board.db")};Pooling=False"))
        {
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO BoardStarterWorkflows (ColumnId, RecipeId) VALUES ($column, $recipe);";
            command.Parameters.AddWithValue("$column", (await boards.GetColumnsAsync(project, Ct))[3].Id);
            command.Parameters.AddWithValue("$recipe", BoardReviewDefaults.RecipeId);
            await command.ExecuteNonQueryAsync(Ct);
        }
        card = await boards.CreateCardAsync(project, new(null, "Work", "", "base:opencode", "medium", null, [], false), Ct);
    }

    [Theory]
    [InlineData("claude", "codex")]
    [InlineData("codex", "claude")]
    public async Task DefaultDirectionsUseExplicitCodingSource_NotAssignmentOrNewestSession(string coder, string reviewer)
    {
        await Source(coder);
        await Session("opencode", "chat");
        await Session("opencode", "code_review");
        var selected = await Resolve();
        Assert.Equal(reviewer, selected.Resolution.Provider);
        Assert.Equal(coder, selected.Resolution.Source.Provider);
        Assert.False(selected.Resolution.UsedFallback);
        Assert.False(selected.Resolution.Overridden);
        Assert.Equal("base:opencode", (await boards.FindCardAsync(project, card.Id, Ct))!.Assignee);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("commit")]
    public async Task DeterministicCheckScopesCannotBecomeLlmReviewScopes(string scope)
    {
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            routing.SaveSettingsAsync(project, card.Id, new(Scope: scope), Ct));
    }

    [Fact]
    public async Task SameCodingProviderIsRejected_ForMappingsFallbackAndOverride()
    {
        await Source("codex");
        var policy = ReviewerRouting.SwitchDefault() with { Mappings = [new("codex", new("base:codex")), new("opencode", new("base:claude"))] };
        Assert.Contains("different LLM", (await Assert.ThrowsAsync<BoardValidationException>(() => Resolve(policy))).Message);
        await Assert.ThrowsAsync<BoardValidationException>(() => Resolve(policy with { Mappings = [] }));
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.ResolveAsync(project, card.Key, ReviewerRouting.SwitchDefault(), new("base:codex"), Ct));
        var once = await routing.ResolveAsync(project, card.Key, policy, new("base:claude", new(Model: "sonnet[1m]")), Ct);
        Assert.Equal("claude", once.Resolution.Provider); Assert.True(once.Resolution.Overridden);
        Assert.Equal("sonnet[1m]", once.Resolution.Selected.Options!.Model); Assert.False(once.Resolution.Selected.Options.Yolo);
        await Source("opencode");
        Assert.Equal("claude", (await Resolve(policy)).Resolution.Provider);
        Assert.Equal("codex", (await Resolve()).Resolution.Provider);
        Assert.True((await Resolve()).Resolution.UsedFallback);
    }



    [Fact]
    public async Task SameProviderEnvironmentCannotBypassSwitch()
    {
        await Source("codex");
        var environment = new LLM_Environment { CustomName = "Another Codex", LLM = LLM.Codex, ProjectPath = project };
        await repository.SaveEnvironmentAsync(environment, Ct);
        var policy = ReviewerRouting.SwitchDefault() with { Mappings = [new("codex", new($"env:{environment.Id}:codex"))] };
        await Assert.ThrowsAsync<BoardValidationException>(() => Resolve(policy));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mixed")]
    [InlineData("human")]
    public async Task UnknownMixedAndHumanUseVisibleConfiguredFallback(string kind)
    {
        await Session("claude", "launch");
        await routing.SaveSettingsAsync(project, card.Id, new(SourceKind: kind, Description: "Shared changes"), Ct);
        var r = (await Resolve(ReviewerRouting.SwitchDefault() with { Fallback = new("base:opencode") })).Resolution;
        Assert.Equal(kind, r.Source.Kind); Assert.Null(r.Source.Provider);
        Assert.Equal("opencode", r.Provider); Assert.True(r.UsedFallback);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("planning")]
    [InlineData("code_review")]
    [InlineData("agent_manager")]
    [InlineData("agent_worker_planning")]
    public async Task DiscussionPlanningAndReviewsCannotBeDeclaredAsCoder_EvenOnAdditionalCards(string intent)
    {
        var session = await Session("claude", intent);
        var other = await boards.CreateCardAsync(project, new(null, "Other", "", null, "medium", null, [], false), Ct);
        await boards.LinkSessionAsync(project, other.Id, session, null, "base:claude", "claude", "Attached", "mcp", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, other.Id,
            new("session", session, "Claimed coding"), Ct));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("manager")]
    public async Task HistoricalNonCodingContextExcludesOldLaunchOriginWithoutRewritingIt(string intent)
    {
        var session = await Session("claude", "launch");
        await boards.RecordContextSampleAsync(project, card.Id, new(session, intent, "claude", "base:claude", 0, 0, 0, 0, "{}"), Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, card.Id, new("session", session, "Coding"), Ct));
        Assert.Equal("launch", (await boards.GetAgentSessionsAsync(project, card.Id, session, Ct)).Single().Session.Origin);
    }

    [Fact]
    public async Task MultiCardSessionNeedsIndependentScopeDeclaration_AndForeignSessionIsRejected()
    {
        var session = await Source("claude");
        var other = await boards.CreateCardAsync(project, new(null, "Other", "", null, "medium", null, [], false), Ct);
        await boards.LinkSessionAsync(project, other.Id, session, null, "base:claude", "claude", "Same session", "mcp", Ct);
        Assert.Null((await routing.ResolveAsync(project, other.Id, ReviewerRouting.SwitchDefault(), null, Ct)).Resolution.Source.Provider);
        await routing.SaveSettingsAsync(project, other.Id, new("session", session, "Only the work for the other card"), Ct);
        Assert.Equal("claude", (await routing.ResolveAsync(project, other.Id, ReviewerRouting.SwitchDefault(), null, Ct)).Resolution.Source.Provider);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, card.Id, new("session", "unlinked", "Wrong session"), Ct));
        Assert.Null(await routing.SaveSettingsAsync(root, card.Id, new(), Ct));
        Assert.False(await boards.SaveReviewSettingsAsync(root, card.Id, new(), Ct));
    }



    [Fact]
    public async Task MissingProviderCanBeInstalledThenRetried_WithoutSubstitution()
    {
        await Source("codex"); executables.Setup(e => e.Resolve(LLM.Claude)).Returns((string?)null);
        var snapshot = await Resolve();
        Assert.Equal("claude", snapshot.Resolution.Provider); Assert.NotNull(snapshot.Resolution.Problem);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(snapshot, Ct));
        executables.Setup(e => e.Resolve(LLM.Claude)).Returns("installed-cli");
        Assert.Null(await routing.RequireLaunchAsync(snapshot, Ct));
    }











    [Fact]
    public async Task FixedPolicyUsesItsTarget_AndMalformedPoliciesAreRejected()
    {
        await Source("codex");
        var fixedPolicy = ReviewerRouting.SwitchDefault() with { Mode = "fixed" };
        Assert.Equal("codex", (await Resolve(fixedPolicy)).Resolution.Provider);
        Assert.False((await Resolve(fixedPolicy)).Resolution.UsedFallback);
        Assert.Throws<BoardValidationException>(() => ReviewRoutingService.Validate(fixedPolicy with { Mappings = null! }, "code_review"));
        Assert.Throws<BoardValidationException>(() => ReviewRoutingService.Validate(fixedPolicy with { Fallback = null! }, "code_review"));
        var upper = await Resolve(fixedPolicy with { Fallback = new("BASE:CODEX") });
        Assert.Equal("base:codex", upper.Resolution.Selected.Selection);
        Assert.Throws<BoardValidationException>(() => ReviewRoutingService.Validate(fixedPolicy with { Mappings = [new("openai", new("base:codex"))] }, "code_review"));
    }





    [Fact]
    public async Task RoutedProviderOptionsKeepExplicitPermissions()
    {
        var snapshot = await routing.ResolveAsync(project, card.Key, ReviewerRouting.SwitchDefault(), new("base:claude", new(Model: "sonnet[1m]")), Ct);
        var args = VibeRails.Services.LlmClis.BaseLlmOptionsBuilder.BuildArguments(LLM.Claude, snapshot.Resolution.Selected.Options);
        Assert.Contains("sonnet[1m]", args);
        Assert.DoesNotContain(args, a => a.Contains("dangerously") || a.Contains("bypass"));
    }



    [Fact]
    public async Task BeginRejectsDifferentCheckoutScopeAndChangedInputs()
    {
        var snapshot = (await Resolve()).Resolution;
        var session = await Session("codex", "code_review");
        await boards.SaveReviewAsync(project, new("frozen", card.Id, "codex", "Codex", DateTime.UtcNow, SessionId: session, Routing: snapshot), Ct);
        var reviews = new BoardReviewService(boards, new Mock<IBoardService>().Object);
        await Assert.ThrowsAsync<BoardValidationException>(() => reviews.BeginAsync(project, card.Id, session, root, "working-tree", "changes", null, null, false, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => reviews.BeginAsync(project, card.Id, session, project, "repository", "changes", null, null, false, Ct));
        await File.WriteAllTextAsync(Path.Combine(project, "file.txt"), "changed after queue", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => reviews.BeginAsync(project, card.Id, session, project, "working-tree", "changes", null, null, false, Ct));
    }

    private Task<ReviewLaunchSnapshot> Resolve(ReviewerRouting? policy = null) => routing.ResolveAsync(project, card.Key, policy ?? ReviewerRouting.SwitchDefault(), null, Ct);
    private async Task<string> Source(string cli)
    {
        var session = await Session(cli, "launch");
        await routing.SaveSettingsAsync(project, card.Id, new("session", session, "Implemented the selected changes"), Ct);
        return session;
    }
    private async Task<string> Session(string cli, string origin)
    {
        var id = Guid.NewGuid().ToString();
        await repository.CreateSessionAsync(id, cli, null, project, 1);
        await boards.LinkSessionAsync(project, card.Id, id, null, "base:" + cli, cli, "Session", origin, Ct);
        return id;
    }
    private async Task Git(params string[] args)
    {
        var result = await GitCli.RunAsync(project, args, Ct); Assert.True(result.Succeeded, result.StdErr);
    }
    [Fact]
    public async Task PresetReviewerPinsSettingsAndRejectsRetiredSelections()
    {
        var launches = new VibeRails.Data.Sqlite.LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        var service = new VibeRails.Services.Launches.LaunchService(launches, Mock.Of<IAutomationScriptService>());
        var preset = (await service.SavePresetAsync(null, new("Reviewer", "", "codex", InitialPrompt: "Review", Purpose: "code_review"), Ct)).Preset;
        routing = new(boards, repository, executables.Object, launches);
        await Source("claude");
        var policy = ReviewerRouting.SwitchDefault() with { Mappings = [new("claude", new($"preset:{preset.Id}:codex"))] };
        var snapshot = await Resolve(policy);
        Assert.Equal("Reviewer", snapshot.Resolution.Reviewer);
        Assert.Null(await routing.RequireLaunchAsync(snapshot, Ct));
        await service.RetireAsync("preset", preset.Id, preset.Revision, Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(snapshot, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => routing.PrepareAsync(project, 1, card.Key, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => Resolve(ReviewerRouting.SwitchDefault() with { Fallback = new("env:1:codex"), Mappings = [] }));
    }

    private sealed class SnapshotFactory(ReviewRoutingService service) : IReviewRunSnapshotFactory
    {
        public Task<ReviewLaunchSnapshot?> PrepareAsync(string project, int workerId, string? cardKey, CancellationToken cancellationToken,
            string defaultScope = "working-tree")
            => service.PrepareAsync(project, workerId, cardKey, cancellationToken, defaultScope);
    }
    public ValueTask DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true); return ValueTask.CompletedTask;
    }
}
