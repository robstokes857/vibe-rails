using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.GitPreflight;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public sealed record JobSnapshotArtifactReferences(
    IReadOnlySet<string> ActiveRunIds,
    IReadOnlySet<string> ActiveSnapshotIds);

public sealed class JobWorkspaceService
{
    internal const string StagedSnapshotIdContextKey = "stagedSnapshotId";
    internal const string StagedSnapshotBaseCommitContextKey = "stagedSnapshotBaseCommit";
    internal const string StagedSnapshotBaseTreeContextKey = "stagedSnapshotBaseTree";
    internal const string StagedSnapshotTreeContextKey = "stagedSnapshotTree";

    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PublicationMarkerGrace = TimeSpan.FromMinutes(2);
    private const string PublicationLockFileName = ".snapshot-publication.lock";
    private const string PublishingMarkerSuffix = ".vca-snapshot.publishing";
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string? _workspaceRoot;

    public JobWorkspaceService()
    {
    }

    internal JobWorkspaceService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public async Task<string> PrepareAsync(JobRunRecord run, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(run.ProjectPath))
            throw new DirectoryNotFoundException($"Project directory no longer exists: {run.ProjectPath}");

        var snapshotRequirement = ReadSnapshotRequirement(run.TriggerContextJson);
        if (run.TriggerKind == JobTriggerKind.Vca && snapshotRequirement is null)
            throw new InvalidOperationException(
                "The VCA run has no staged snapshot metadata; refusing to run against mutable project state.");
        if (snapshotRequirement is null
            && run.ExecutionMode is JobExecutionMode.Review or JobExecutionMode.LiveWrite)
            return run.ProjectPath;

        // The clone separates ordinary working-tree changes for convenience; it is not an OS or
        // process sandbox. The unattended CLI still runs as the VibeRails user and may access paths
        // outside this workspace through arguments, configuration, MCP servers, tools, or scripts.
        var root = EnsureWorkspaceRoot();
        var workspace = Path.GetFullPath(Path.Combine(root, run.Id));
        var normalizedRoot = Path.GetFullPath(root + Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!workspace.StartsWith(normalizedRoot, comparison))
            throw new InvalidOperationException("Refusing to create an isolated job workspace outside the Jobs workspace root.");
        if (Directory.Exists(workspace) || File.Exists(workspace))
            throw new InvalidOperationException($"The isolated workspace already exists: {workspace}");

