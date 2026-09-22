using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Jobs;

/// <summary>
/// Cross-repository import (VB-31). The stores and the Automation create path are mocked; the
/// script side runs against the real <see cref="AutomationScriptService"/> and two real
/// directories, because copying files between repositories is the part with teeth.
///
/// In the ProcessEnvIsolation collection because <see cref="LlmCliEnvironmentService"/> resolves
/// the clone's directory under the process-global environments root.
/// </summary>
[Collection("ProcessEnvIsolation")]
public sealed class AutomationImportServiceTests : IDisposable
{
    private readonly string _parent = Path.Combine(
        Path.GetTempPath(), "vb-import", Guid.NewGuid().ToString("N")[..10]);
    private readonly string _sourceRoot;
    private readonly string _targetRoot;
    private readonly string _originalEnvPath = ParserConfigs.GetEnvPath();

    private readonly Mock<IJobStore> _store = new();
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IJobService> _jobService = new();
    private readonly Mock<IJobExecutableResolver> _resolver = new();
    private readonly Mock<IClaudeLlmCliEnvironment> _claudeEnvironment = new();

    private readonly List<JobDefinitionRecord> _jobs = [];
    private readonly List<LLM_Environment> _environments = [];
    private readonly Dictionary<int, List<EnvironmentStep>> _steps = [];
    private CreateJobRequest? _createdRequest;
    private LLM_Environment? _savedEnvironment;
    private IReadOnlyList<EnvironmentStep>? _replacedSteps;
    private int? _replacedStepsEnvironmentId;

    public AutomationImportServiceTests()
    {
        // Short, fixed leaf names so a suggested clone name is predictable ("… - target").
        _sourceRoot = Path.Combine(_parent, "source");
        _targetRoot = Path.Combine(_parent, "target");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_targetRoot);
        Directory.CreateDirectory(Path.Combine(_parent, "envs"));
        ParserConfigs.SetEnvPath(Path.Combine(_parent, "envs"));

        foreach (var runtime in Enum.GetValues<JobScriptRuntime>())
        {
            _resolver.Setup(candidate => candidate.Resolve(runtime))
                .Returns(new JobExecutable($"{runtime}-test", []));
        }

        _store
            .Setup(store => store.GetJobsAsync(null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _jobs.ToList());
        _store
            .Setup(store => store.GetJobAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, CancellationToken _) => _jobs.FirstOrDefault(job => job.Id == id));

