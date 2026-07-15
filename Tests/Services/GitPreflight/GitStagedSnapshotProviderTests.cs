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
            Path.Combine(_repository, "AGENTS.md"),
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
        // The committed version rides along so scoring can tell new concern from old debt.
        Assert.Equal("class Original { }\n", staged.PreviousContent);
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
    public async Task CaptureAsync_ReadsAgentInstructionsFromIndex()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "AGENTS.md"),
            "# Staged instructions\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "AGENTS.md");
        await File.WriteAllTextAsync(
            Path.Combine(_repository, "AGENTS.md"),
            "# Unstaged instructions\n",
            TestContext.Current.CancellationToken);

        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _repository,
            TestContext.Current.CancellationToken);

        var agent = Assert.Single(snapshot.AgentFiles);
        Assert.Equal("# Staged instructions\n", agent.Content);
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

    private async Task GitAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _repository,
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