        try
        {
            var clone = await GitProcessRunner.RunAsync(
                ["clone", "--no-local", "--quiet", "--", run.ProjectPath, workspace],
                root,
                CloneTimeout,
                cancellationToken);
            if (clone.TimedOut)
                throw new TimeoutException("Creating the isolated Git workspace timed out.");
            if (clone.ExitCode != 0)
                throw new InvalidOperationException($"Could not create the isolated Git workspace: {clone.StdErr}");

            await ApplyStagedSnapshotAsync(run, workspace, snapshotRequirement, cancellationToken);
            return workspace;
        }
        catch
        {
            TryDeleteWorkspace(workspace);
            throw;
        }
    }

    /// <summary>
    /// Captures the already-frozen staged tree in <paramref name="snapshot"/> as a patch
    /// before a queue record can reference it. The patch is written to a private temporary file and
    /// atomically renamed into visibility only after Git exits successfully.
    /// </summary>
    public async Task<CapturedStagedSnapshot> CaptureStagedSnapshotAsync(
        GitStagedSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var identity = snapshot.StagedIdentity
            ?? throw new InvalidOperationException(
                "The validated staged snapshot has no immutable Git identity; refusing to queue VCA Jobs.");
        return await CaptureStagedSnapshotAsync(
            snapshot.RepositoryPath,
            identity.BaseCommit,
            identity.BaseTree,
            identity.StagedTree,
            cancellationToken);
    }

    /// <summary>
    /// Recreates a terminal run's staged snapshot from the immutable Git object ids stored with the
    /// original run. A null result means the run was not tied to a staged snapshot.
    /// </summary>
    public async Task<CapturedStagedSnapshot?> CaptureRetrySnapshotAsync(
        JobRunRecord source,
        CancellationToken cancellationToken)
    {
        var requirement = ReadSnapshotRequirement(source.TriggerContextJson);
        if (requirement is null)
            return null;

        return await CaptureStagedSnapshotAsync(
            source.ProjectPath,
            requirement.BaseCommit,
            requirement.BaseTree,
            requirement.StagedTree,
            cancellationToken);
    }

    public static string CreateRetryContextJson(
        string? sourceContextJson,
        CapturedStagedSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(sourceContextJson))
            throw new InvalidOperationException("The original staged snapshot context is missing.");

        using var document = JsonDocument.Parse(sourceContextJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The original staged snapshot context is invalid.");

        var context = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("The original staged snapshot context is invalid.");
            context[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        context[StagedSnapshotIdContextKey] = snapshot.Id;
        context[StagedSnapshotBaseTreeContextKey] = snapshot.BaseTree;
        context[StagedSnapshotTreeContextKey] = snapshot.StagedTree;
        if (snapshot.BaseCommit is null)
            context.Remove(StagedSnapshotBaseCommitContextKey);
        else
            context[StagedSnapshotBaseCommitContextKey] = snapshot.BaseCommit;

        return JsonSerializer.Serialize(
            context,
            AppJsonSerializerContext.Default.DictionaryStringString);
    }

    private async Task<CapturedStagedSnapshot> CaptureStagedSnapshotAsync(
        string repositoryPath,
        string? baseCommit,
        string baseTree,
        string stagedTree,
        CancellationToken cancellationToken)
    {
        if ((baseCommit is not null && !IsObjectId(baseCommit))
            || !IsObjectId(baseTree)
            || !IsObjectId(stagedTree))
        {
            throw new InvalidOperationException("The validated staged snapshot Git identity is invalid.");
        }

        EnsureWorkspaceRoot();
        var publicationLease = await AcquirePublicationLeaseAsync(cancellationToken);
        var snapshotId = Guid.NewGuid().ToString("N");
        var finalPath = CapturedSnapshotPath(snapshotId);
        var temporaryPath = ResolveArtifactPath($"{snapshotId}.{Guid.NewGuid():N}.vca-snapshot.tmp");
        var markerPath = PublishingMarkerPath(snapshotId);
        try
        {
            CreatePrivateEmptyFile(markerPath);
            CreatePrivateEmptyFile(temporaryPath);
            // Write the diff straight to a file so binary / odd-encoding content is captured
            // byte-exact (GitProcessRunner returns stdout as a trimmed string, which would corrupt
            // a patch). Both operands are the same immutable trees already read by validation.
            var diff = await GitProcessRunner.RunAsync(
                [
                    "diff", "--binary", "--full-index", $"--output={temporaryPath}",
                    baseTree, stagedTree, "--"
                ],
                repositoryPath,
                GitTimeout,
                cancellationToken);
            if (diff.TimedOut)
                throw new TimeoutException("Capturing the staged snapshot patch timed out.");
            if (diff.ExitCode != 0 || !File.Exists(temporaryPath))
                throw new InvalidOperationException(
                    $"Could not capture the staged snapshot patch: {diff.StdErr.Trim()}");

            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, finalPath);
            return new CapturedStagedSnapshot(
                snapshotId,
                baseCommit,
                baseTree,
                stagedTree,
                markerPath,
                publicationLease);
        }
        catch
        {
            TryDeleteFile(finalPath);
            TryDeleteFile(markerPath);
            publicationLease.Dispose();
            throw;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    /// <summary>
    /// Creates an atomic per-run copy after enqueue. Workers may safely use the already-published
    /// captured copy during this fan-out; the shared file is removed only when every run copy exists.
    /// </summary>
    public async Task MaterializeRunSnapshotsAsync(
        CapturedStagedSnapshot snapshot,
        IReadOnlyList<string> runIds,
        CancellationToken cancellationToken)
    {
        var source = CapturedSnapshotPath(snapshot.Id);
        if (!File.Exists(source))
            throw new FileNotFoundException("The captured staged snapshot is missing.", source);

        var allCopied = true;
        foreach (var runId in runIds)
        {
            try
            {
                await CopyAtomicallyAsync(source, StagedPatchPath(runId), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                allCopied = false;
                Log.Warning(exception, "[Jobs] Could not copy the staged snapshot for run {RunId}", runId);
            }
        }

        if (allCopied)
            TryDeleteFile(source);
    }

    public void DiscardCapturedSnapshot(CapturedStagedSnapshot snapshot) =>
        TryDeleteFile(CapturedSnapshotPath(snapshot.Id));

    /// <summary>Deletes a run's staged patch and any throwaway clone.</summary>
    public void Cleanup(JobRunRecord run, string? workspace)
    {
        // The staged patch is written for every enqueued VCA run; remove it on any exit path.
        TryDeleteFile(StagedPatchPath(run.Id));

        if (string.IsNullOrEmpty(workspace))
            return;
        // Containment inside TryDeleteWorkspace is the deletion authority. This remains non-throwing
        // even when legacy/malformed trigger JSON caused preparation to fail, and it can never delete
        // the real project path returned by an ordinary Review/LiveWrite run.
        TryDeleteWorkspace(workspace);
    }

    /// <summary>
    /// Deletes leftover isolated clones. This is safe only for the lease-holding worker at startup,
    /// after stale Running rows have been interrupted; it must not run periodically.
    /// </summary>
    public void SweepOrphanWorkspaces()
    {
        var root = WorkspaceRoot();
        if (!Directory.Exists(root))
            return;
        foreach (var directory in Directory.EnumerateDirectories(root))
            TryDeleteWorkspace(directory);
    }

    /// <summary>
    /// Tries to exclude every snapshot publisher. The caller must hold the returned lease while it
    /// queries active database references and calls <see cref="SweepSnapshotArtifacts"/>.
    /// </summary>
    public ArtifactSweepLease? TryAcquireArtifactSweepLease()
    {
        var root = EnsureWorkspaceRoot();
        EnsurePublicationLockFile();
        try
        {
            var stream = new FileStream(
                PublicationLockPath(),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            return new ArtifactSweepLease(root, stream);
        }
        catch (IOException)
        {
            // A publisher owns a shared handle from before patch visibility until queue/fan-out
            // completion. Skipping this pass is safer than deleting a not-yet-referenced patch.
            return null;
        }
    }

    /// <summary>
    /// Deletes only snapshot artifacts that no Queued or Running row references. The exclusive
    /// publication lease closes the gap between this database snapshot and filesystem deletion.
    /// </summary>
    public void SweepSnapshotArtifacts(
        ArtifactSweepLease sweepLease,
        JobSnapshotArtifactReferences references)
    {
        ArgumentNullException.ThrowIfNull(sweepLease);
        ArgumentNullException.ThrowIfNull(references);
        var root = EnsureWorkspaceRoot();
        sweepLease.AssertOwns(root);

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var activeRunPatches = references.ActiveRunIds
            .Select(StagedPatchPath)
            .ToHashSet(pathComparer);
        var activeSnapshotIds = references.ActiveSnapshotIds
            .Where(id => Guid.TryParseExact(id, "N", out _))
            .Select(id => Guid.ParseExact(id, "N").ToString("N"))
            .ToHashSet(StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var recentPublishingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in Directory.EnumerateFiles(root, $"*{PublishingMarkerSuffix}"))
        {
            var fileName = Path.GetFileName(marker);
            var id = fileName[..^PublishingMarkerSuffix.Length];
            var isRecent = File.GetLastWriteTimeUtc(marker) >= now - PublicationMarkerGrace;
            if (Guid.TryParseExact(id, "N", out var parsedId) && isRecent)
            {
                recentPublishingIds.Add(parsedId.ToString("N"));
                continue;
            }

            // No live publisher can exist while the exclusive lease is held. A recent marker still
            // receives a grace period in case another process crashed around its DB commit.
            if (!isRecent)
                TryDeleteFile(marker);
        }

        foreach (var patch in Directory.EnumerateFiles(root, "*.staged.patch"))
        {
            if (!activeRunPatches.Contains(Path.GetFullPath(patch)))
                TryDeleteFile(patch);
        }

        foreach (var patch in Directory.EnumerateFiles(root, "*.vca-snapshot.patch"))
        {
            var fileName = Path.GetFileName(patch);
            var suffix = ".vca-snapshot.patch";
            var id = fileName[..^suffix.Length];
            if (!activeSnapshotIds.Contains(id) && !recentPublishingIds.Contains(id))
                TryDeleteFile(patch);
        }

        // Copy operations are bounded by the same Git timeout and publish by atomic rename. Their
        // old temporary files can be reclaimed without racing a live writer.
        var abandonedBefore = now - (GitTimeout + GitTimeout);
        foreach (var temporary in Directory.EnumerateFiles(root, "*.vca-snapshot.tmp")
                     .Concat(Directory.EnumerateFiles(root, "*.staged.patch.*.tmp")))
        {
            if (File.GetLastWriteTimeUtc(temporary) < abandonedBefore)
                TryDeleteFile(temporary);
        }
    }

    private async Task ApplyStagedSnapshotAsync(
        JobRunRecord run,
        string workspace,
        SnapshotRequirement? requirement,
        CancellationToken cancellationToken)
    {
        if (requirement is null)
            return;

        if (requirement.BaseCommit is not null)
        {
            var checkout = await GitProcessRunner.RunAsync(
                ["checkout", "--detach", "--force", "--quiet", requirement.BaseCommit],
                workspace,
                GitTimeout,
                cancellationToken);
            if (checkout.TimedOut)
                throw new TimeoutException("Checking out the staged snapshot base commit timed out.");
            if (checkout.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Could not check out staged snapshot base {requirement.BaseCommit}: {checkout.StdErr.Trim()}");

            var checkedOutTree = await GitProcessRunner.RunAsync(
                ["rev-parse", "--verify", "HEAD^{tree}"],
                workspace,
                GitTimeout,
                cancellationToken);
            if (checkedOutTree.TimedOut
                || checkedOutTree.ExitCode != 0
                || !string.Equals(
                    checkedOutTree.StdOut.Trim(),
                    requirement.BaseTree,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The cloned base commit does not match the queued staged snapshot.");
            }
        }
        else
        {
            // An initial-commit snapshot has no commit to check out. Reset the fresh clone to an
            // unborn empty index and remove files that may have appeared if HEAD advanced meanwhile.
            await RunRequiredGitAsync(["read-tree", "--empty"], workspace, "reset the initial-commit index", cancellationToken);
            await RunRequiredGitAsync(["clean", "-ffdx"], workspace, "clear the initial-commit workspace", cancellationToken);
        }

        var patch = await ResolveRunPatchAsync(run.Id, requirement.SnapshotId, cancellationToken);
        if (new FileInfo(patch).Length > 0)
        {
            // A required staged snapshot must apply successfully. Falling through to clone HEAD
            // would run against code other than the tree that made the queue row visible.
            await RunRequiredGitAsync(
                ["apply", "--index", "--whitespace=nowarn", patch],
                workspace,
                $"apply the required staged snapshot for run {run.Id}",
                cancellationToken);
        }

        var materializedTree = await GitProcessRunner.RunAsync(
            ["write-tree"],
            workspace,
            GitTimeout,
            cancellationToken);
        if (materializedTree.TimedOut
            || materializedTree.ExitCode != 0
            || !string.Equals(
                materializedTree.StdOut.Trim(),
                requirement.StagedTree,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The materialized workspace does not match the validated staged tree; refusing to run the Job.");
        }
    }

    private static async Task RunRequiredGitAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await GitProcessRunner.RunAsync(
            arguments,
            workingDirectory,
            GitTimeout,
            cancellationToken);
        if (result.TimedOut)
            throw new TimeoutException($"Git timed out while trying to {operation}.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not {operation}: {result.StdErr.Trim()}");
    }

    private async Task<string> ResolveRunPatchAsync(
        string runId,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        var runPatch = StagedPatchPath(runId);
        if (File.Exists(runPatch))
        {
            SetPrivateFileMode(runPatch);
            return runPatch;
        }

        var capturedPatch = CapturedSnapshotPath(snapshotId);
        try
        {
            await CopyAtomicallyAsync(capturedPatch, runPatch, cancellationToken);
        }
        catch (FileNotFoundException) when (File.Exists(runPatch))
        {
            // The enqueue process completed the per-run copy and removed the shared capture between
            // our two checks. The atomically published run copy is the same snapshot.
        }

        if (!File.Exists(runPatch))
            throw new FileNotFoundException(
                "The required staged snapshot is missing; refusing to run against committed HEAD.",
                capturedPatch);
        SetPrivateFileMode(runPatch);
        return runPatch;
    }

    private static SnapshotRequirement? ReadSnapshotRequirement(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return null;

        using var document = JsonDocument.Parse(contextJson);
        var root = document.RootElement;
        if (!root.TryGetProperty(StagedSnapshotIdContextKey, out var idProperty))
            return null;

        var snapshotId = idProperty.GetString();
        var baseCommit = root.TryGetProperty(StagedSnapshotBaseCommitContextKey, out var commitProperty)
            ? commitProperty.GetString()
            : null;
        var baseTree = root.TryGetProperty(StagedSnapshotBaseTreeContextKey, out var baseTreeProperty)
            ? baseTreeProperty.GetString()
            : null;
        var stagedTree = root.TryGetProperty(StagedSnapshotTreeContextKey, out var treeProperty)
            ? treeProperty.GetString()
            : null;
        if (!Guid.TryParseExact(snapshotId, "N", out _)
            || (baseCommit is not null && !IsObjectId(baseCommit))
            || !IsObjectId(baseTree)
            || !IsObjectId(stagedTree))
            throw new InvalidOperationException("The queued staged snapshot metadata is invalid.");

        return new SnapshotRequirement(snapshotId!, baseCommit, baseTree!, stagedTree!);
    }

    private string WorkspaceRoot() =>
        _workspaceRoot
        ?? Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.JOB_WORKSPACES_SUBDIR);

    private string EnsureWorkspaceRoot()
    {
        var root = Path.GetFullPath(WorkspaceRoot());
        if (Directory.Exists(root)
            && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Refusing to use a symbolic link as the Jobs workspace root.");
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            Directory.CreateDirectory(root, PrivateDirectoryMode);
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to use a symbolic link as the Jobs workspace root.");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, PrivateDirectoryMode);
            NormalizeExistingArtifactPermissions(root);
        }
        return root;
    }

    private static void NormalizeExistingArtifactPermissions(string root)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var path in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(path);
            if (!name.Equals(PublicationLockFileName, StringComparison.Ordinal)
                && !name.EndsWith(".vca-snapshot.patch", StringComparison.Ordinal)
                && !name.EndsWith(PublishingMarkerSuffix, StringComparison.Ordinal)
                && !name.EndsWith(".staged.patch", StringComparison.Ordinal)
                && !name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                // Never follow a user-planted symlink while tightening permissions. Operations that
                // consume a required artifact reject reparse points separately and fail closed.
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    File.SetUnixFileMode(path, PrivateFileMode);
            }
            catch (FileNotFoundException)
            {
                // A concurrent publisher/worker atomically renamed or removed this entry.
            }
            catch (DirectoryNotFoundException)
            {
                // The dedicated root is not removed by normal operation, but tolerate shutdown races.
            }
        }
    }

    private string StagedPatchPath(string runId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        return ResolveArtifactPath(digest + ".staged.patch");
    }

    private string CapturedSnapshotPath(string snapshotId)
    {
        if (!Guid.TryParseExact(snapshotId, "N", out _))
            throw new InvalidOperationException("The staged snapshot identifier is invalid.");
        return ResolveArtifactPath(Guid.ParseExact(snapshotId, "N").ToString("N") + ".vca-snapshot.patch");
    }

    private string PublishingMarkerPath(string snapshotId)
    {
        if (!Guid.TryParseExact(snapshotId, "N", out var parsed))
            throw new InvalidOperationException("The staged snapshot identifier is invalid.");
        return ResolveArtifactPath(parsed.ToString("N") + PublishingMarkerSuffix);
    }

    private string PublicationLockPath() => ResolveArtifactPath(PublicationLockFileName);

    private string ResolveArtifactPath(string fileName)
    {
        var root = Path.GetFullPath(WorkspaceRoot());
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var normalizedRoot = Path.GetFullPath(root + Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(normalizedRoot, comparison))
            throw new InvalidOperationException("Refusing to use a job artifact outside the Jobs workspace root.");
        return path;
    }

    private async Task CopyAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The captured staged snapshot is missing.", sourcePath);
        SetPrivateFileMode(sourcePath);

        var temporaryPath = ResolveArtifactPath(
            Path.GetFileName(destinationPath) + $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(sourcePath, new FileStreamOptions
                         {
                             Mode = FileMode.Open,
                             Access = FileAccess.Read,
                             Share = FileShare.Read | FileShare.Delete,
                             BufferSize = 81_920,
                             Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                         }))
            await using (var destination = CreatePrivateFileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool IsObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private async Task<FileStream> AcquirePublicationLeaseAsync(CancellationToken cancellationToken)
    {
        EnsurePublicationLockFile();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    PublicationLockPath(),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private void EnsurePublicationLockFile()
    {
        EnsureWorkspaceRoot();
        var lockPath = PublicationLockPath();
        if (!File.Exists(lockPath))
        {
            try
            {
                using var created = CreatePrivateFileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    FileOptions.None);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                // Another process won the one-time lock-file creation race.
            }
        }
        SetPrivateFileMode(lockPath);
    }

    private static void CreatePrivateEmptyFile(string path)
    {
        using var stream = CreatePrivateFileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.None);
    }

    private static FileStream CreatePrivateFileStream(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options)
    {
        var fileOptions = new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = share,
            Options = options,
            BufferSize = 81_920
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = PrivateFileMode;
        var stream = new FileStream(path, fileOptions);
        try
        {
            SetPrivateFileMode(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to use a symbolic link as a Jobs snapshot artifact.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, PrivateFileMode);
    }

    private void TryDeleteWorkspace(string workspace)
    {
        try
        {
            var root = Path.GetFullPath(WorkspaceRoot() + Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(workspace);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root, comparison) || !Directory.Exists(full))
                return;
            ClearReadOnlyAttributes(full);
            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Could not delete isolated workspace {Workspace}", workspace);
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        // Git writes pack files (*.idx/*.pack) read-only, which blocks Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Could not delete staged patch {Path}", path);
        }
    }

    public sealed class CapturedStagedSnapshot : IDisposable
    {
        private readonly string _markerPath;
        private FileStream? _publicationLease;

        internal CapturedStagedSnapshot(
            string id,
            string? baseCommit,
            string baseTree,
            string stagedTree,
            string markerPath,
            FileStream publicationLease)
        {
            Id = id;
            BaseCommit = baseCommit;
            BaseTree = baseTree;
            StagedTree = stagedTree;
            _markerPath = markerPath;
            _publicationLease = publicationLease;
        }

        public string Id { get; }
        public string? BaseCommit { get; }
        public string BaseTree { get; }
        public string StagedTree { get; }

        public void Dispose()
        {
            var lease = Interlocked.Exchange(ref _publicationLease, null);
            if (lease is null)
                return;
            // Remove the marker while the shared lease is still held. Once the lease is released,
            // an exclusive sweeper can trust that every marker it sees belongs to a crashed process.
            TryDeleteFile(_markerPath);
            lease.Dispose();
        }
    }

    public sealed class ArtifactSweepLease : IDisposable
    {
        private readonly string _root;
        private FileStream? _stream;

        internal ArtifactSweepLease(string root, FileStream stream)
        {
            _root = Path.GetFullPath(root);
            _stream = stream;
        }

        internal void AssertOwns(string root)
        {
            if (_stream is null)
                throw new ObjectDisposedException(nameof(ArtifactSweepLease));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(_root, Path.GetFullPath(root), comparison))
                throw new InvalidOperationException("The artifact sweep lease belongs to another workspace root.");
        }

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    private sealed record SnapshotRequirement(
        string SnapshotId,
        string? BaseCommit,
        string BaseTree,
        string StagedTree);
}