        _repository
            .Setup(repository => repository.GetAllEnvironmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _environments.ToList());
        _repository
            .Setup(repository => repository.GetStepsForEnvironmentsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<int> ids, CancellationToken _) =>
                ids.Where(_steps.ContainsKey).ToDictionary(id => id, id => _steps[id].ToList()));
        _repository
            .Setup(repository => repository.GetStepsForEnvironmentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => _steps.TryGetValue(id, out var steps) ? steps.ToList() : []);
        _repository
            .Setup(repository => repository.GetProjectDisplayNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => $"{Path.GetFileName(path)} project");
        _repository
            .Setup(repository => repository.FindEnvironmentByNameIgnoreCaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => _environments.FirstOrDefault(environment =>
                string.Equals(environment.CustomName, name, StringComparison.OrdinalIgnoreCase)));
        _repository
            .Setup(repository => repository.SaveEnvironmentAsync(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLM_Environment environment, CancellationToken _) =>
            {
                environment.Id = 500;
                _savedEnvironment = environment;
                _environments.Add(environment);
                return environment;
            });
        _repository
            .Setup(repository => repository.ReplaceStepsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<EnvironmentStep>>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyList<EnvironmentStep>, CancellationToken>((id, steps, _) =>
            {
                _replacedStepsEnvironmentId = id;
                _replacedSteps = steps;
            })
            .Returns(Task.CompletedTask);

        _jobService
            .Setup(service => service.CreateJobAsync(It.IsAny<CreateJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateJobRequest request, CancellationToken _) =>
            {
                _createdRequest = request;
                return new JobResponse(
                    77, request.Name, request.ProjectPath, LLM.Claude, request.EnvironmentId, null, string.Empty,
                    request.TimeoutMinutes, request.Enabled, DateTime.UtcNow, DateTime.UtcNow, null, [],
                    request.LaunchMinimized, null);
            });

        _claudeEnvironment
            .Setup(environment => environment.SaveEnvironment(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ----- Catalog -----

    [Fact]
    public async Task GetCatalog_ExcludesTheCurrentRepositoryAndGroupsTheRestByProject()
    {
        var otherRoot = Path.Combine(_parent, "gone");
        AddEnvironment(1, "Nightly", _sourceRoot);
        _jobs.Add(Job(10, "Local one", _targetRoot, WorkerAction(10, 1)));
        _jobs.Add(Job(11, "Source B", _sourceRoot, WorkerAction(11, 1)));
        _jobs.Add(Job(12, "Source A", _sourceRoot, WorkerAction(12, 1)));
        _jobs.Add(Job(13, "Elsewhere", otherRoot, WorkerAction(13, 1)));

        // A trailing separator (and, on Windows, different casing) still means "this repository".
        var spelledDifferently = OperatingSystem.IsWindows()
            ? _targetRoot.ToUpperInvariant() + Path.DirectorySeparatorChar
            : _targetRoot + Path.DirectorySeparatorChar;
        var catalog = await Service().GetCatalogAsync(spelledDifferently, TestContext.Current.CancellationToken);

        Assert.Equal(_targetRoot, catalog.CurrentProjectPath, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(2, catalog.Projects.Count);
        Assert.DoesNotContain(catalog.Projects.SelectMany(group => group.Automations), entry => entry.SourceJobId == 10);

        var source = Assert.Single(catalog.Projects, group => group.ProjectPath == _sourceRoot);
        Assert.True(source.DirectoryExists);
        Assert.Equal("source project", source.DisplayName);
        Assert.Equal(["Source A", "Source B"], source.Automations.Select(entry => entry.Name));

        var gone = Assert.Single(catalog.Projects, group => group.ProjectPath == otherRoot);
        Assert.False(gone.DirectoryExists);
        Assert.Single(gone.Automations);

        _store.Verify(store => store.GetJobsAsync(null, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCatalog_FlagsEachScriptAgainstBothRepositoriesAndBlocksWhenItExistsInNeither()
    {
        WriteFile(_sourceRoot, "scripts/a.py", "print('a')");
        WriteFile(_targetRoot, "scripts/b.py", "print('b')");
        _jobs.Add(Job(20, "Checks", _sourceRoot,
            ScriptAction(20, 0, "scripts/a.py"),
            ScriptAction(20, 1, "scripts/b.py"),
            ScriptAction(20, 2, "scripts/c.py")));
        _jobs.Add(Job(21, "Only a", _sourceRoot, ScriptAction(21, 0, "scripts/a.py")));

        var catalog = await Service().GetCatalogAsync(_targetRoot, TestContext.Current.CancellationToken);

        var group = Assert.Single(catalog.Projects);
        var checks = Assert.Single(group.Automations, entry => entry.SourceJobId == 20);
        Assert.Null(checks.Worker);
        Assert.Equal(
            [(false, true), (true, false), (false, false)],
            checks.Actions.Select(action => (action.ExistsInTargetRepository, action.ExistsInSourceRepository)));
        Assert.False(checks.CanImport);
        Assert.Contains("scripts/c.py", checks.Blocker);
        Assert.DoesNotContain("scripts/a.py", checks.Blocker);

        var onlyA = Assert.Single(group.Automations, entry => entry.SourceJobId == 21);
        Assert.True(onlyA.CanImport);
        Assert.Null(onlyA.Blocker);
    }

    [Fact]
    public async Task GetCatalog_DescribesTheWorkerWithItsStepsAndSuggestsAUniqueCloneName()
    {
        AddEnvironment(1, "Nightly", _sourceRoot, customArgs: "--model opus", prompt: "Review {{step:s1}}",
            workspaceMode: EnvironmentWorkspaceMode.PerRun);
        _steps[1] = [Step("s1", EnvironmentStepPhase.Manual, "git log -1"), Step("s2", EnvironmentStepPhase.PreLaunch, "dotnet build")];
        // The obvious suggestion is already taken (by an unrelated project), so the next free one is offered.
        AddEnvironment(2, "nightly - TARGET", Path.Combine(_parent, "elsewhere"));
        _jobs.Add(Job(30, "Review", _sourceRoot, WorkerAction(30, 1), triggers:
        [
            new JobTriggerDto(1, JobTriggerKind.Schedule, JobScheduleKind.Daily, null, "02:00", 0, "UTC"),
            new JobTriggerDto(2, JobTriggerKind.BoardLane)
        ]));

        var catalog = await Service().GetCatalogAsync(_targetRoot, TestContext.Current.CancellationToken);

        var entry = Assert.Single(Assert.Single(catalog.Projects).Automations);
        Assert.True(entry.CanImport);
        Assert.NotNull(entry.Worker);
        var worker = entry.Worker!;
        Assert.Equal(1, worker.SourceEnvironmentId);
        Assert.Equal("Nightly", worker.Name);
        Assert.Equal(LLM.Claude, worker.Llm);
        // The same wire name GET /api/v1/environments emits; the browser lowercases on compare.
        Assert.Equal(LlmParser.ToWireName(LLM.Claude), worker.Cli);
        Assert.Equal("--model opus", worker.CustomArgs);
        Assert.Equal("Review {{step:s1}}", worker.Prompt);
        Assert.Equal((int)EnvironmentWorkspaceMode.PerRun, worker.WorkspaceMode);
        Assert.Equal(["dotnet build", "git log -1"], worker.Steps.Select(step => step.Command));
        Assert.Null(worker.ReusableEnvironmentId);
        Assert.Equal("Nightly - target 2", worker.SuggestedCloneName);

        // BoardLane triggers live in board.db lane configuration and are not portable.
        var trigger = Assert.Single(entry.Triggers);
        Assert.Equal(JobTriggerKind.Schedule, trigger.Kind);
        Assert.Equal("02:00", trigger.LocalTime);
        Assert.Equal(JobActionKind.Worker, Assert.Single(entry.Actions).Kind);
    }

    [Fact]
    public async Task GetCatalog_ReusesAWorkerVisibleHereWithTheSameNameAndCli()
    {
        AddEnvironment(1, "Nightly", _sourceRoot);
        AddEnvironment(2, "nightly", _targetRoot);
        AddEnvironment(3, "Nightly", null, llm: LLM.Codex); // legacy/global, but a different CLI
        _jobs.Add(Job(30, "Review", _sourceRoot, WorkerAction(30, 1)));

        var catalog = await Service().GetCatalogAsync(_targetRoot, TestContext.Current.CancellationToken);

        Assert.NotNull(Assert.Single(Assert.Single(catalog.Projects).Automations).Worker);

        var worker = Assert.Single(Assert.Single(catalog.Projects).Automations).Worker!;
        Assert.Equal(2, worker.ReusableEnvironmentId);
        Assert.Equal("nightly", worker.ReusableEnvironmentName);
    }

    [Fact]
    public async Task GetCatalog_BlocksAnEntryWhoseSourceWorkerIsGone()
    {
        _jobs.Add(Job(40, "Orphan", _sourceRoot, WorkerAction(40, 999)));

        var catalog = await Service().GetCatalogAsync(_targetRoot, TestContext.Current.CancellationToken);

        var entry = Assert.Single(Assert.Single(catalog.Projects).Automations);
        Assert.Null(entry.Worker);
        Assert.False(entry.CanImport);
        Assert.Equal("The source Worker no longer exists.", entry.Blocker);
    }

    [Fact]
    public async Task GetCatalog_PresentsALegacyWorkerOnlyRowAsAOneWorkerWorkflow()
    {
        AddEnvironment(1, "Nightly", _sourceRoot);
        _jobs.Add(Job(50, "Legacy", _sourceRoot) with { EnvironmentId = 1, EnvironmentName = "Nightly", Actions = null });

        var catalog = await Service().GetCatalogAsync(_targetRoot, TestContext.Current.CancellationToken);

        var entry = Assert.Single(Assert.Single(catalog.Projects).Automations);
        Assert.True(entry.CanImport);
        Assert.Equal(JobActionKind.Worker, Assert.Single(entry.Actions).Kind);
        Assert.Equal("Nightly", entry.Worker?.Name);
    }

    // ----- Import -----

    [Fact]
    public async Task Import_CopiesMissingScriptsKeepsExistingOnesAndCreatesTheAutomationDisabled()
    {
        WriteFile(_sourceRoot, "scripts/check.py", "print('source')");
        WriteFile(_sourceRoot, "tools/run.ps1", "Write-Host 'run'");
        WriteFile(_targetRoot, "scripts/check.py", "print('target keeps its own')");
        _jobs.Add(Job(60, "Checks", _sourceRoot,
            ScriptAction(60, 0, "scripts/check.py", arguments: ["--strict", "value with spaces"]),
            ScriptAction(60, 1, "tools/run.ps1", JobScriptRuntime.PowerShell, workingDirectory: "work", timeoutSeconds: 90),
            triggers:
            [
                new JobTriggerDto(1, JobTriggerKind.Commit),
                new JobTriggerDto(2, JobTriggerKind.BoardLane)
            ]) with { TimeoutMinutes = 45, LaunchMinimized = true });

        var response = await Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(60), TestContext.Current.CancellationToken);

        Assert.Equal(["tools/run.ps1"], response.CopiedScripts);
        Assert.Equal(["work"], response.CreatedDirectories);
        Assert.Equal(AutomationImportWorkerOutcome.None, response.WorkerOutcome);
        Assert.Null(response.WorkerName);
        Assert.Equal(77, response.Job.Id);
        Assert.Equal("print('target keeps its own')", await ReadFileAsync(_targetRoot, "scripts/check.py"));
        Assert.Equal("Write-Host 'run'", await ReadFileAsync(_targetRoot, "tools/run.ps1"));
        Assert.True(Directory.Exists(Path.Combine(_targetRoot, "work")));

        Assert.NotNull(_createdRequest);

        var request = _createdRequest!;
        Assert.Equal("Checks", request.Name);
        Assert.Equal(_targetRoot, request.ProjectPath);
        Assert.False(request.Enabled);
        Assert.Null(request.EnvironmentId);
        Assert.Equal(45, request.TimeoutMinutes);
        Assert.True(request.LaunchMinimized);
        Assert.Equal(JobTriggerKind.Commit, Assert.Single(request.Triggers).Kind);
        Assert.NotNull(request.Actions);
        var actions = request.Actions!;
        Assert.All(actions, action =>
        {
            Assert.Null(action.Id);
            Assert.Null(action.ApprovedHash);
            Assert.Null(action.EnvironmentId);
        });
        Assert.Equal(["scripts/check.py", "tools/run.ps1"], actions.Select(action => action.ScriptPath));
        Assert.Equal(["--strict", "value with spaces"], actions[0].Arguments);
        Assert.Equal("work", actions[1].WorkingDirectory);
        Assert.Equal(90, actions[1].TimeoutSeconds);
        Assert.Null(_savedEnvironment);
    }

    [Fact]
    public async Task Import_ClonesTheWorkerWithFreshStepIdsAndRemappedPromptTokens()
    {
        var s1 = Guid.NewGuid().ToString();
        var s2 = Guid.NewGuid().ToString();
        AddEnvironment(1, "Nightly", _sourceRoot,
            customArgs: "--model opus",
            prompt: $"Run {{{{step:{s1}}}}} then {{{{ STEP : {s1.ToUpperInvariant()} }}}} and {{{{step:{s2}}}}}; keep {{{{step:deadbeef}}}} and {{{{git_branch}}}}.",
            workspaceMode: EnvironmentWorkspaceMode.Persistent);
        _steps[1] =
        [
            Step(s1, EnvironmentStepPhase.Manual, "git log -1", name: "Last commit"),
            Step(s2, EnvironmentStepPhase.PostExit, "echo done", enabled: false, timeoutSeconds: 42, startMinimized: true)
        ];
        _jobs.Add(Job(70, "Review", _sourceRoot, WorkerAction(70, 1)));

        var response = await Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(70), TestContext.Current.CancellationToken);

        Assert.Equal(AutomationImportWorkerOutcome.Created, response.WorkerOutcome);
        Assert.Equal("Nightly - target", response.WorkerName);

        Assert.NotNull(_savedEnvironment);

        var clone = _savedEnvironment!;
        Assert.Equal("Nightly - target", clone.CustomName);
        Assert.Equal(LLM.Claude, clone.LLM);
        Assert.Equal("--model opus", clone.CustomArgs);
        Assert.True(clone.AutomationWorker);
        Assert.False(clone.Hidden);
        Assert.Equal(EnvironmentWorkspaceMode.Persistent, clone.WorkspaceMode);
        Assert.Equal(_targetRoot, clone.ProjectPath);
        _claudeEnvironment.Verify(
            environment => environment.SaveEnvironment(clone, It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(500, _replacedStepsEnvironmentId);
        Assert.NotNull(_replacedSteps);
        var steps = _replacedSteps!;
        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.True(Guid.TryParse(step.Id, out _)));
        Assert.DoesNotContain(steps, step => step.Id == s1 || step.Id == s2);
        var lastCommit = Assert.Single(steps, step => step.Command == "git log -1");
        Assert.Equal("Last commit", lastCommit.Name);
        Assert.Equal(EnvironmentStepPhase.Manual, lastCommit.Phase);
        var done = Assert.Single(steps, step => step.Command == "echo done");
        Assert.False(done.Enabled);
        Assert.Equal(42, done.TimeoutSeconds);
        Assert.True(done.StartMinimized);

        Assert.Equal(
            $"Run {{{{step:{lastCommit.Id}}}}} then {{{{step:{lastCommit.Id}}}}} and {{{{step:{done.Id}}}}}; keep {{{{step:deadbeef}}}} and {{{{git_branch}}}}.",
            clone.CustomPrompt);

        Assert.NotNull(_createdRequest);

        var request = _createdRequest!;
        Assert.Equal(500, request.EnvironmentId);
        var action = Assert.Single(request.Actions!);
        Assert.Equal(JobActionKind.Worker, action.Kind);
        Assert.Equal(500, action.EnvironmentId);
        Assert.False(request.Enabled);
    }

    [Fact]
    public async Task Import_HonoursTheRequestedCloneName()
    {
        AddEnvironment(1, "Nightly", _sourceRoot);
        _jobs.Add(Job(70, "Review", _sourceRoot, WorkerAction(70, 1)));

        var response = await Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(70, "  Review bot  "), TestContext.Current.CancellationToken);

        Assert.Equal("Review bot", response.WorkerName);
        Assert.Equal("Review bot", _savedEnvironment?.CustomName);
    }

    [Fact]
    public async Task Import_ReusesAVisibleWorkerInsteadOfCloning()
    {
        AddEnvironment(1, "Nightly", _sourceRoot);
        AddEnvironment(2, "NIGHTLY", _targetRoot);
        _jobs.Add(Job(70, "Review", _sourceRoot, WorkerAction(70, 1)));

        var response = await Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(70, "ignored when reusing"), TestContext.Current.CancellationToken);

        Assert.Equal(AutomationImportWorkerOutcome.Reused, response.WorkerOutcome);
        Assert.Equal("NIGHTLY", response.WorkerName);
        Assert.Null(_savedEnvironment);
        _claudeEnvironment.Verify(
            environment => environment.SaveEnvironment(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(2, _createdRequest?.EnvironmentId);
    }

    [Theory]
    [InlineData("bad/name", 400, "path separators")]
    [InlineData("claude", 400, "built-in CLI")]
    [InlineData("taken", 409, "already exists")]
    public async Task Import_RejectsACloneNameThatCannotBecomeAnEnvironment(string name, int status, string message)
    {
        AddEnvironment(1, "Nightly", _sourceRoot);
        AddEnvironment(2, "Taken", Path.Combine(_parent, "elsewhere"));
        _jobs.Add(Job(70, "Review", _sourceRoot, WorkerAction(70, 1)));

        var error = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(70, name), TestContext.Current.CancellationToken));

        Assert.Equal(status, error.StatusCode);
        Assert.Contains(message, error.Message);
        Assert.Null(_savedEnvironment);
        Assert.Null(_createdRequest);
    }

    [Fact]
    public async Task Import_RejectsAnAutomationThatAlreadyBelongsHereOrNoLongerExists()
    {
        AddEnvironment(1, "Nightly", _targetRoot);
        _jobs.Add(Job(80, "Local", _targetRoot, WorkerAction(80, 1)));
        _jobs.Add(Job(81, "Deleted", _sourceRoot, WorkerAction(81, 1)) with { DeletedUtc = DateTime.UtcNow });

        var local = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(80), TestContext.Current.CancellationToken));
        Assert.Equal(400, local.StatusCode);
        Assert.Contains("already belongs to this repository", local.Message);

        var deleted = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(81), TestContext.Current.CancellationToken));
        Assert.Equal(404, deleted.StatusCode);

        var unknown = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(999), TestContext.Current.CancellationToken));
        Assert.Equal(404, unknown.StatusCode);
        Assert.Null(_createdRequest);
    }

    [Fact]
    public async Task Import_RefusesWhenAScriptExistsInNeitherRepositoryBeforeWritingAnything()
    {
        WriteFile(_sourceRoot, "scripts/a.py", "print('a')");
        _jobs.Add(Job(90, "Checks", _sourceRoot,
            ScriptAction(90, 0, "scripts/a.py"),
            ScriptAction(90, 1, "scripts/missing.py")));

        var error = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(90), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("scripts/missing.py", error.Message);
        Assert.False(File.Exists(Path.Combine(_targetRoot, "scripts", "a.py")));
        Assert.Null(_createdRequest);
    }

    [Fact]
    public async Task Import_RefusesWhenTheSourceWorkerIsGoneOrTheRuntimeIsMissing()
    {
        _jobs.Add(Job(100, "Orphan", _sourceRoot, WorkerAction(100, 999)));
        var orphan = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(100), TestContext.Current.CancellationToken));
        Assert.Equal(400, orphan.StatusCode);
        Assert.Contains("source Worker no longer exists", orphan.Message);

        WriteFile(_sourceRoot, "scripts/a.sh", "echo a");
        _resolver.Setup(candidate => candidate.Resolve(JobScriptRuntime.Bash)).Returns((JobExecutable?)null);
        _jobs.Add(Job(101, "Bash", _sourceRoot, ScriptAction(101, 0, "scripts/a.sh", JobScriptRuntime.Bash)));
        var runtime = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(101), TestContext.Current.CancellationToken));
        Assert.Equal(400, runtime.StatusCode);
        Assert.Contains("Bash", runtime.Message);
        Assert.False(File.Exists(Path.Combine(_targetRoot, "scripts", "a.sh")));
    }

    [Fact]
    public async Task Import_RollsBackTheClonedWorkerWhenTheAutomationCannotBeCreated()
    {
        AddEnvironment(1, "Nightly", _sourceRoot);
        _jobs.Add(Job(110, "Review", _sourceRoot, WorkerAction(110, 1)));
        _jobService
            .Setup(service => service.CreateJobAsync(It.IsAny<CreateJobRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(JobServiceException.BadRequest("The selected LLM cannot run as an Automation."));
        _store
            .Setup(store => store.TryDeleteEnvironmentIfUnusedAsync(500, It.IsAny<Action>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var error = await Assert.ThrowsAsync<JobServiceException>(() => Service().ImportAsync(
            _targetRoot, new AutomationImportRequest(110), TestContext.Current.CancellationToken));

        // The caller sees the real reason, and the half-made Worker was handed to the guarded delete.
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("cannot run as an Automation", error.Message);
        Assert.Equal(500, _savedEnvironment?.Id);
        _store.Verify(
            store => store.TryDeleteEnvironmentIfUnusedAsync(500, It.IsAny<Action>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ----- Pure helpers -----

    [Theory]
    [InlineData("Nightly", "target", "", "Nightly - target")]
    [InlineData("Nightly", "target", "nightly - target", "Nightly - target 2")]
    [InlineData("Nightly (v2)", "vibe.rails", "", "Nightly v2 - vibe rails")]
    [InlineData("  --weird  ", "C:\\", "", "weird - C")]
    [InlineData("...", "", "", "Worker")]
    public void SuggestCloneName_ProducesAValidUniqueEnvironmentName(
        string source, string repository, string existing, string expected)
    {
        var taken = existing.Length == 0 ? [] : new[] { existing };

        var suggestion = AutomationImportService.SuggestCloneName(source, repository, taken);

        Assert.Equal(expected, suggestion);
        Assert.Null(EnvironmentNameValidator.Validate(suggestion));
    }

    [Fact]
    public void SuggestCloneName_StaysWithinTheLengthLimitEvenWhenDeduplicating()
    {
        var longName = new string('n', 70);
        var first = AutomationImportService.SuggestCloneName(longName, "repo", []);
        var second = AutomationImportService.SuggestCloneName(longName, "repo", [first]);

        Assert.Equal(64, first.Length);
        Assert.True(second.Length <= 64, second);
        Assert.EndsWith(" 2", second);
        Assert.Null(EnvironmentNameValidator.Validate(first));
        Assert.Null(EnvironmentNameValidator.Validate(second));
    }

    [Fact]
    public void RemapStepPlaceholders_RewritesOnlyKnownStepIds()
    {
        var oldId = "0F3D2C1B-AAAA-BBBB-CCCC-123456789ABC";
        var newId = Guid.NewGuid().ToString();
        var map = new Dictionary<string, string> { [Guid.Parse(oldId).ToString()] = newId };

        var remapped = AutomationImportService.RemapStepPlaceholders(
            $"a {{{{step:{oldId}}}}} b {{{{ step : {oldId.ToLowerInvariant()} }}}} c {{{{step:not-a-guid}}}} d {{{{date}}}}",
            map);

        Assert.Equal($"a {{{{step:{newId}}}}} b {{{{step:{newId}}}}} c {{{{step:not-a-guid}}}} d {{{{date}}}}", remapped);
        Assert.Equal("unchanged", AutomationImportService.RemapStepPlaceholders("unchanged", map));
        Assert.Equal("{{step:x}}", AutomationImportService.RemapStepPlaceholders("{{step:x}}", new Dictionary<string, string>()));
    }

    // ----- Fixtures -----

    private AutomationImportService Service() => new(
        _store.Object,
        _repository.Object,
        _jobService.Object,
        new AutomationScriptService(_resolver.Object),
        new LlmCliEnvironmentService(
            _claudeEnvironment.Object,
            Mock.Of<ICodexLlmCliEnvironment>(),
            Mock.Of<IAntigravityLlmCliEnvironment>(),
            Mock.Of<ICopilotLlmCliEnvironment>(),
            Mock.Of<IOpencodeLlmCliEnvironment>(),
            Mock.Of<IGrokLlmCliEnvironment>(),
            Mock.Of<IFileService>()),
        new LlmParser());

    private void AddEnvironment(
        int id,
        string name,
        string? projectPath,
        LLM llm = LLM.Claude,
        string customArgs = "",
        string prompt = "run the nightly review",
        EnvironmentWorkspaceMode workspaceMode = EnvironmentWorkspaceMode.Project) =>
        _environments.Add(new LLM_Environment
        {
            Id = id,
            LLM = llm,
            CustomName = name,
            CustomArgs = customArgs,
            CustomPrompt = prompt,
            AutomationWorker = true,
            WorkspaceMode = workspaceMode,
            ProjectPath = projectPath
        });

    private static EnvironmentStep Step(
        string id,
        EnvironmentStepPhase phase,
        string command,
        string name = "",
        bool enabled = true,
        int timeoutSeconds = 600,
        bool startMinimized = false) => new()
    {
        Id = id,
        EnvironmentId = 1,
        Phase = phase,
        Position = 0,
        Name = name,
        Command = command,
        Enabled = enabled,
        TimeoutSeconds = timeoutSeconds,
        StartMinimized = startMinimized
    };

    private static JobDefinitionRecord Job(
        long id,
        string name,
        string projectPath,
        params JobActionRecord[] actions) => Job(id, name, projectPath, actions, triggers: null);

    private static JobDefinitionRecord Job(
        long id,
        string name,
        string projectPath,
        JobActionRecord[] actions,
        List<JobTriggerDto>? triggers) => new(
        id, name, projectPath, LLM.NotSet, null, null, string.Empty, 30, true,
        DateTime.UtcNow, DateTime.UtcNow, null, triggers ?? [], false, actions);

    private static JobDefinitionRecord Job(
        long id,
        string name,
        string projectPath,
        JobActionRecord action,
        List<JobTriggerDto> triggers) => Job(id, name, projectPath, [action], triggers);

    private static JobDefinitionRecord Job(
        long id,
        string name,
        string projectPath,
        JobActionRecord first,
        JobActionRecord second,
        List<JobTriggerDto> triggers) => Job(id, name, projectPath, [first, second], triggers);

    private static JobActionRecord WorkerAction(long jobId, int environmentId) => new(
        Guid.NewGuid().ToString(), jobId, 0, JobActionKind.Worker, environmentId, "Nightly", LLM.Claude,
        null, null, [], null, null, null);

    private static JobActionRecord ScriptAction(
        long jobId,
        int position,
        string scriptPath,
        JobScriptRuntime runtime = JobScriptRuntime.Python,
        List<string>? arguments = null,
        string? workingDirectory = null,
        int? timeoutSeconds = null) => new(
        Guid.NewGuid().ToString(), jobId, position, JobActionKind.Script, null, null, LLM.NotSet,
        scriptPath, runtime, arguments ?? [], workingDirectory, timeoutSeconds, new string('a', 64));

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static Task<string> ReadFileAsync(string root, string relativePath) =>
        File.ReadAllTextAsync(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            TestContext.Current.CancellationToken);

    public void Dispose()
    {
        ParserConfigs.SetEnvPath(_originalEnvPath);
        try { Directory.Delete(_parent, recursive: true); }
        catch { /* a leftover temp tree is not worth failing a test run over */ }
    }
}
