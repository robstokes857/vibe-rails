using Serilog;
using VibeRails.DB;
using VibeRails.Services.Git;

namespace VibeRails.Services.Workspaces;

/// <summary>Creates prompt worktrees and removes only verified, expired, idle worktrees it owns.</summary>
public interface IManagedWorktreeService
{
    /// <summary>Creates a fresh branch and worktree from committed HEAD and records its expiry.</summary>
    Task<ManagedWorktree> CreateAsync(string workingDirectory, WorktreeOptions options, CancellationToken ct = default);
    /// <summary>Protects a managed checkout from cleanup until the caller disposes the lease.</summary>
    Task<IDisposable?> AcquireUsageAsync(string workingDirectory, CancellationToken ct = default);
    /// <summary>Removes expired, verified worktrees that are idle and returns the removal count.</summary>
    Task<int> CleanupExpiredAsync(CancellationToken ct = default);
}

/// <summary>Uses Git argv, durable ownership markers, and OS-backed leases shared across roots.</summary>
public sealed class ManagedWorktreeService(
    IManagedWorktreeStore store,
    TimeProvider? timeProvider = null,
    string? defaultDirectory = null) : IManagedWorktreeService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly string _defaultDirectory = defaultDirectory ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe_rails", "worktrees");
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public async Task<ManagedWorktree> CreateAsync(string workingDirectory, WorktreeOptions options, CancellationToken ct = default)
    {
        if (options.DeleteAfterDays is < 7 or > 36500) throw new ArgumentException("Worktree retention must be at least 7 days, or never.");
        var root = await GitAsync(workingDirectory, ["rev-parse", "--show-toplevel"], ct);
        root = System.IO.Path.GetFullPath(root);
        var common = System.IO.Path.GetFullPath(await GitAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct));
        EnsureNoLinks(common);
        var head = await GitAsync(root, ["rev-parse", "--verify", "HEAD"], ct);
        var id = Guid.NewGuid().ToString("N");
        var repoName = new string(System.IO.Path.GetFileName(root).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        var path = options.Path is null
            ? System.IO.Path.Combine(_defaultDirectory, $"{repoName}-{id[..12]}")
            : System.IO.Path.GetFullPath(ExpandHome(options.Path), root);
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (Contains(root, path) || Contains(path, root) || Contains(common, path))
            throw new ArgumentException("Choose a worktree path outside the source checkout and its Git directory.");
        EnsureNoLinks(path);
        if (Directory.Exists(path) || File.Exists(path))
            throw new ArgumentException($"The worktree path already exists: {path}. Choose a new directory.");
        if ((await store.ListAsync(ct)).Any(worktree => Contains(worktree.Path, path)))
            throw new ArgumentException("A managed worktree cannot be created inside another managed worktree.");

        var parent = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        // Moving a fresh empty directory reserves the exact destination without ever adopting an
        // existing user directory, even when two processes request the same custom path.
        var reservation = System.IO.Path.Combine(parent, ".viberails-reserve-" + id);
        Directory.CreateDirectory(reservation);
        try { Directory.Move(reservation, path); }
        finally { if (Directory.Exists(reservation)) Directory.Delete(reservation); }

        var branch = $"viberails/worktree-{id}";
        var created = false;
        try
        {
            await GitAsync(root, ["worktree", "add", "-b", branch, "--", path, head], ct, TimeSpan.FromMinutes(2));
            created = true;
            var gitDirectory = System.IO.Path.GetFullPath(await GitAsync(path, ["rev-parse", "--absolute-git-dir"], ct));
            var worktreeMetadata = System.IO.Path.Combine(common, "worktrees");
            if (!Contains(worktreeMetadata, gitDirectory) || SamePath(worktreeMetadata, gitDirectory))
                throw new IOException("Git did not create the worktree in the expected repository.");
            EnsureNoLinks(gitDirectory);
            var now = _clock.GetUtcNow().UtcDateTime;
            var worktree = new ManagedWorktree(id, path, common, gitDirectory, branch, now,
                options.DeleteAfterDays is int days ? now.AddDays(days) : null);
            await using (var marker = new StreamWriter(new FileStream(MarkerPath(worktree), FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                await marker.WriteAsync(id.AsMemory(), ct);
            await store.AddAsync(worktree, ct);
            Log.Information("[Worktree] Created {Path} branch={Branch} expires={Expiry}", path, branch, worktree.DeleteAfterUTC);
            return worktree;
        }
        catch
        {
            // Roll back only this launch's fresh checkout. Git owns recursive removal; an
            // unregistered nonempty directory is deliberately left for inspection.
            if (created)
                await GitCli.RunAsync(common, ["worktree", "remove", "--force", "--", path], CancellationToken.None, TimeSpan.FromMinutes(2));
            else if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IDisposable?> AcquireUsageAsync(string workingDirectory, CancellationToken ct = default)
    {
        var worktree = (await store.ListAsync(ct)).FirstOrDefault(w => Contains(w.Path, workingDirectory));
        if (worktree is null) return null;
        var lease = OpenLease(worktree, exclusive: false);
        try
        {
            if (!HasOwnership(worktree)) throw new IOException($"The managed worktree is missing or has changed: {worktree.Path}");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    /// <inheritdoc />
    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var expired = (await store.ListAsync(ct)).Where(w => w.DeleteAfterUTC is { } expiry && expiry <= now).ToArray();
        var removed = 0;
        foreach (var worktree in expired)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Shared session leases cover launch, the PTY and post-exit steps. The exclusive
                // lease also prevents two root backends from removing the same worktree.
                using var lease = OpenLease(worktree, exclusive: true);
                var openDirectories = await store.GetOpenSessionDirectoriesAsync(ct);
                if (openDirectories.Any(directory => Contains(worktree.Path, directory))) continue;
                if (!HasOwnership(worktree))
                {
                    Log.Warning("[Worktree] Skipping expired path whose ownership cannot be verified: {Path}", worktree.Path);
                    continue;
                }
                // A single --force implements the explicitly requested age-based retention,
                // including dirty/untracked files. Git still protects worktrees locked by users.
                await GitAsync(worktree.GitCommonDirectory, ["worktree", "remove", "--force", "--", worktree.Path], ct, TimeSpan.FromMinutes(2));
                await store.RemoveAsync(worktree.Id, ct);
                removed++;
                Log.Information("[Worktree] Removed expired {Path}; retained branch {Branch}", worktree.Path, worktree.Branch);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (IOException) { /* A live usage/cleanup lease or temporarily locked file: retry next tick. */ }
            catch (Exception ex) { Log.Warning(ex, "[Worktree] Could not remove expired {Path}; will retry", worktree.Path); }
        }
        return removed;
    }

    private static FileStream OpenLease(ManagedWorktree worktree, bool exclusive)
    {
        var directory = System.IO.Path.Combine(worktree.GitCommonDirectory, "viberails-worktree-leases");
        EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, worktree.Id + ".lease");
        EnsureNoLinks(path);
        // Lease files remain in place: unlinking one would let another Unix process lock a
        // different inode while a session still holds the original lease.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            exclusive ? FileShare.None : FileShare.ReadWrite);
    }

    private static bool HasOwnership(ManagedWorktree worktree)
    {
        EnsureNoLinks(worktree.Path);
        EnsureNoLinks(worktree.GitDirectory);
        var worktreeMetadata = System.IO.Path.Combine(worktree.GitCommonDirectory, "worktrees");
        if (!Contains(worktreeMetadata, worktree.GitDirectory) || SamePath(worktreeMetadata, worktree.GitDirectory)) return false;
        var gitFile = System.IO.Path.Combine(worktree.Path, ".git");
        EnsureNoLinks(gitFile);
        EnsureNoLinks(MarkerPath(worktree));
        if (!File.Exists(gitFile) || !File.Exists(MarkerPath(worktree))) return false;
        var pointer = File.ReadAllText(gitFile).Trim();
        if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) return false;
        return SamePath(System.IO.Path.GetFullPath(pointer[8..], worktree.Path), worktree.GitDirectory)
            && File.ReadAllText(MarkerPath(worktree)) == worktree.Id;
    }

    private static string MarkerPath(ManagedWorktree worktree) => System.IO.Path.Combine(worktree.GitDirectory, "viberails-worktree-id");

    private static bool SamePath(string first, string second) => string.Equals(
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(first)),
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(second)), PathComparison);

    private static bool Contains(string parent, string child)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(parent));
        var path = System.IO.Path.GetFullPath(child);
        return SamePath(root, path) || path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, PathComparison);
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Worktree paths cannot traverse symbolic links or junctions: {current}");
        }
    }

    private static string ExpandHome(string path) => path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;

    private static async Task<string> GitAsync(string directory, string[] arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        var result = await GitCli.RunAsync(directory, arguments, ct, timeout);
        if (!result.Succeeded) throw new InvalidOperationException($"Could not prepare or clean up the worktree: {result.StdErr.Trim()}" + (result.TimedOut ? " (Git timed out.)" : ""));
        return result.StdOut.Trim();
    }
}
