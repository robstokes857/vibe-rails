using System.Collections.Concurrent;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.Workspaces;

/// <summary>
/// The directory an environment should run in, and the workspace behind it when that is not
/// the project directory itself.
/// </summary>
/// <param name="WorkingDirectory">Where the CLI runs. The project path in Project mode.</param>
/// <param name="Workspace">The clone backing this launch, or null in Project mode.</param>
/// <param name="Error">A user-facing failure reason; null on success.</param>
public sealed record WorkspaceResolution(string WorkingDirectory, Sandbox? Workspace, string? Error = null)
{
    public bool Success => Error is null;

    public static WorkspaceResolution InProject(string projectPath) => new(projectPath, null);
    public static WorkspaceResolution Failed(string projectPath, string error) => new(projectPath, null, error);
}

public interface IRunWorkspaceService
{
    /// <summary>
    /// Resolves — provisioning if necessary — the directory this environment runs in. Safe to
    /// call on every launch: Project mode is a pure pass-through, and Persistent mode reuses
    /// the clone it made the first time.
    /// </summary>
    Task<WorkspaceResolution> ResolveAsync(LLM_Environment environment, string projectPath, CancellationToken ct = default);

    /// <summary>The current workspace for an environment, without provisioning one.</summary>
    Task<Sandbox?> GetWorkspaceAsync(LLM_Environment environment, CancellationToken ct = default);

    /// <summary>
    /// Gives up an environment's workspaces: best-effort delete, and whatever will not delete
    /// is released to a standalone sandbox rather than left dangling.
    /// </summary>
    Task ReleaseAsync(int environmentId, CancellationToken ct = default);

    /// <summary>
    /// Unbinds an environment's workspaces without deleting anything. Used when the workspace
    /// mode changes: the clone may hold uncommitted work, so it survives as a standalone
    /// sandbox and the environment provisions a fresh one on its next launch.
    /// </summary>
    Task DetachAsync(int environmentId, CancellationToken ct = default);
}

