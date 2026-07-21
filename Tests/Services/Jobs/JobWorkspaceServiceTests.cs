using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobWorkspaceServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"job_workspace_{Guid.NewGuid():N}");
    private string _repository = null!;
    private string _artifacts = null!;

    public async ValueTask InitializeAsync()
    {
        _repository = Path.Combine(_root, "repository");
        _artifacts = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(_repository);
        await GitAsync("init");
        await GitAsync("config", "user.email", "tests@viberails.local");
        await GitAsync("config", "user.name", "VibeRails Tests");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class Original { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", ".");
        await GitAsync("commit", "-m", "initial");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RequiredSnapshot_UsesValidatedTree_WhenIndexChangesBeforeQueuePublication()
    {
        var service = new JobWorkspaceService(_artifacts);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class StagedSnapshot { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(_repository, "asset.bin"),
            [0, 1, 2, 3],
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs", "asset.bin");

        var validatedSnapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class LaterIndex { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(_repository, "asset.bin"),
            [9, 8, 7],
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs", "asset.bin");

        using var capture = await service.CaptureStagedSnapshotAsync(
            validatedSnapshot,
            TestContext.Current.CancellationToken);

        var published = Assert.Single(Directory.EnumerateFiles(_artifacts, "*.vca-snapshot.patch"));
        Assert.True(new FileInfo(published).Length > 0);
        Assert.Empty(Directory.EnumerateFiles(_artifacts, "*.tmp"));

        var runId = Guid.NewGuid().ToString("N");
        await service.MaterializeRunSnapshotsAsync(
            capture,
            [runId],
            TestContext.Current.CancellationToken);
        Assert.Empty(Directory.EnumerateFiles(_artifacts, "*.vca-snapshot.patch"));
        Assert.Single(Directory.EnumerateFiles(_artifacts, "*.staged.patch"));

        // Commit the later index and advance HEAD again. A Review run must still clone, check out
        // the captured base, and apply the tree that VCA validated before either mutation.
        await GitAsync("commit", "-m", "commit later index");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class LaterHead { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        await GitAsync("commit", "-m", "later head");

        var run = Run(runId, JobExecutionMode.Review, Context(capture));
        var workspace = await service.PrepareAsync(run, TestContext.Current.CancellationToken);
        try
        {
            Assert.NotEqual(Path.GetFullPath(_repository), Path.GetFullPath(workspace));
            var workspaceContent = await File.ReadAllTextAsync(
                Path.Combine(workspace, "tracked.cs"),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "class StagedSnapshot { }\n",
                workspaceContent.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal(
                [0, 1, 2, 3],
                await File.ReadAllBytesAsync(
                    Path.Combine(workspace, "asset.bin"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            service.Cleanup(run, workspace);
        }

        Assert.False(Directory.Exists(workspace));
        Assert.Empty(Directory.EnumerateFiles(_artifacts, "*.staged.patch"));
    }

    [Fact]
    public async Task RequiredSnapshot_MissingArtifactFailsClosedAndRemovesPartialClone()
    {
        var service = new JobWorkspaceService(_artifacts);
        using var capture = await CaptureAsync(service);
        service.DiscardCapturedSnapshot(capture);
        var run = Run(Guid.NewGuid().ToString("N"), JobExecutionMode.LiveWrite, Context(capture));

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.PrepareAsync(run, TestContext.Current.CancellationToken));

        Assert.Contains("snapshot is missing", exception.Message);
        Assert.Empty(Directory.Exists(_artifacts)
            ? Directory.EnumerateDirectories(_artifacts)
            : []);
    }

    [Fact]
    public async Task RequiredSnapshot_IsRunnableFromSharedCaptureBeforePerRunFanOut()
    {
        var service = new JobWorkspaceService(_artifacts);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class QueueVisibleSnapshot { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        using var capture = await CaptureAsync(service);
        var run = Run(Guid.NewGuid().ToString("N"), JobExecutionMode.Review, Context(capture));

        // This is the exact window after the Queued row commits but before the enqueue process has
        // fanned the shared capture out to per-run files.
        var workspace = await service.PrepareAsync(run, TestContext.Current.CancellationToken);
        try
        {
            var content = await File.ReadAllTextAsync(
                Path.Combine(workspace, "tracked.cs"),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "class QueueVisibleSnapshot { }\n",
                content.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            service.Cleanup(run, workspace);
            service.DiscardCapturedSnapshot(capture);
        }
    }

    [Fact]
    public async Task VcaRunWithoutSnapshotMetadataFailsClosedBeforeUsingMutableProject()
    {
        var service = new JobWorkspaceService(_artifacts);
        var run = Run(Guid.NewGuid().ToString("N"), JobExecutionMode.Review, contextJson: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(run, TestContext.Current.CancellationToken));

        Assert.Contains("no staged snapshot metadata", exception.Message);
        Assert.False(Directory.Exists(_artifacts));
    }

    [Fact]
    public async Task InitialCommitSnapshot_MaterializesFromEmptyBaseTree()
    {
        var unbornRepository = Path.Combine(_root, "unborn-repository");
        Directory.CreateDirectory(unbornRepository);
        await GitInAsync(unbornRepository, "init");
        await GitInAsync(unbornRepository, "config", "user.email", "tests@viberails.local");
        await GitInAsync(unbornRepository, "config", "user.name", "VibeRails Tests");
        await File.WriteAllTextAsync(
            Path.Combine(unbornRepository, "first.cs"),
            "class FirstCommit { }\n",
            TestContext.Current.CancellationToken);
        await GitInAsync(unbornRepository, "add", "first.cs");

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            unbornRepository,
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot.StagedIdentity);
        Assert.Null(snapshot.StagedIdentity.BaseCommit);

        var service = new JobWorkspaceService(_artifacts);
        using var capture = await service.CaptureStagedSnapshotAsync(
            snapshot,
            TestContext.Current.CancellationToken);
        var runId = Guid.NewGuid().ToString("N");
        await service.MaterializeRunSnapshotsAsync(capture, [runId], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(unbornRepository, "first.cs"),
            "class LaterInitialCommit { }\n",
            TestContext.Current.CancellationToken);
        await GitInAsync(unbornRepository, "add", "first.cs");
        await GitInAsync(unbornRepository, "commit", "-m", "later initial commit");
        var run = Run(runId, JobExecutionMode.Review, Context(capture)) with
        {
            ProjectPath = unbornRepository
        };

        var workspace = await service.PrepareAsync(run, TestContext.Current.CancellationToken);
        try
        {
            var content = await File.ReadAllTextAsync(
                Path.Combine(workspace, "first.cs"),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "class FirstCommit { }\n",
                content.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            service.Cleanup(run, workspace);
        }
    }

    [Fact]
    public void Cleanup_MalformedLegacyContextCannotMaskCleanup()
    {
        var service = new JobWorkspaceService(_artifacts);
        var workspace = Path.Combine(_artifacts, "partial-clone");
        Directory.CreateDirectory(workspace);
        var run = Run("legacy-run", JobExecutionMode.Review, "{");

        var exception = Record.Exception(() => service.Cleanup(run, workspace));

        Assert.Null(exception);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public async Task SweepSnapshotArtifacts_PreservesOnlyDatabaseReferencedArtifacts()
    {
        var service = new JobWorkspaceService(_artifacts);
        var sharedCapture = await CaptureAsync(service);
        var perRunCapture = await CaptureAsync(service);
        var activeRunId = Guid.NewGuid().ToString("N");
        await service.MaterializeRunSnapshotsAsync(
            perRunCapture,
            [activeRunId],
            TestContext.Current.CancellationToken);
        sharedCapture.Dispose();
        perRunCapture.Dispose();

        using var sweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
            service.TryAcquireArtifactSweepLease());
        service.SweepSnapshotArtifacts(
            sweepLease,
            new JobSnapshotArtifactReferences(
                new HashSet<string>([activeRunId], StringComparer.Ordinal),
                new HashSet<string>([sharedCapture.Id], StringComparer.Ordinal)));

        Assert.Single(Directory.EnumerateFiles(_artifacts, "*.vca-snapshot.patch"));
        Assert.Single(Directory.EnumerateFiles(_artifacts, "*.staged.patch"));

        sweepLease.Dispose();
        using var finalSweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
            service.TryAcquireArtifactSweepLease());
        service.SweepSnapshotArtifacts(
            finalSweepLease,
            new JobSnapshotArtifactReferences(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));
        Assert.Empty(Directory.EnumerateFiles(_artifacts, "*.vca-snapshot.patch"));
        Assert.Empty(Directory.EnumerateFiles(_artifacts, "*.staged.patch"));
    }

    [Fact]
    public async Task ArtifactSweepLease_IsUnavailableUntilPublicationCompletes()
    {
        var service = new JobWorkspaceService(_artifacts);
        using var capture = await CaptureAsync(service);

        Assert.Null(service.TryAcquireArtifactSweepLease());

        capture.Dispose();
        using var sweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
            service.TryAcquireArtifactSweepLease());
    }

    [Fact]
    public void SweepSnapshotArtifacts_GivesCrashedPublicationMarkerGracePeriod()
    {
        var service = new JobWorkspaceService(_artifacts);
        Directory.CreateDirectory(_artifacts);
        var snapshotId = Guid.NewGuid().ToString("N");
        var marker = Path.Combine(_artifacts, $"{snapshotId}.vca-snapshot.publishing");
        var patch = Path.Combine(_artifacts, $"{snapshotId}.vca-snapshot.patch");
        File.WriteAllText(marker, string.Empty);
        File.WriteAllText(patch, "snapshot");
        var noReferences = new JobSnapshotArtifactReferences(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        using (var sweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
                   service.TryAcquireArtifactSweepLease()))
        {
            service.SweepSnapshotArtifacts(sweepLease, noReferences);
        }

        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(patch));

        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(-3));
        using var staleSweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
            service.TryAcquireArtifactSweepLease());
        service.SweepSnapshotArtifacts(staleSweepLease, noReferences);

        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(patch));
    }

    [Fact]
    public async Task SnapshotArtifacts_ArePrivateOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var service = new JobWorkspaceService(_artifacts);
        using var capture = await CaptureAsync(service);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_artifacts));
        foreach (var artifact in Directory.EnumerateFiles(_artifacts))
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(artifact));
        }

        var runId = Guid.NewGuid().ToString("N");
        await service.MaterializeRunSnapshotsAsync(
            capture,
            [runId],
            TestContext.Current.CancellationToken);
        var runPatch = Assert.Single(Directory.EnumerateFiles(_artifacts, "*.staged.patch"));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(runPatch));
    }

    [Fact]
    public void ExistingSnapshotArtifactPermissions_AreTightenedOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_artifacts);
        File.SetUnixFileMode(
            _artifacts,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var snapshotId = Guid.NewGuid().ToString("N");
        string[] existingArtifacts =
        [
            Path.Combine(_artifacts, ".snapshot-publication.lock"),
            Path.Combine(_artifacts, $"{snapshotId}.vca-snapshot.patch"),
            Path.Combine(_artifacts, $"{snapshotId}.vca-snapshot.publishing"),
            Path.Combine(_artifacts, $"{snapshotId}.vca-snapshot.tmp")
        ];
        foreach (var artifact in existingArtifacts)
        {
            File.WriteAllText(artifact, string.Empty);
            File.SetUnixFileMode(
                artifact,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }

        var service = new JobWorkspaceService(_artifacts);
        using var sweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
            service.TryAcquireArtifactSweepLease());

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_artifacts));
        foreach (var artifact in existingArtifacts)
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(artifact));
        }
    }

    [Fact]
    public void ExistingArtifactPermissionNormalization_DoesNotFollowSymlinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_artifacts);
        var external = Path.Combine(_root, "external.txt");
        File.WriteAllText(external, "outside");
        var externalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(external, externalMode);
        File.CreateSymbolicLink(
            Path.Combine(_artifacts, $"{Guid.NewGuid():N}.vca-snapshot.patch"),
            external);

        var service = new JobWorkspaceService(_artifacts);
        using var sweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
            service.TryAcquireArtifactSweepLease());

        Assert.Equal(externalMode, File.GetUnixFileMode(external));
    }

    [Fact]
    public void JobsWorkspaceRoot_RejectsDirectSymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        var actualRoot = Path.Combine(_root, "actual-artifacts");
        Directory.CreateDirectory(actualRoot);
        var linkedRoot = Path.Combine(_root, "linked-artifacts");
        Directory.CreateSymbolicLink(linkedRoot, actualRoot);
        var service = new JobWorkspaceService(linkedRoot);

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.TryAcquireArtifactSweepLease());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredSnapshot_RejectsSymlinkedPerRunPatch()
    {
        if (OperatingSystem.IsWindows())
            return;

        var service = new JobWorkspaceService(_artifacts);
        using var capture = await CaptureAsync(service);
        var runId = Guid.NewGuid().ToString("N");
        var run = Run(runId, JobExecutionMode.Review, Context(capture));
        service.DiscardCapturedSnapshot(capture);
        capture.Dispose();
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        var runPatch = Path.Combine(_artifacts, $"{digest}.staged.patch");
        var externalPatch = Path.Combine(_root, "external.patch");
        await File.WriteAllTextAsync(
            externalPatch,
            string.Empty,
            TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(runPatch, externalPatch);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(run, TestContext.Current.CancellationToken));

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(_artifacts));
    }

    private JobRunRecord Run(string id, JobExecutionMode mode, string? contextJson) => new(
        Id: id,
        JobId: 1,
        TriggerId: 1,
        TriggerKind: JobTriggerKind.Vca,
        TriggerKey: "vca:test",
        Status: JobRunStatus.Queued,
        JobName: "Test",
        ProjectPath: _repository,
        Llm: LLM.Codex,
        EnvironmentId: null,
        EnvironmentName: null,
        EnvironmentPath: null,
        EnvironmentArgs: "",
        Prompt: "Review",
        ExecutionMode: mode,
        TimeoutMinutes: 5,
        ExecutablePath: null,
        TriggerContextJson: contextJson,
        QueuedUtc: DateTime.UtcNow,
        StartedUtc: null,
        EndedUtc: null,
        ExitCode: null,
        ResultText: null,
        ErrorMessage: null,
        WorkspacePath: null,
        CancelRequested: false,
        OwnerInstanceId: null,
        OwnerProcessId: null);

    private static string Context(JobWorkspaceService.CapturedStagedSnapshot capture)
    {
        var context = new Dictionary<string, string>
        {
            [JobWorkspaceService.StagedSnapshotIdContextKey] = capture.Id,
            [JobWorkspaceService.StagedSnapshotBaseTreeContextKey] = capture.BaseTree,
            [JobWorkspaceService.StagedSnapshotTreeContextKey] = capture.StagedTree
        };
        if (capture.BaseCommit is not null)
            context[JobWorkspaceService.StagedSnapshotBaseCommitContextKey] = capture.BaseCommit;
        return JsonSerializer.Serialize(context);
    }

    private async Task<JobWorkspaceService.CapturedStagedSnapshot> CaptureAsync(
        JobWorkspaceService service)
    {
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);
        return await service.CaptureStagedSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
    }

    private Task GitAsync(params string[] arguments) => GitInAsync(_repository, arguments);

    private static async Task GitInAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {output}\n{error}");
    }
}
