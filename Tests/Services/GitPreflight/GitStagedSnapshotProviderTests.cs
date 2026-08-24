using System.Diagnostics;
using System.Text;
using VibeRails.Services.GitPreflight;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class GitStagedSnapshotProviderTests : IAsyncLifetime
{
    private string _repository = null!;

    public async ValueTask InitializeAsync()
    {
        _repository = Path.Combine(Path.GetTempPath(), $"git_preflight_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repository);
        await GitAsync("init");
        await GitAsync("config", "user.email", "tests@viberails.local");
        await GitAsync("config", "user.name", "VibeRails Tests");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class Original { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "vc.rules.md"),
            "# Agent\n\n## Rules\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", ".");
        await GitAsync("commit", "-m", "initial");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_repository))
        {
            foreach (var file in Directory.EnumerateFiles(_repository, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_repository, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CaptureAsync_UsesIndexContent_NotUnstagedWorkingTreeContent()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class Staged { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class Unstaged { }\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var staged = Assert.Single(snapshot.Files);
        Assert.Equal("tracked.cs", staged.RelativePath);
        Assert.Equal("class Staged { }\n", staged.Content);
        Assert.DoesNotContain("Unstaged", staged.Content);
        Assert.Equal(GitStagedChangeKind.Modified, staged.ChangeKind);
        // The committed version remains available for consumers that need complete context.
        Assert.Equal("class Original { }\n", staged.PreviousContent);
        Assert.Equal("class Staged { }", staged.AddedContent);
        Assert.Equal([1], staged.AddedLineNumbers);
    }

    [Fact]
    public async Task CaptureAsync_ListsTrackedFiles_AndGivesAddedFilesNoBaseline()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "untracked.cs"),
            "class Untracked { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "brand_new.cs"),
            "class BrandNew { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "brand_new.cs");

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot.TrackedFiles);
        Assert.Contains("tracked.cs", snapshot.TrackedFiles);
        Assert.Contains("brand_new.cs", snapshot.TrackedFiles);
        Assert.DoesNotContain("untracked.cs", snapshot.TrackedFiles);

        var added = Assert.Single(snapshot.Files);
        Assert.Equal(GitStagedChangeKind.Added, added.ChangeKind);
        Assert.Null(added.PreviousContent);
        Assert.Equal("class BrandNew { }", added.AddedContent);
        Assert.Equal([1], added.AddedLineNumbers);
    }

    [Fact]
    public async Task CaptureAsync_RemovalOnlyEdit_HasNoAddedContent()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            """
            class Original
            {
                int Keep;
                int Remove;
            }

            """,
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        await GitAsync("commit", "-m", "multiline baseline");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            """
            class Original
            {
                int Keep;
            }

            """,
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var staged = Assert.Single(snapshot.Files);
        Assert.Equal(GitStagedChangeKind.Modified, staged.ChangeKind);
        Assert.Equal(string.Empty, staged.AddedContent);
        Assert.Empty(staged.AddedLineNumbers!);
    }

    [Fact]
    public async Task CaptureAsync_ContentIdenticalRename_HasNoAddedContent()
    {
        await GitAsync("mv", "tracked.cs", "renamed.cs");

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var staged = Assert.Single(snapshot.Files);
        Assert.Equal("renamed.cs", staged.RelativePath);
        Assert.Equal("tracked.cs", staged.PreviousRelativePath);
        Assert.Equal(GitStagedChangeKind.Renamed, staged.ChangeKind);
        Assert.Equal(string.Empty, staged.AddedContent);
        Assert.Empty(staged.AddedLineNumbers!);
    }

    [Fact]
    public async Task CaptureAsync_AddedContent_MapsBackToItsCurrentFileLine()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            """
            class Original
            {
                void Keep() { }
            }

            """,
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        await GitAsync("commit", "-m", "method baseline");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            """
            class Original
            {
                void Added() { if (true) { } }
                void Keep() { }
            }

            """,
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var staged = Assert.Single(snapshot.Files);
        Assert.Equal("    void Added() { if (true) { } }", staged.AddedContent);
        Assert.Equal([3], staged.AddedLineNumbers);
    }

    [Fact]
    public async Task CaptureAsync_IdentifiesAddedBinaryAndDeletedFiles()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_repository, "asset.bin"),
            [0, 1, 2, 3],
            TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(_repository, "tracked.cs"));
        await GitAsync("add", "-A");

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var binary = Assert.Single(snapshot.Files, file => file.RelativePath == "asset.bin");
        Assert.True(binary.IsBinary);
        Assert.Null(binary.Content);
        Assert.Equal(GitStagedChangeKind.Added, binary.ChangeKind);

        var deleted = Assert.Single(snapshot.Files, file => file.RelativePath == "tracked.cs");
        Assert.False(deleted.ExistsInIndex);
        Assert.Equal(GitStagedChangeKind.Deleted, deleted.ChangeKind);
    }

    [Fact]
    public async Task CaptureAsync_RecordsAStagedSubmoduleWithoutReadingItsGitLink()
    {
        // A staged submodule is an index entry of mode 160000 whose object is a commit in
        // the submodule's own history. `git show :<path>` on it fails with "bad object",
        // which used to abort the whole snapshot (and with it Git Guard and the pre-commit
        // hook). The commit SHA below intentionally exists nowhere.
        await GitAsync(
            "update-index", "--add", "--cacheinfo",
            "160000,1111111111111111111111111111111111111111,PyBridge");

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var gitLink = Assert.Single(snapshot.Files);
        Assert.Equal("PyBridge", gitLink.RelativePath);
        Assert.Equal(GitStagedChangeKind.Added, gitLink.ChangeKind);
        Assert.True(gitLink.IsBinary);
        Assert.Null(gitLink.Content);
        Assert.DoesNotContain("PyBridge", snapshot.TrackedFiles!);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_RecordsASubmoduleWithoutReadingItsGitLink()
    {
        // A real embedded repository: `git add` stages it as a gitlink whose commit SHA
        // lives only in the nested repository's object store.
        var submodule = Path.Combine(_repository, "PyBridge");
        Directory.CreateDirectory(submodule);
        await GitInAsync(submodule, "init");
        await GitInAsync(submodule, "config", "user.email", "tests@viberails.local");
        await GitInAsync(submodule, "config", "user.name", "VibeRails Tests");
        await File.WriteAllTextAsync(
            Path.Combine(submodule, "bridge.py"),
            "print('bridge')\n",
            TestContext.Current.CancellationToken);
        await GitInAsync(submodule, "add", ".");
        await GitInAsync(submodule, "commit", "-m", "submodule content");
        await GitAsync("add", "PyBridge");

        var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var gitLink = Assert.Single(snapshot.Files, file => file.RelativePath == "PyBridge");
        Assert.True(gitLink.IsBinary);
        Assert.Null(gitLink.Content);
        Assert.Null(gitLink.PreviousContent);
        Assert.DoesNotContain("PyBridge", snapshot.TrackedFiles!);
        Assert.DoesNotContain(
            snapshot.Files,
            file => file.RelativePath.StartsWith("PyBridge/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_SharesOnlyTheActiveCapture_ThenReleasesIt()
    {
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource<GitStagedSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        var provider = new GitStagedSnapshotProvider(
            maximumUnpushedSnapshotBytes: 64,
            workingTreeCaptureOverride: (_, cancellationToken) =>
            {
                Interlocked.Increment(ref captureCount);
                captureStarted.TrySetResult();
                return releaseCapture.Task.WaitAsync(cancellationToken);
            });

        var first = provider.CaptureWorkingTreeAsync(_repository, CancellationToken.None);
        await captureStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = provider.CaptureWorkingTreeAsync(_repository, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref captureCount));
        Assert.Equal(1, provider.ActiveWorkingTreeFlightCount);

        releaseCapture.SetResult(new GitStagedSnapshot(_repository, [], []));
        await Task.WhenAll(first, second);

        Assert.Equal(0, provider.ActiveWorkingTreeFlightCount);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_CancelsCaptureAfterTheLastWaiterLeaves()
    {
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new GitStagedSnapshotProvider(
            maximumUnpushedSnapshotBytes: 64,
            workingTreeCaptureOverride: async (_, cancellationToken) =>
            {
                captureStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("The test capture should be cancelled.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    captureCancelled.TrySetResult();
                    throw;
                }
            });
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = provider.CaptureWorkingTreeAsync(_repository, firstCancellation.Token);
        await captureStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = provider.CaptureWorkingTreeAsync(_repository, secondCancellation.Token);

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        Assert.False(captureCancelled.Task.IsCompleted);

        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
        await captureCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, provider.ActiveWorkingTreeFlightCount);
    }

    [Fact]
    public async Task CaptureAsync_ReadsAgentInstructionsFromIndex()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "vc.rules.md"),
            "# Staged instructions\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "vc.rules.md");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "vc.rules.md"),
            "# Unstaged instructions\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var agent = Assert.Single(snapshot.AgentFiles);
        Assert.Equal("# Staged instructions\n", agent.Content);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_ReadsAgentRulesFromTheWorkingTree()
    {
        // An unstaged vc.rules.md edit and a brand-new untracked vc.rules.md both govern the
        // working-tree preview, even though the commit hooks keep reading the index.
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "vc.rules.md"),
            "# Staged instructions\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "vc.rules.md");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "vc.rules.md"),
            "# Unstaged instructions\n",
            TestContext.Current.CancellationToken);
        var nested = Path.Combine(_repository, "src");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(
            Path.Combine(nested, "vc.rules.md"),
            "# Untracked instructions\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
            _repository,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.AgentFiles.Count);
        var root = Assert.Single(snapshot.AgentFiles, file => file.RelativePath == "vc.rules.md");
        Assert.Equal("# Unstaged instructions\n", root.Content);
        var untracked = Assert.Single(snapshot.AgentFiles, file => file.RelativePath == "src/vc.rules.md");
        Assert.Equal("# Untracked instructions\n", untracked.Content);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_IncludesUnstagedAndUntrackedFiles()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class UnstagedOnly { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "untracked.cs"),
            "class Untracked { }\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
            _repository,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Files.Count);
        var modified = Assert.Single(snapshot.Files, file => file.RelativePath == "tracked.cs");
        Assert.Equal(GitStagedChangeKind.Modified, modified.ChangeKind);
        Assert.Equal("class UnstagedOnly { }\n", modified.Content);
        Assert.Equal("class Original { }\n", modified.PreviousContent);

        var untracked = Assert.Single(snapshot.Files, file => file.RelativePath == "untracked.cs");
        Assert.Equal(GitStagedChangeKind.Added, untracked.ChangeKind);
        Assert.True(untracked.ExistsInIndex);
        Assert.Equal("class Untracked { }\n", untracked.Content);
        Assert.Null(untracked.PreviousContent);
        Assert.DoesNotContain("untracked.cs", snapshot.TrackedFiles!);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_UsesLatestContentWhenIndexAlsoHasChanges()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class Staged { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class LatestWorkingTree { }\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var changed = Assert.Single(snapshot.Files);
        Assert.Equal("class LatestWorkingTree { }\n", changed.Content);
        Assert.Equal("class Original { }\n", changed.PreviousContent);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_AnalyzesAFileRecreatedAfterAStagedDeletion()
    {
        await GitAsync("rm", "tracked.cs");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class Recreated { }\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var changed = Assert.Single(snapshot.Files);
        Assert.Equal(GitStagedChangeKind.Modified, changed.ChangeKind);
        Assert.True(changed.ExistsInIndex);
        Assert.Equal("class Recreated { }\n", changed.Content);
        Assert.Equal("class Original { }\n", changed.PreviousContent);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_DoesNotFollowASymlinkOutOfTheRepository()
    {
        // The staged snapshot reads blobs from the object store, where a symlink is stored as
        // its link text. The working-tree snapshot reads the filesystem, where an unguarded
        // read would follow the link and hand the target's contents back to the client.
        var secretDirectory = Path.Combine(Path.GetTempPath(), $"git_preflight_secret_{Guid.NewGuid():N}");
        Directory.CreateDirectory(secretDirectory);
        var secret = Path.Combine(secretDirectory, "id_rsa");
        await File.WriteAllTextAsync(
            secret,
            "class TotallyPrivateKeyMaterial { }\n",
            TestContext.Current.CancellationToken);

        try
        {
            var link = Path.Combine(_repository, "leak.cs");
            try
            {
                File.CreateSymbolicLink(link, secret);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows needs Developer Mode or elevation to create symlinks.
                Assert.Skip("This platform does not permit creating symbolic links.");
                return;
            }

            var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
                _repository,
                TestContext.Current.CancellationToken);

            var leaked = snapshot.Files.SingleOrDefault(file => file.RelativePath == "leak.cs");
            if (leaked is not null)
            {
                Assert.Null(leaked.Content);
                Assert.False(leaked.ExistsInIndex);
            }

            Assert.DoesNotContain(
                snapshot.Files,
                file => file.Content?.Contains("TotallyPrivateKeyMaterial", StringComparison.Ordinal) == true);
        }
        finally
        {
            Directory.Delete(secretDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_DoesNotReadThroughAJunctionedDirectory()
    {
        // Git lists `linkdir/leak.cs` even though `linkdir` is a junction out of the
        // repository, and that file is not itself a reparse point — so guarding only the leaf
        // would still read it. Junctions also need no elevation, unlike symlinks.
        var secretDirectory = Path.Combine(Path.GetTempPath(), $"git_preflight_secret_{Guid.NewGuid():N}");
        Directory.CreateDirectory(secretDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(secretDirectory, "leak.cs"),
            "class TotallyPrivateKeyMaterial { }\n",
            TestContext.Current.CancellationToken);

        try
        {
            if (!TryCreateJunction(Path.Combine(_repository, "linkdir"), secretDirectory))
            {
                Assert.Skip("This platform does not support directory junctions.");
                return;
            }

            var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
                _repository,
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(
                snapshot.Files,
                file => file.Content?.Contains("TotallyPrivateKeyMaterial", StringComparison.Ordinal) == true);
        }
        finally
        {
            var link = Path.Combine(_repository, "linkdir");
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(secretDirectory, recursive: true);
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }

    [Fact]
    public async Task CaptureWorkingTreeAsync_ReadsUnstagedAndUntrackedChanges()
    {
        // The whole point of the working-tree snapshot: unlike CaptureAsync, it sees edits the
        // user has not staged, plus files Git does not track at all.
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "class Unstaged { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "untracked.cs"),
            "class Untracked { }\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureWorkingTreeAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var unstaged = Assert.Single(snapshot.Files, file => file.RelativePath == "tracked.cs");
        Assert.Equal("class Unstaged { }\n", unstaged.Content);
        Assert.Equal("class Original { }\n", unstaged.PreviousContent);

        var untracked = Assert.Single(snapshot.Files, file => file.RelativePath == "untracked.cs");
        Assert.Equal("class Untracked { }\n", untracked.Content);
        Assert.Equal(GitStagedChangeKind.Added, untracked.ChangeKind);
        Assert.Null(untracked.PreviousContent);
    }

    [Fact]
    public async Task CaptureUnpushedAsync_UsesHeadTreeAndBlobs_NotIndexOrWorkingTree()
    {
        await ConfigureUpstreamAtHeadAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "public class HeadVersion { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "head-caller.cs"),
            "public class HeadCaller { HeadVersion value; }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", ".");
        await GitAsync("commit", "-m", "unpushed head content");

        // Disturb both the index and working tree after the commit being scanned.
        await GitAsync("rm", "--cached", "tracked.cs");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "tracked.cs"),
            "public class WorkingTreeVersion { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "staged-only.cs"),
            "public class StagedOnly { HeadVersion value; }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "staged-only.cs");

        var snapshot = await new GitStagedSnapshotProvider().CaptureUnpushedAsync(
            _repository,
            TestContext.Current.CancellationToken);

        Assert.Contains("tracked.cs", snapshot.TrackedFiles!);
        Assert.Contains("head-caller.cs", snapshot.TrackedFiles!);
        Assert.DoesNotContain("staged-only.cs", snapshot.TrackedFiles!);
        var changed = Assert.Single(snapshot.Files, file => file.RelativePath == "tracked.cs");
        Assert.Equal("public class HeadVersion { }\n", changed.Content);
        Assert.DoesNotContain("WorkingTreeVersion", changed.Content);
        Assert.Contains(snapshot.ImpactFiles!, file =>
            file.RelativePath == "head-caller.cs" && file.Content.Contains("HeadVersion", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.ImpactFiles!, file => file.RelativePath == "staged-only.cs");
    }

    [Fact]
    public async Task CaptureUnpushedAsync_RejectsOversizedHeadBlobBeforeReadingIt()
    {
        await ConfigureUpstreamAtHeadAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "oversized.cs"),
            new string('a', (5 * 1024 * 1024) + 1),
            TestContext.Current.CancellationToken);
        await GitAsync("add", "oversized.cs");
        await GitAsync("commit", "-m", "large committed source");

        var snapshot = await new GitStagedSnapshotProvider().CaptureUnpushedAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var oversized = Assert.Single(snapshot.Files, file => file.RelativePath == "oversized.cs");
        Assert.True(oversized.IsBinary);
        Assert.Null(oversized.Content);
        Assert.DoesNotContain(snapshot.ImpactFiles!, file => file.RelativePath == "oversized.cs");
    }

    [Fact]
    public async Task CaptureUnpushedAsync_EnforcesAggregateBlobBudget()
    {
        await ConfigureUpstreamAtHeadAsync();
        await GitAsync("rm", "vc.rules.md");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "first.cs"),
            "public class FirstHeadSource { }\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "second.cs"),
            "public class SecondHeadSource { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", ".");
        await GitAsync("commit", "-m", "two unpushed sources");

        var snapshot = await new GitStagedSnapshotProvider(maximumUnpushedSnapshotBytes: 40)
            .CaptureUnpushedAsync(_repository, TestContext.Current.CancellationToken);

        Assert.Single(snapshot.Files, file => file.Content is not null);
        Assert.Single(snapshot.ImpactFiles!);
        Assert.True(snapshot.ImpactFiles!.Sum(file => Encoding.UTF8.GetByteCount(file.Content)) <= 40);
    }

    [Fact]
    public async Task RunBinaryGitAsync_TimeoutDrainsStdoutCopyBeforeReturningBytes()
    {
        var result = await GitStagedSnapshotProvider.RunBinaryGitForTestAsync(
            _repository,
            ["-c", "alias.vr-timeout=!printf pre-timeout; sleep 5", "vr-timeout"],
            TimeSpan.FromMilliseconds(150),
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Equal("pre-timeout", Encoding.UTF8.GetString(result.Bytes));
    }

    private async Task ConfigureUpstreamAtHeadAsync()
    {
        await GitAsync("branch", "upstream-base", "HEAD");
        await GitAsync("branch", "--set-upstream-to=upstream-base");
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
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {output}\n{error}");
    }
}