/// <summary>
/// Turns an environment's <see cref="EnvironmentWorkspaceMode"/> into a real directory.
///
/// Persistent and PerRun are deliberately the same code path with two knobs — retention and
/// whether dirty files come along — because they are the same feature. A sandbox is a clone
/// kept forever; a per-run workspace is a clone kept until the next few replace it.
///
/// All the git, path-containment and Windows read-only handling lives in
/// <see cref="SandboxService"/> and is reused wholesale. This service owns only the questions
/// SandboxService has no opinion about: which name, whether to reuse, and what to prune.
/// </summary>
public sealed class RunWorkspaceService(
    IRepository repository,
    ISandboxService sandboxService) : IRunWorkspaceService
{
    /// <summary>
    /// How many per-run workspaces survive per environment. Every run is a full working copy,
    /// so this is the only thing standing between a nightly automation and a full disk.
    /// </summary>
    public const int MaxRetainedPerRunWorkspaces = 3;

    /// <summary>
    /// Grace period before a workspace may be pruned at all. Covers the window between the clone
    /// completing and the CLI's session row existing, during which an open-session check would
    /// wrongly report the workspace as idle.
    /// </summary>
    public static readonly TimeSpan MinimumPruneAge = TimeSpan.FromMinutes(10);

    // Serialises provisioning per environment so two launches racing each other cannot both
    // decide the workspace is missing and both try to clone into the same directory. In-process
    // only: a job run spawned as its own process resolves its workspace here in the dashboard
    // before the terminal opens, so every writer does go through this instance.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ProvisionLocks = new();

    public async Task<WorkspaceResolution> ResolveAsync(
        LLM_Environment environment,
        string projectPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.UsesWorkspaceClone)
            return WorkspaceResolution.InProject(projectPath);

        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            return WorkspaceResolution.Failed(projectPath, $"The project directory no longer exists: {projectPath}");

        if (!await IsGitRepositoryAsync(projectPath, ct))
        {
            return WorkspaceResolution.Failed(
                projectPath,
                $"'{environment.CustomName}' runs in a git clone, but {projectPath} is not a git repository.");
        }

        var gate = ProvisionLocks.GetOrAdd(environment.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return environment.WorkspaceMode == EnvironmentWorkspaceMode.Persistent
                ? await ResolvePersistentAsync(environment, projectPath, ct)
                : await ResolvePerRunAsync(environment, projectPath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Clone failures are ordinary and actionable (no disk, no network, a locked
            // directory), so the reason reaches the user instead of a generic launch error.
            Log.Error(ex, "[Workspace] Could not provision a workspace for environment {EnvironmentId}", environment.Id);
            return WorkspaceResolution.Failed(
                projectPath,
                $"Could not prepare the workspace for '{environment.CustomName}': {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Sandbox?> GetWorkspaceAsync(LLM_Environment environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.UsesWorkspaceClone)
            return null;

        var owned = await repository.GetSandboxesByEnvironmentIdAsync(environment.Id, ct);
        return owned.FirstOrDefault();
    }

    public async Task DetachAsync(int environmentId, CancellationToken ct = default)
    {
        try
        {
            await repository.OrphanSandboxesForEnvironmentAsync(environmentId, ct);
        }
        catch (Exception ex)
        {
            // The environment's new mode is already saved; a failure here leaves the old
            // workspace attached, which the next launch would reuse. Loud, not fatal.
            Log.Warning(ex, "[Workspace] Could not detach workspaces from environment {EnvironmentId}", environmentId);
        }
    }

    public async Task ReleaseAsync(int environmentId, CancellationToken ct = default)
    {
        List<Sandbox> owned;
        try
        {
            owned = await repository.GetSandboxesByEnvironmentIdAsync(environmentId, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Workspace] Could not list workspaces for environment {EnvironmentId}", environmentId);
            return;
        }

        if (owned.Count == 0)
            return;

        // Release the rows first. If the process dies halfway through the deletes below, the
        // workspaces are already standalone sandboxes the user can see and remove by hand —
        // never rows pointing at an environment that no longer exists.
        try
        {
            await repository.OrphanSandboxesForEnvironmentAsync(environmentId, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Workspace] Could not release workspaces for environment {EnvironmentId}", environmentId);
            return;
        }

        foreach (var sandbox in owned)
        {
            var deleted = await sandboxService.TryDeleteSandboxAsync(sandbox.Id, ct);
            if (!deleted)
            {
                Log.Information(
                    "[Workspace] Workspace '{Name}' could not be removed and is now a standalone sandbox at {Path}",
                    sandbox.Name, sandbox.Path);
            }
        }
    }

    private async Task<WorkspaceResolution> ResolvePersistentAsync(
        LLM_Environment environment,
        string projectPath,
        CancellationToken ct)
    {
        var owned = await repository.GetSandboxesByEnvironmentIdAsync(environment.Id, ct);

        // A workspace whose directory was deleted outside the app is treated as absent and
        // re-cloned, rather than handing the CLI a path that is not there.
        var existing = owned.FirstOrDefault(s =>
            ProjectPathComparer.Matches(s.ProjectPath, projectPath) && Directory.Exists(s.Path));

        if (existing is not null)
            return new WorkspaceResolution(existing.Path, existing);

        var stale = owned.FirstOrDefault(s => ProjectPathComparer.Matches(s.ProjectPath, projectPath));
        if (stale is not null)
        {
            Log.Information(
                "[Workspace] Workspace '{Name}' for environment {EnvironmentId} is missing from disk; re-creating it",
                stale.Name, environment.Id);
            await sandboxService.TryDeleteSandboxAsync(stale.Id, ct);
        }

        var created = await CreateWorkspaceAsync(
            environment,
            projectPath,
            WorkspaceNameSlug.ForEnvironment(environment.CustomName, environment.Id),
            // A persistent workspace is a place to keep working, so it starts from the project
            // as it actually is — same contract as a hand-made sandbox.
            copyDirtyFiles: true,
            ct);

        return new WorkspaceResolution(created.Path, created);
    }

    private async Task<WorkspaceResolution> ResolvePerRunAsync(
        LLM_Environment environment,
        string projectPath,
        CancellationToken ct)
    {
        var created = await CreateWorkspaceAsync(
            environment,
            projectPath,
            WorkspaceNameSlug.ForRun(
                environment.CustomName,
                environment.Id,
                DateTime.UtcNow,
                WorkspaceNameSlug.NewRunToken()),
            // "Fresh" means the committed tree and nothing else. Note this leaves the clone
            // without .env or any other gitignored local config.
            copyDirtyFiles: false,
            ct);

        await PruneAsync(environment, keepSandboxId: created.Id, ct);

        return new WorkspaceResolution(created.Path, created);
    }

    private async Task<Sandbox> CreateWorkspaceAsync(
        LLM_Environment environment,
        string projectPath,
        string name,
        bool copyDirtyFiles,
        CancellationToken ct)
    {
        Log.Information(
            "[Workspace] Cloning {ProjectPath} into workspace '{Name}' for environment '{EnvironmentName}'",
            projectPath, name, environment.CustomName);

        var sandbox = await sandboxService.CreateSandboxAsync(
            name,
            projectPath,
            new SandboxCreateOptions(CopyDirtyFiles: copyDirtyFiles, EnvironmentId: environment.Id),
            ct);

        Log.Information("[Workspace] Workspace '{Name}' ready at {Path}", sandbox.Name, sandbox.Path);
        return sandbox;
    }

    /// <summary>
    /// Drops per-run workspaces past the retention count, newest kept.
    ///
    /// Two things are never eligible, and both matter:
    ///
    /// <b>Anything that is not this environment's own run workspace.</b> The name must be one
    /// <see cref="WorkspaceNameSlug.ForRun"/> would have produced for this environment id, so an
    /// environment switched from Persistent to PerRun never has its persistent workspace pruned,
    /// a hand-attached sandbox is left alone, and two environments whose display names slug to
    /// the same text cannot reach each other's clones.
    ///
    /// <b>Anything still being worked in.</b> Retention is a count, and a count knows nothing
    /// about a run that is still going — a fourth run would otherwise delete the oldest live
    /// workspace. Windows would refuse the delete because of open handles, but Linux would
    /// happily unlink the directory out from under a running agent, so this is checked
    /// explicitly rather than left to the filesystem.
    /// </summary>
    private async Task PruneAsync(LLM_Environment environment, int keepSandboxId, CancellationToken ct)
    {
        try
        {
            var owned = await repository.GetSandboxesByEnvironmentIdAsync(environment.Id, ct);

            var candidates = owned
                .Where(s => s.Id != keepSandboxId)
                .Where(s => WorkspaceNameSlug.IsRunNameFor(environment.CustomName, environment.Id, s.Name))
                .OrderByDescending(s => s.CreatedUTC)
                .Skip(Math.Max(0, MaxRetainedPerRunWorkspaces - 1))
                .ToList();

            foreach (var sandbox in candidates)
            {
                if (await IsInUseAsync(sandbox, ct))
                {
                    // Deliberately kept over retention rather than deleted. Retention is a
                    // disk-space policy; destroying an in-flight run's working tree is not a
                    // trade it is allowed to make. The next prune pass will pick it up.
                    Log.Information(
                        "[Workspace] Keeping workspace '{Name}' past retention — a session is still open in it",
                        sandbox.Name);
                    continue;
                }

                Log.Information(
                    "[Workspace] Pruning workspace '{Name}' (keeping the newest {Keep})",
                    sandbox.Name, MaxRetainedPerRunWorkspaces);
                await sandboxService.TryDeleteSandboxAsync(sandbox.Id, ct);
            }
        }
        catch (Exception ex)
        {
            // Retention is housekeeping. A launch that already has its workspace must not fail
            // because an older one could not be tidied away.
            Log.Warning(ex, "[Workspace] Could not prune workspaces for environment {EnvironmentId}", environment.Id);
        }
    }

    /// <summary>
    /// Whether a workspace is still being worked in.
    ///
    /// An open session inside the directory is the durable signal, and it covers both in-app
    /// tabs and job runs. It is backed up by a minimum age because there is a gap between the
    /// clone finishing and the session row appearing — without it, a burst of runs could prune a
    /// workspace whose CLI has not registered itself yet.
    ///
    /// Errs toward "in use": a failed check keeps the directory. Leaking a clone costs disk;
    /// deleting a live one costs the user's work.
    /// </summary>
    private async Task<bool> IsInUseAsync(Sandbox sandbox, CancellationToken ct)
    {
        if (DateTime.UtcNow - sandbox.CreatedUTC < MinimumPruneAge)
            return true;

        try
        {
            return await repository.HasOpenSessionUnderDirectoryAsync(sandbox.Path, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[Workspace] Could not tell whether workspace '{Name}' is in use; keeping it",
                sandbox.Name);
            return true;
        }
    }

    private static async Task<bool> IsGitRepositoryAsync(string projectPath, CancellationToken ct)
    {
        try
        {
            var result = await GitProcessRunner.RunAsync(
                ["rev-parse", "--git-dir"],
                projectPath,
                TimeSpan.FromSeconds(15),
                ct);
            return !result.TimedOut && result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Workspace] Could not determine whether {ProjectPath} is a git repository", projectPath);
            return false;
        }
    }

}
