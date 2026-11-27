using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Hands the staged snapshot that one commit's pre-commit check captured to the commit-msg check that
/// follows it (VIBE-374). Both read the same two trees, and their git work (two tree listings, three
/// diffs and every changed blob) was a third of each commit's hook time.
/// </summary>
/// <remarks>
/// <para>
/// A capture is a function of the base commit, the base tree and the staged tree (<c>git write-tree</c>
/// of the index), all content-addressed. The reader recomputes those three itself and uses an entry only
/// when all three match, together with the repository path and the VibeRails build that wrote it. A
/// staged change after pre-commit therefore misses, and the reader captures as usual.
/// </para>
/// <para>
/// Diff configuration and <c>.gitattributes</c> also shape a capture (binary detection, rename limits),
/// and they are not in the key. Git changes neither between its hooks; someone editing them while the
/// commit message editor is open gets what pre-commit read. Entries expire after
/// <see cref="MaximumAge"/> to bound that.
/// </para>
/// <para>
/// The file lives in the worktree's git directory, beside the STOP bypass request. Anyone who can write
/// there can already rewrite or remove the hooks, so it trusts nothing the hooks do not already trust.
/// Every failure to read or write it is quiet: the capture simply runs.
/// </para>
/// </remarks>
public sealed class StagedSnapshotShare
{
    internal const string FileName = "viberails-staged-snapshot.json";

    /// <summary>How long after pre-commit an entry may still be used, so an editor left open is covered.</summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Snapshots holding more text than this are not written: serializing them costs more than the
    /// git work saved, and the file would sit in the git directory until the next commit.
    /// </summary>
    internal const long MaximumTextChars = 16L * 1024 * 1024;

    /// <summary>Bumped whenever <see cref="GitStagedSnapshot"/> changes shape.</summary>
    private const int Format = 1;

    private static readonly string CurrentBuild = DescribeBuild();
    private readonly Func<DateTimeOffset> _clock;

    public StagedSnapshotShare()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    internal StagedSnapshotShare(Func<DateTimeOffset> clock) => _clock = clock;

    internal static string PathFor(string gitDirectory) => Path.Combine(gitDirectory, FileName);

    /// <summary>The snapshot written for exactly this repository and identity, or null.</summary>
    public async Task<GitStagedSnapshot?> TryReadAsync(
        string gitDirectory,
        string repositoryPath,
        GitStagedSnapshotIdentity identity,
        CancellationToken cancellationToken)
    {
        var path = PathFor(gitDirectory);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            // UTF-16 text can take up to three bytes per char in UTF-8, and JSON escapes add more.
            if (file.Length > MaximumTextChars * 4)
                return null;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var entry = await JsonSerializer.DeserializeAsync(stream, StagedSnapshotShareJson.Default.StagedSnapshotShareEntry, cancellationToken);
            if (entry is null
                || entry.Format != Format
                || !string.Equals(entry.Build, CurrentBuild, StringComparison.Ordinal)
                || !string.Equals(entry.Snapshot.RepositoryPath, repositoryPath, StringComparison.Ordinal)
                || entry.Snapshot.StagedIdentity != identity)
            {
                return null;
            }

            var age = _clock() - entry.WrittenUtc;
            return age >= TimeSpan.FromMinutes(-1) && age <= MaximumAge ? entry.Snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException
            or ArgumentException or InvalidOperationException)
        {
            Log.Debug(ex, "[GitPreflight] Ignored the shared staged snapshot at {Path}", path);
            return null;
        }
    }

    /// <summary>Saves the snapshot for the next check of the same commit. Best effort.</summary>
    public async Task WriteAsync(string gitDirectory, GitStagedSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = PathFor(gitDirectory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return;
            if (snapshot.StagedIdentity is null || TextChars(snapshot) > MaximumTextChars)
            {
                // A stale entry could not match this identity, but it need not keep its disk space either.
                File.Delete(path);
                return;
            }

            var entry = new StagedSnapshotShareEntry(Format, CurrentBuild, _clock(), snapshot with
            {
                // Added by the pipeline after capture, from this run's card; never part of the capture.
                QualityExemptions = null
            });
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, entry, StagedSnapshotShareJson.Default.StagedSnapshotShareEntry, cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or JsonException or ArgumentException or InvalidOperationException)
        {
            Log.Debug(ex, "[GitPreflight] Could not share the staged snapshot at {Path}", path);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next write to replace; it never matches a reader's file name.
            }
        }
    }

    private static long TextChars(GitStagedSnapshot snapshot)
    {
        long total = 0;
        foreach (var file in snapshot.Files)
            total += (file.Content?.Length ?? 0) + (file.PreviousContent?.Length ?? 0) + (file.AddedContent?.Length ?? 0);
        foreach (var agent in snapshot.AgentFiles)
            total += agent.Content.Length;
        return total;
    }

    /// <summary>
    /// The running VibeRails: its version and the executable's write time, so two builds of one
    /// version (a developer's) never read each other's entries.
    /// </summary>
    private static string DescribeBuild()
    {
        var version = typeof(StagedSnapshotShare).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var stamp = 0L;
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } executable)
                stamp = File.GetLastWriteTimeUtc(executable).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Version alone still separates releases.
        }
        return FormattableString.Invariant($"{version}|{stamp}");
    }
}

internal sealed record StagedSnapshotShareEntry(
    int Format,
    string Build,
    DateTimeOffset WrittenUtc,
    GitStagedSnapshot Snapshot);

[JsonSerializable(typeof(StagedSnapshotShareEntry))]
internal partial class StagedSnapshotShareJson : JsonSerializerContext;
