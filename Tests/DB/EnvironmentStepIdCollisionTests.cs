using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.DB;

/// <summary>
/// EnvironmentSteps.Id is the primary key for the whole table, not per environment, so an id
/// arriving from one environment's save can already belong to another's row. Before, the insert
/// hit the PK constraint and 500'd the entire save; now the colliding step is stored under a
/// fresh id so the rest of the list still lands.
/// </summary>
public sealed class EnvironmentStepIdCollisionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"viberails-step-ids-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly Repository _repository;

    public EnvironmentStepIdCollisionTests()
    {
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        // The constructor runs schema creation and migrations, so the real table shape is
        // under test rather than a hand-written approximation of it.
        _repository = new Repository(_connectionString);
    }

    [Fact]
    public async Task AStepIdOwnedByAnotherEnvironment_IsRegeneratedRatherThanFailingTheSave()
    {
        var (first, second) = await TwoEnvironmentsAsync();
        var sharedId = Guid.NewGuid().ToString();

        await _repository.ReplaceStepsAsync(
            first, [Step(sharedId, "Owned by the first environment")], TestContext.Current.CancellationToken);
        await _repository.ReplaceStepsAsync(
            second, [Step(sharedId, "Pasted into the second")], TestContext.Current.CancellationToken);

        var firstSteps = await _repository.GetStepsForEnvironmentAsync(first, TestContext.Current.CancellationToken);
        var secondSteps = await _repository.GetStepsForEnvironmentAsync(second, TestContext.Current.CancellationToken);

        // The original owner is untouched: a later save elsewhere must not renumber, move, or
        // drop a step whose {{step:<id>}} references are already written into a prompt.
        Assert.Equal(sharedId, Assert.Single(firstSteps).Id);
        Assert.Equal("Owned by the first environment", firstSteps[0].Name);

        var stored = Assert.Single(secondSteps);
        Assert.NotEqual(sharedId, stored.Id);
        Assert.True(Guid.TryParse(stored.Id, out _), $"Expected a GUID replacement, got '{stored.Id}'.");
        Assert.Equal("Pasted into the second", stored.Name);
    }

    [Fact]
    public async Task ARegeneratedStepIdIsScopedToItsOwnEnvironment()
    {
        // The point of regenerating rather than reusing: {{step:<id>}} resolution is scoped by
        // environment, so the second environment must reach its own copy and not the first's.
        var (first, second) = await TwoEnvironmentsAsync();
        var sharedId = Guid.NewGuid().ToString();

        await _repository.ReplaceStepsAsync(
            first, [Step(sharedId, "First", "echo first")], TestContext.Current.CancellationToken);
        await _repository.ReplaceStepsAsync(
            second, [Step(sharedId, "Second", "echo second")], TestContext.Current.CancellationToken);

        // The shared id still resolves for its owner and is invisible to the other environment.
        var owner = await _repository.GetStepByIdAsync(first, sharedId, TestContext.Current.CancellationToken);
        Assert.NotNull(owner);
        Assert.Equal("echo first", owner.Command);
        Assert.Null(await _repository.GetStepByIdAsync(second, sharedId, TestContext.Current.CancellationToken));

        var replacement = Assert.Single(
            await _repository.GetStepsForEnvironmentAsync(second, TestContext.Current.CancellationToken));
        var resolved = await _repository.GetStepByIdAsync(
            second, replacement.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(resolved);
        Assert.Equal("echo second", resolved.Command);
    }

    [Fact]
    public async Task OnlyTheCollidingStepIsRenumbered_TheRestOfTheListKeepsItsIds()
    {
        var (first, second) = await TwoEnvironmentsAsync();
        var sharedId = Guid.NewGuid().ToString();
        var ownId = Guid.NewGuid().ToString();

        await _repository.ReplaceStepsAsync(
            first, [Step(sharedId, "Owner")], TestContext.Current.CancellationToken);
        await _repository.ReplaceStepsAsync(
            second,
            [Step(ownId, "Keeps its id"), Step(sharedId, "Collides")],
            TestContext.Current.CancellationToken);

        var steps = await _repository.GetStepsForEnvironmentAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal(2, steps.Count);
        // Array order still decides Position, so the surviving id stays first and the rewritten
        // one keeps its slot in the list — the collision is an id concern only.
        Assert.Equal(ownId, steps[0].Id);
        Assert.Equal(0, steps[0].Position);
        Assert.Equal("Collides", steps[1].Name);
        Assert.NotEqual(sharedId, steps[1].Id);
        Assert.Equal(1, steps[1].Position);
    }

    [Fact]
    public async Task ReplacingAnEnvironmentsOwnSteps_KeepsTheirIds()
    {
        // The delete runs before the taken-id read, so an environment's own ids are free again
        // by the time they are checked. Without that ordering every save would rewrite every id
        // and break every {{step:<id>}} reference in the prompt.
        var (first, _) = await TwoEnvironmentsAsync();
        var id = Guid.NewGuid().ToString();

        await _repository.ReplaceStepsAsync(
            first, [Step(id, "Before")], TestContext.Current.CancellationToken);
        await _repository.ReplaceStepsAsync(
            first, [Step(id, "After")], TestContext.Current.CancellationToken);

        var step = Assert.Single(
            await _repository.GetStepsForEnvironmentAsync(first, TestContext.Current.CancellationToken));
        Assert.Equal(id, step.Id);
        Assert.Equal("After", step.Name);
    }

    [Fact]
    public async Task ACollisionThatDiffersOnlyInCase_IsAlsoRegenerated()
    {
        // GUIDs are compared case-insensitively here because SQLite's default TEXT collation is
        // BINARY: an upper-cased copy would insert cleanly and then be a second, silently
        // distinct id for what the user pasted as the same step.
        var (first, second) = await TwoEnvironmentsAsync();
        var sharedId = Guid.NewGuid().ToString();

        await _repository.ReplaceStepsAsync(
            first, [Step(sharedId, "Owner")], TestContext.Current.CancellationToken);
        await _repository.ReplaceStepsAsync(
            second, [Step(sharedId.ToUpperInvariant(), "Shouty copy")], TestContext.Current.CancellationToken);

        var stored = Assert.Single(
            await _repository.GetStepsForEnvironmentAsync(second, TestContext.Current.CancellationToken));
        Assert.NotEqual(sharedId, stored.Id, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABlankOrMalformedId_StillGetsAFreshGuid()
    {
        var (first, _) = await TwoEnvironmentsAsync();

        await _repository.ReplaceStepsAsync(
            first,
            [Step("", "No id at all"), Step("not-a-guid", "Hand-built request")],
            TestContext.Current.CancellationToken);

        var steps = await _repository.GetStepsForEnvironmentAsync(first, TestContext.Current.CancellationToken);

        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.True(
            Guid.TryParse(step.Id, out _), $"Expected a GUID, got '{step.Id}'."));
        Assert.NotEqual(steps[0].Id, steps[1].Id);
    }

    private async Task<(int First, int Second)> TwoEnvironmentsAsync()
    {
        var first = await _repository.SaveEnvironmentAsync(
            new LLM_Environment { CustomName = "First", LLM = LLM.Claude },
            TestContext.Current.CancellationToken);
        var second = await _repository.SaveEnvironmentAsync(
            new LLM_Environment { CustomName = "Second", LLM = LLM.Claude },
            TestContext.Current.CancellationToken);
        return (first.Id, second.Id);
    }

    private static EnvironmentStep Step(string id, string name, string command = "echo hi") => new()
    {
        Id = id,
        Name = name,
        Command = command,
        Phase = EnvironmentStepPhase.Manual,
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A still-open WAL handle on Windows; the temp directory is disposable either way.
        }
    }
}
