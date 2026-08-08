using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Workspaces;

/// <summary>
/// Workspace resolution: which directory a launch runs in, and what happens to the clones.
///
/// The git repository is real (the service shells out to <c>git rev-parse</c> before it will
/// clone anything) but <see cref="ISandboxService"/> is mocked, so these tests exercise the
/// decisions — reuse, re-create, prune, release — without paying for real clones.
/// </summary>
public sealed class RunWorkspaceServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails_workspace_{Guid.NewGuid():N}");

    private static LLM_Environment Environment(
        EnvironmentWorkspaceMode mode,
        int id = 42,
        string name = "Nightly Review") =>
        new()
        {
            Id = id,
            CustomName = name,
            LLM = LLM.Claude,
            WorkspaceMode = mode
        };

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.AddDays(-days);

    /// <summary>A run workspace named exactly as the service would have named it.</summary>
    private Sandbox RunWorkspace(LLM_Environment environment, string projectPath, int id, DateTime createdUtc) =>
        new()
        {
            Id = id,
            Name = WorkspaceNameSlug.ForRun(
                environment.CustomName,
                environment.Id,
                createdUtc,
                WorkspaceNameSlug.NewRunToken()),
            Path = Path.Combine(_testRoot, $"run-{id}"),
            ProjectPath = projectPath,
            CreatedUTC = createdUtc,
            EnvironmentId = environment.Id
        };

    /// <summary>A repository where nothing is running in any workspace.</summary>
    private static Mock<IRepository> NoOpenSessions()
    {
        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.HasOpenSessionUnderDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return repository;
    }

    /// <summary>Returns the pre-create listing once, flipping the flag for later reads.</summary>
    private static List<Sandbox> Snapshot(List<Sandbox> existing, ref bool listed)
    {
        listed = true;
        return existing;
    }

    [Fact]
    public async Task ResolveAsync_ProjectMode_PassesTheProjectDirectoryThrough()
    {
        // Strict mocks: Project mode must not touch the database or the cloner at all.
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var sandboxes = new Mock<ISandboxService>(MockBehavior.Strict);
        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);

        var resolution = await service.ResolveAsync(
            Environment(EnvironmentWorkspaceMode.Project),
            @"C:\does\not\even\need\to\exist",
            TestContext.Current.CancellationToken);

        Assert.True(resolution.Success);
        Assert.Equal(@"C:\does\not\even\need\to\exist", resolution.WorkingDirectory);
        Assert.Null(resolution.Workspace);
    }

    [Fact]
    public async Task ResolveAsync_CloneMode_FailsClearlyOutsideAGitRepository()
    {
        var projectPath = Path.Combine(_testRoot, "not-a-repo");
        Directory.CreateDirectory(projectPath);

        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var sandboxes = new Mock<ISandboxService>(MockBehavior.Strict);
        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);

        var resolution = await service.ResolveAsync(
            Environment(EnvironmentWorkspaceMode.Persistent),
            projectPath,
            TestContext.Current.CancellationToken);

        Assert.False(resolution.Success);
        Assert.Contains("not a git repository", resolution.Error, StringComparison.OrdinalIgnoreCase);
        // The failure must not silently fall back to the project directory: that would let an
        // agent write into the very tree the workspace exists to keep it out of.
        sandboxes.Verify(
            s => s.CreateSandboxAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SandboxCreateOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_CloneMode_FailsWhenTheProjectIsGone()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var sandboxes = new Mock<ISandboxService>(MockBehavior.Strict);
        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);

        var resolution = await service.ResolveAsync(
            Environment(EnvironmentWorkspaceMode.PerRun),
            Path.Combine(_testRoot, "missing"),
            TestContext.Current.CancellationToken);

        Assert.False(resolution.Success);
        Assert.Contains("no longer exists", resolution.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_PersistentMode_ReusesAnExistingWorkspace()
    {
        var projectPath = await NewGitRepositoryAsync("project");
        var workspacePath = Path.Combine(_testRoot, "existing-workspace");
        Directory.CreateDirectory(workspacePath);

        var environment = Environment(EnvironmentWorkspaceMode.Persistent);
        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(environment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Sandbox
                {
                    Id = 3,
                    Name = WorkspaceNameSlug.ForEnvironment(environment.CustomName, environment.Id),
                    Path = workspacePath,
                    ProjectPath = projectPath,
                    EnvironmentId = environment.Id
                }
            ]);
        var sandboxes = new Mock<ISandboxService>(MockBehavior.Strict);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        var resolution = await service.ResolveAsync(
            environment,
            projectPath,
            TestContext.Current.CancellationToken);

        Assert.True(resolution.Success);
        Assert.Equal(workspacePath, resolution.WorkingDirectory);
        Assert.Equal(3, resolution.Workspace?.Id);
        // Reuse means no second clone. A strict mock proves it rather than just asserting the
        // returned path, which a fresh clone could coincidentally match.
        sandboxes.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_PersistentMode_ClonesWithDirtyFilesOnFirstLaunch()
    {
        var projectPath = await NewGitRepositoryAsync("project");
        var environment = Environment(EnvironmentWorkspaceMode.Persistent);

        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(environment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        SandboxCreateOptions? captured = null;
        var sandboxes = new Mock<ISandboxService>();
        sandboxes
            .Setup(s => s.CreateSandboxAsync(
                It.IsAny<string>(), projectPath, It.IsAny<SandboxCreateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, SandboxCreateOptions?, CancellationToken>((_, _, options, _) => captured = options)
            .ReturnsAsync((string name, string project, SandboxCreateOptions? _, CancellationToken _) => new Sandbox
            {
                Id = 9,
                Name = name,
                Path = Path.Combine(_testRoot, name),
                ProjectPath = project,
                EnvironmentId = environment.Id
            });

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        var resolution = await service.ResolveAsync(
            environment,
            projectPath,
            TestContext.Current.CancellationToken);

        Assert.True(resolution.Success);
        // Carries the environment id, so two environments whose names slug alike cannot end up
        // fighting over one directory in the flat global sandboxes root.
        Assert.Equal("Nightly-Review-e42", resolution.Workspace?.Name);
        Assert.NotNull(captured);
        // A persistent workspace continues the work in progress, so it carries dirty files over
        // exactly like a hand-made sandbox does.
        Assert.True(captured.CopyDirtyFiles);
        Assert.Equal(environment.Id, captured.EnvironmentId);
    }

    [Fact]
    public async Task ResolveAsync_PerRunMode_ClonesPristineAndPrunesOlderRuns()
    {
        var projectPath = await NewGitRepositoryAsync("project");
        var environment = Environment(EnvironmentWorkspaceMode.PerRun);

        // One more run than retention allows, so exactly one is prunable. Ages are well past
        // MinimumPruneAge so the grace window is not what is being measured here.
        var existing = Enumerable
            .Range(0, RunWorkspaceService.MaxRetainedPerRunWorkspaces)
            .Select(index => RunWorkspace(environment, projectPath, 100 + index, DaysAgo(30 - index)))
            .ToList();

        // A hand-made sandbox that happens to be attached must never be pruned as a run.
        existing.Add(new Sandbox
        {
            Id = 500,
            Name = "hand-made",
            Path = Path.Combine(_testRoot, "hand-made"),
            ProjectPath = projectPath,
            CreatedUTC = DaysAgo(90),
            EnvironmentId = environment.Id
        });

        var created = RunWorkspace(environment, projectPath, 999, DateTime.UtcNow);

        var repository = NoOpenSessions();
        var listed = false;
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(environment.Id, It.IsAny<CancellationToken>()))
            // The prune pass re-reads after creating, so the new row is present by then.
            .Returns(() => Task.FromResult(listed ? [.. existing, created] : Snapshot(existing, ref listed)));

        SandboxCreateOptions? captured = null;
        var deleted = new List<int>();
        var sandboxes = new Mock<ISandboxService>();
        sandboxes
            .Setup(s => s.CreateSandboxAsync(
                It.IsAny<string>(), projectPath, It.IsAny<SandboxCreateOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, SandboxCreateOptions?, CancellationToken>((_, _, options, _) => captured = options)
            .ReturnsAsync(created);
        sandboxes
            .Setup(s => s.TryDeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((id, _) => deleted.Add(id))
            .ReturnsAsync(true);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        var resolution = await service.ResolveAsync(
            environment,
            projectPath,
            TestContext.Current.CancellationToken);

        Assert.True(resolution.Success);
        Assert.Equal(created.Path, resolution.WorkingDirectory);
        Assert.NotNull(captured);
        // "Fresh" is the whole point: the clone must not inherit the working tree's mess.
        Assert.False(captured.CopyDirtyFiles);

        Assert.DoesNotContain(created.Id, deleted);
        Assert.DoesNotContain(500, deleted);
        Assert.Equal([100], deleted);
    }

    [Fact]
    public async Task Prune_KeepsAWorkspaceThatStillHasAnOpenSession()
    {
        var projectPath = await NewGitRepositoryAsync("project");
        var environment = Environment(EnvironmentWorkspaceMode.PerRun);

        var existing = Enumerable
            .Range(0, RunWorkspaceService.MaxRetainedPerRunWorkspaces)
            .Select(index => RunWorkspace(environment, projectPath, 100 + index, DaysAgo(30 - index)))
            .ToList();
        var oldest = existing[0];
        var created = RunWorkspace(environment, projectPath, 999, DateTime.UtcNow);

        var repository = NoOpenSessions();
        // The oldest workspace — the one retention would drop — still has a live run in it.
        repository
            .Setup(r => r.HasOpenSessionUnderDirectoryAsync(oldest.Path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var listed = false;
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(environment.Id, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(listed ? [.. existing, created] : Snapshot(existing, ref listed)));

        var deleted = new List<int>();
        var sandboxes = new Mock<ISandboxService>();
        sandboxes
            .Setup(s => s.CreateSandboxAsync(
                It.IsAny<string>(), projectPath, It.IsAny<SandboxCreateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        sandboxes
            .Setup(s => s.TryDeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((id, _) => deleted.Add(id))
            .ReturnsAsync(true);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        await service.ResolveAsync(environment, projectPath, TestContext.Current.CancellationToken);

        // Retention is a disk-space policy. It is not allowed to delete the working tree of a
        // run that is still going — on Linux nothing at the filesystem level would stop it.
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task Prune_KeepsAWorkspaceYoungerThanTheGracePeriod()
    {
        var projectPath = await NewGitRepositoryAsync("project");
        var environment = Environment(EnvironmentWorkspaceMode.PerRun);

        // Every existing run was created moments ago — a burst. None has had time to register a
        // session row yet, so the open-session check alone would call them all idle.
        var existing = Enumerable
            .Range(0, RunWorkspaceService.MaxRetainedPerRunWorkspaces)
            .Select(index => RunWorkspace(environment, projectPath, 100 + index, DateTime.UtcNow.AddSeconds(-index)))
            .ToList();
        var created = RunWorkspace(environment, projectPath, 999, DateTime.UtcNow);

        var repository = NoOpenSessions();
        var listed = false;
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(environment.Id, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(listed ? [.. existing, created] : Snapshot(existing, ref listed)));

        var deleted = new List<int>();
        var sandboxes = new Mock<ISandboxService>();
        sandboxes
            .Setup(s => s.CreateSandboxAsync(
                It.IsAny<string>(), projectPath, It.IsAny<SandboxCreateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        sandboxes
            .Setup(s => s.TryDeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((id, _) => deleted.Add(id))
            .ReturnsAsync(true);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        await service.ResolveAsync(environment, projectPath, TestContext.Current.CancellationToken);

        Assert.Empty(deleted);
    }

    [Fact]
    public async Task Prune_KeepsAWorkspaceWhenTheInUseCheckFails()
    {
        var projectPath = await NewGitRepositoryAsync("project");
        var environment = Environment(EnvironmentWorkspaceMode.PerRun);

        var existing = Enumerable
            .Range(0, RunWorkspaceService.MaxRetainedPerRunWorkspaces)
            .Select(index => RunWorkspace(environment, projectPath, 100 + index, DaysAgo(30 - index)))
            .ToList();
        var created = RunWorkspace(environment, projectPath, 999, DateTime.UtcNow);

        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.HasOpenSessionUnderDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var listed = false;
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(environment.Id, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(listed ? [.. existing, created] : Snapshot(existing, ref listed)));

        var deleted = new List<int>();
        var sandboxes = new Mock<ISandboxService>();
        sandboxes
            .Setup(s => s.CreateSandboxAsync(
                It.IsAny<string>(), projectPath, It.IsAny<SandboxCreateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        sandboxes
            .Setup(s => s.TryDeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((id, _) => deleted.Add(id))
            .ReturnsAsync(true);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        var resolution = await service.ResolveAsync(environment, projectPath, TestContext.Current.CancellationToken);

        // Errs toward keeping: leaking a clone costs disk, deleting a live one costs work.
        Assert.True(resolution.Success);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task ReleaseAsync_OrphansBeforeDeleting()
    {
        var environmentId = 77;
        var order = new List<string>();

        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(environmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Sandbox { Id = 1, Name = "w1", Path = Path.Combine(_testRoot, "w1"), EnvironmentId = environmentId }
            ]);
        repository
            .Setup(r => r.OrphanSandboxesForEnvironmentAsync(environmentId, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("orphan"))
            .Returns(Task.CompletedTask);

        var sandboxes = new Mock<ISandboxService>();
        sandboxes
            .Setup(s => s.TryDeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("delete"))
            .ReturnsAsync(false);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        await service.ReleaseAsync(environmentId, TestContext.Current.CancellationToken);

        // Orphaning first is what makes a crash mid-release safe: the rows are already
        // standalone sandboxes rather than pointers to an environment that is gone.
        Assert.Equal(["orphan", "delete"], order);
    }

    [Fact]
    public async Task ReleaseAsync_SurvivesADirectoryThatWillNotDelete()
    {
        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.GetSandboxesByEnvironmentIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Sandbox { Id = 1, Name = "locked", Path = Path.Combine(_testRoot, "locked"), EnvironmentId = 5 }]);
        repository
            .Setup(r => r.OrphanSandboxesForEnvironmentAsync(5, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sandboxes = new Mock<ISandboxService>();
        sandboxes
            .Setup(s => s.TryDeleteSandboxAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);

        // A clone still locked by a running CLI is the normal case on Windows, not an error.
        await service.ReleaseAsync(5, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DetachAsync_ReleasesTheRowsWithoutDeletingAnything()
    {
        var repository = new Mock<IRepository>();
        repository
            .Setup(r => r.OrphanSandboxesForEnvironmentAsync(11, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Strict: changing a dropdown must never destroy a clone that may hold uncommitted work.
        var sandboxes = new Mock<ISandboxService>(MockBehavior.Strict);

        var service = new RunWorkspaceService(repository.Object, sandboxes.Object);
        await service.DetachAsync(11, TestContext.Current.CancellationToken);

        repository.Verify(
            r => r.OrphanSandboxesForEnvironmentAsync(11, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private async Task<string> NewGitRepositoryAsync(string name)
    {
        var path = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(path);
        await RunGitAsync(path, "init", "-b", "main");
        await RunGitAsync(path, "config", "user.email", "workspace-tests@example.invalid");
        await RunGitAsync(path, "config", "user.name", "Workspace Tests");
        await File.WriteAllTextAsync(
            Path.Combine(path, "README.md"),
            "workspace test",
            TestContext.Current.CancellationToken);
        await RunGitAsync(path, "add", "--", "README.md");
        await RunGitAsync(path, "commit", "-m", "Initial commit");
        return path;
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdErr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stdErr}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_testRoot))
            return;

        // Git marks objects read-only; clear that before recursive delete on Windows.
        foreach (var file in Directory.EnumerateFiles(_testRoot, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        try { Directory.Delete(_testRoot, recursive: true); }
        catch (IOException) { /* a leftover handle must not fail the test run */ }
    }
}
