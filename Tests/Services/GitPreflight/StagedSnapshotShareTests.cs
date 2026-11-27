using System.Text.Json;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using Xunit;

namespace Tests.Services.GitPreflight;

/// <summary>
/// One commit's pre-commit and commit-msg checks share a staged capture (VIBE-374). The reuse is only
/// safe while the base commit, base tree and staged tree all still match, so these tests prove a
/// shared capture equals a fresh one, is reused when nothing moved, and is never reused otherwise.
/// </summary>
public sealed class StagedSnapshotShareTests : IAsyncLifetime
{
    private readonly string _repository = Path.Combine(Path.GetTempPath(), $"staged_share_{Guid.NewGuid():N}");
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SharePath => Path.Combine(_repository, ".git", StagedSnapshotShare.FileName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_repository);
        await GitAsync("init", "-q");
        await GitAsync("config", "user.email", "tests@viberails.local");
        await GitAsync("config", "user.name", "VibeRails Tests");
        await File.WriteAllTextAsync(Path.Combine(_repository, "tracked.cs"), "class Original { }\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(_repository, "vc.rules.md"), "# Agent\n\n## Vibe Rails Rules\n- Log all file changes (WARN)\n", Ct);
        await GitAsync("add", ".");
        await GitAsync("commit", "-q", "-m", "initial");
        await File.WriteAllTextAsync(Path.Combine(_repository, "tracked.cs"), "class Staged { }\n", Ct);
        await GitAsync("add", "tracked.cs");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repository, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repository, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ASharedCaptureIsTheSameCaptureAndIsSavedForTheNextCheck()
    {
        var fresh = await Provider().CaptureAsync(_repository, Ct);
        Assert.False(File.Exists(SharePath));

        var shared = await Provider().CaptureSharedAsync(_repository, Ct);

        Assert.Equal(Describe(fresh), Describe(shared));
        Assert.Equal(Describe(fresh), Describe(await ReadSavedAsync(fresh)));
    }

    [Fact]
    public async Task AReusedCaptureKeepsEveryKindOfChange()
    {
        // Renamed, deleted, binary, executable and policy files, read back from the saved file.
        await File.WriteAllTextAsync(Path.Combine(_repository, "gone.txt"), "soon deleted\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(_repository, "old-name.cs"), "class Moved { int A; int B; int C; }\n", Ct);
        await GitAsync("add", ".");
        await GitAsync("commit", "-q", "-m", "more files");
        await GitAsync("rm", "-q", "gone.txt");
        await GitAsync("mv", "old-name.cs", "new-name.cs");
        await File.WriteAllBytesAsync(Path.Combine(_repository, "image.bin"), [0, 1, 2, 0, 255, 0], Ct);
        await File.WriteAllTextAsync(Path.Combine(_repository, "run.sh"), "#!/bin/sh\necho hi\n", Ct);
        await File.AppendAllTextAsync(Path.Combine(_repository, "vc.rules.md"), "- Log file changes > 5 lines (WARN)\n", Ct);
        await GitAsync("add", ".");
        await GitAsync("update-index", "--chmod=+x", "run.sh");

        var fresh = await Provider().CaptureAsync(_repository, Ct);
        await Provider().CaptureSharedAsync(_repository, Ct);
        var reused = await ReadSavedAsync(fresh);

        Assert.Contains(fresh.Files, file => file.ChangeKind == GitStagedChangeKind.Renamed);
        Assert.Contains(fresh.Files, file => file.ChangeKind == GitStagedChangeKind.Deleted);
        Assert.Contains(fresh.Files, file => file.IsBinary);
        Assert.Contains(fresh.Files, file => file.StagedMode == "100755");
        Assert.NotEmpty(fresh.AgentFiles);
        Assert.Equal(Describe(fresh), Describe(reused));
    }

    [Fact]
    public async Task TheNextCheckOfTheSameTreesReadsTheSavedCapture()
    {
        await Provider().CaptureSharedAsync(_repository, Ct);
        await MarkSavedCaptureAsync();

        var reused = await Provider().CaptureSharedAsync(_repository, Ct);

        Assert.Equal("class Marked { }\n", Assert.Single(reused.Files).Content);
    }

    [Fact]
    public async Task AStagedChangeAfterTheSaveIsCapturedAgain()
    {
        await Provider().CaptureSharedAsync(_repository, Ct);
        await MarkSavedCaptureAsync();
        await File.WriteAllTextAsync(Path.Combine(_repository, "tracked.cs"), "class Restaged { }\n", Ct);
        await GitAsync("add", "tracked.cs");

        var capture = await Provider().CaptureSharedAsync(_repository, Ct);

        Assert.Equal("class Restaged { }\n", Assert.Single(capture.Files).Content);
    }

    [Fact]
    public async Task ANewBaseCommitWithTheSameTreesIsCapturedAgain()
    {
        // Amending without changes keeps both trees but moves HEAD: the base commit is part of the key.
        await Provider().CaptureSharedAsync(_repository, Ct);
        await MarkSavedCaptureAsync();
        await GitAsync("stash", "-q");
        await GitAsync("commit", "-q", "--amend", "-m", "initial, amended");
        await GitAsync("stash", "pop", "-q");
        await GitAsync("add", "tracked.cs");

        var capture = await Provider().CaptureSharedAsync(_repository, Ct);

        Assert.Equal("class Staged { }\n", Assert.Single(capture.Files).Content);
    }

    [Fact]
    public async Task AnExpiredSaveIsCapturedAgain()
    {
        await Provider().CaptureSharedAsync(_repository, Ct);
        await MarkSavedCaptureAsync();
        _now += StagedSnapshotShare.MaximumAge + TimeSpan.FromSeconds(1);

        var capture = await Provider().CaptureSharedAsync(_repository, Ct);

        Assert.Equal("class Staged { }\n", Assert.Single(capture.Files).Content);
    }

    [Fact]
    public async Task AnUnreadableSaveIsCapturedAgain()
    {
        await Provider().CaptureSharedAsync(_repository, Ct);
        await File.WriteAllTextAsync(SharePath, "{ not json", Ct);

        var capture = await Provider().CaptureSharedAsync(_repository, Ct);

        Assert.Equal("class Staged { }\n", Assert.Single(capture.Files).Content);
    }

    [Fact]
    public async Task APlainCaptureNeitherReadsNorWritesTheShare()
    {
        await Provider().CaptureSharedAsync(_repository, Ct);
        await MarkSavedCaptureAsync();

        var capture = await Provider().CaptureAsync(_repository, Ct);

        Assert.Equal("class Staged { }\n", Assert.Single(capture.Files).Content);
    }

    /// <summary>The saved capture, read the way the next check reads it; a miss fails the test.</summary>
    private async Task<GitStagedSnapshot> ReadSavedAsync(GitStagedSnapshot fresh)
    {
        var saved = await new StagedSnapshotShare(() => _now).TryReadAsync(
            Path.Combine(_repository, ".git"), fresh.RepositoryPath, fresh.StagedIdentity!, Ct);
        Assert.NotNull(saved);
        return saved;
    }

    private GitStagedSnapshotProvider Provider() =>
        new(64L * 1024 * 1024, share: new StagedSnapshotShare(() => _now));

    /// <summary>Rewrites the saved content so a test can tell a reused capture from a fresh one.</summary>
    private async Task MarkSavedCaptureAsync()
    {
        var json = await File.ReadAllTextAsync(SharePath, Ct);
        Assert.Contains("class Staged { }", json);
        await File.WriteAllTextAsync(SharePath, json.Replace("class Staged { }", "class Marked { }"), Ct);
    }

    private static string Describe(GitStagedSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, StagedSnapshotShareJson.Default.GitStagedSnapshot);

    private async Task GitAsync(params string[] args)
    {
        var result = await GitCli.RunAsync(_repository, args, Ct);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.StdErr}");
    }
}
