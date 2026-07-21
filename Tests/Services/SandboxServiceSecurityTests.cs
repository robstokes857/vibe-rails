using Moq;
using System.Runtime.InteropServices;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.Services;

public sealed class SandboxServiceSecurityTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails_sandbox_security_{Guid.NewGuid():N}");
    private readonly string _sandboxRoot;

    public SandboxServiceSecurityTests()
    {
        _sandboxRoot = Path.Combine(_testRoot, "sandboxes");
        Directory.CreateDirectory(_sandboxRoot);
    }

    [Fact]
    public async Task DeleteSandboxAsync_RejectsPathOutsideConfiguredRoot()
    {
        var outsidePath = Path.Combine(_testRoot, "outside");
        Directory.CreateDirectory(outsidePath);
        var markerPath = Path.Combine(outsidePath, "keep.txt");
        await File.WriteAllTextAsync(markerPath, "keep", TestContext.Current.CancellationToken);

        var repository = RepositoryReturning(new Sandbox { Id = 7, Path = outsidePath });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSandboxAsync(7, TestContext.Current.CancellationToken));

        Assert.Contains("outside the configured sandbox directory", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(markerPath));
        repository.Verify(
            item => item.DeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteSandboxAsync_RejectsConfiguredRootItself()
    {
        var markerPath = Path.Combine(_sandboxRoot, "keep.txt");
        await File.WriteAllTextAsync(markerPath, "keep", TestContext.Current.CancellationToken);

        var repository = RepositoryReturning(new Sandbox { Id = 8, Path = _sandboxRoot });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSandboxAsync(8, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(markerPath));
        repository.Verify(
            item => item.DeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteSandboxAsync_DeletesContainedDirectoryAndRecord()
    {
        var sandboxPath = Path.Combine(_sandboxRoot, "review");
        Directory.CreateDirectory(sandboxPath);
        await File.WriteAllTextAsync(
            Path.Combine(sandboxPath, "delete.txt"),
            "delete",
            TestContext.Current.CancellationToken);

        var repository = RepositoryReturning(new Sandbox { Id = 9, Path = sandboxPath });
        repository
            .Setup(item => item.DeleteSandboxAsync(9, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new SandboxService(repository.Object, _sandboxRoot);

        await service.DeleteSandboxAsync(9, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(sandboxPath));
        repository.Verify(
            item => item.DeleteSandboxAsync(9, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteSandboxAsync_RejectsSymlinkedSandboxDirectory()
    {
        var outsidePath = Path.Combine(_testRoot, "delete-target");
        Directory.CreateDirectory(outsidePath);
        var markerPath = Path.Combine(outsidePath, "keep.txt");
        await File.WriteAllTextAsync(markerPath, "keep", TestContext.Current.CancellationToken);
        var sandboxPath = Path.Combine(_sandboxRoot, "linked");
        try
        {
            Directory.CreateSymbolicLink(sandboxPath, outsidePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This platform does not permit creating directory symbolic links.");
            return;
        }

        try
        {
            var repository = RepositoryReturning(new Sandbox { Id = 16, Path = sandboxPath });
            var service = new SandboxService(repository.Object, _sandboxRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteSandboxAsync(16, TestContext.Current.CancellationToken));

            Assert.True(File.Exists(markerPath));
            repository.Verify(
                item => item.DeleteSandboxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(sandboxPath))
                Directory.Delete(sandboxPath);
        }
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("nested/../../secret.txt")]
    public void ResolveDiffFilePath_RejectsTraversal(string relativePath)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SandboxService.ResolveDiffFilePath(_sandboxRoot, relativePath));
    }

    [Fact]
    public void ResolveDiffFilePath_RejectsRootedPath()
    {
        var rootedPath = Path.Combine(Path.GetPathRoot(_sandboxRoot)!, "secret.txt");

        Assert.Throws<InvalidOperationException>(() =>
            SandboxService.ResolveDiffFilePath(_sandboxRoot, rootedPath));
    }

    [Fact]
    public void ResolveDiffFilePath_ResolvesContainedPath()
    {
        var result = SandboxService.ResolveDiffFilePath(_sandboxRoot, "nested/file.txt");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_sandboxRoot, "nested", "file.txt")),
            result);
    }

    [Fact]
    public async Task PushToRemoteAsync_RejectsOptionLikePersistedBranch()
    {
        var sandboxPath = Path.Combine(_sandboxRoot, "review");
        Directory.CreateDirectory(sandboxPath);
        var repository = RepositoryReturning(new Sandbox
        {
            Id = 10,
            Path = sandboxPath,
            Branch = "--force",
            RemoteUrl = "https://example.invalid/repository.git"
        });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PushToRemoteAsync(10, TestContext.Current.CancellationToken));

        Assert.Contains("branch is invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiffAsync_DoesNotReadOversizedWorkingTreeFile()
    {
        var sandboxPath = Path.Combine(_sandboxRoot, "large-diff");
        Directory.CreateDirectory(sandboxPath);
        await RunGitAsync(sandboxPath, "init");
        var largePath = Path.Combine(sandboxPath, "large.txt");
        await using (var stream = new FileStream(largePath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength((5 * 1024 * 1024) + 1);
        }

        var repository = RepositoryReturning(new Sandbox
        {
            Id = 11,
            Path = sandboxPath,
            Branch = "large-diff"
        });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var result = await service.GetDiffAsync(11, TestContext.Current.CancellationToken);

        var file = Assert.Single(result.Files);
        Assert.Equal("large.txt", file.FileName);
        Assert.Empty(file.ModifiedContent);
    }

    [Fact]
    public async Task GetDiffAsync_ReadsContainedOptionLikeRegularFile()
    {
        var sandboxPath = Path.Combine(_sandboxRoot, "regular-diff");
        Directory.CreateDirectory(sandboxPath);
        await RunGitAsync(sandboxPath, "init", "-b", "regular-diff");
        await ConfigureGitIdentityAsync(sandboxPath);
        const string fileName = "--dangerous.txt";
        await File.WriteAllTextAsync(
            Path.Combine(sandboxPath, fileName),
            "before",
            TestContext.Current.CancellationToken);
        await RunGitAsync(sandboxPath, "add", "--", fileName);
        await RunGitAsync(sandboxPath, "commit", "-m", "Initial commit");
        var commitHash = await RunGitAsync(sandboxPath, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(
            Path.Combine(sandboxPath, fileName),
            "after",
            TestContext.Current.CancellationToken);

        var repository = RepositoryReturning(new Sandbox
        {
            Id = 12,
            Path = sandboxPath,
            Branch = "regular-diff",
            CommitHash = commitHash
        });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var result = await service.GetDiffAsync(12, TestContext.Current.CancellationToken);

        var file = Assert.Single(result.Files);
        Assert.Equal(fileName, file.FileName);
        Assert.Equal("before", file.OriginalContent);
        Assert.Equal("after", file.ModifiedContent);
    }

    [Fact]
    public async Task GetDiffAsync_DoesNotReadUnixFifo()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows named pipes cannot be created as files inside the test repository.");
            return;
        }

        var sandboxPath = Path.Combine(_sandboxRoot, "fifo-diff");
        Directory.CreateDirectory(sandboxPath);
        await RunGitAsync(sandboxPath, "init", "-b", "fifo-diff");
        await ConfigureGitIdentityAsync(sandboxPath);
        const string fileName = "pipe.txt";
        var fifoPath = Path.Combine(sandboxPath, fileName);
        await File.WriteAllTextAsync(fifoPath, "before", TestContext.Current.CancellationToken);
        await RunGitAsync(sandboxPath, "add", "--", fileName);
        await RunGitAsync(sandboxPath, "commit", "-m", "Initial commit");
        var commitHash = await RunGitAsync(sandboxPath, "rev-parse", "HEAD");
        File.Delete(fifoPath);
        if (MkFifo(fifoPath, Convert.ToUInt32("600", 8)) != 0)
        {
            Assert.Skip($"mkfifo failed with errno {Marshal.GetLastPInvokeError()}.");
            return;
        }

        Assert.False(await SandboxService.GitConfirmsRegularFileAsync(
            sandboxPath,
            fifoPath,
            TestContext.Current.CancellationToken));

        var repository = RepositoryReturning(new Sandbox
        {
            Id = 13,
            Path = sandboxPath,
            Branch = "fifo-diff",
            CommitHash = commitHash
        });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var result = await service.GetDiffAsync(13, TestContext.Current.CancellationToken);

        var file = Assert.Single(result.Files);
        Assert.Equal(fileName, file.FileName);
        Assert.Equal("before", file.OriginalContent);
        Assert.Empty(file.ModifiedContent);
    }

    [Fact]
    public async Task GetDiffAsync_DoesNotFollowSymlinkOutsideSandbox()
    {
        var sandboxPath = Path.Combine(_sandboxRoot, "symlink-diff");
        Directory.CreateDirectory(sandboxPath);
        await RunGitAsync(sandboxPath, "init");
        var outsidePath = Path.Combine(_testRoot, "secret.txt");
        const string secret = "totally-private-content";
        await File.WriteAllTextAsync(outsidePath, secret, TestContext.Current.CancellationToken);
        var linkPath = Path.Combine(sandboxPath, "leak.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, outsidePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This platform does not permit creating file symbolic links.");
            return;
        }

        try
        {
            var repository = RepositoryReturning(new Sandbox
            {
                Id = 17,
                Path = sandboxPath,
                Branch = "symlink-diff"
            });
            var service = new SandboxService(repository.Object, _sandboxRoot);

            var result = await service.GetDiffAsync(17, TestContext.Current.CancellationToken);

            var file = Assert.Single(result.Files);
            Assert.Equal("leak.txt", file.FileName);
            Assert.Empty(file.ModifiedContent);
            Assert.DoesNotContain(secret, file.OriginalContent, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(linkPath))
                File.Delete(linkPath);
        }
    }

    [Fact]
    public async Task CreateSandboxAsync_PreservesQuotedRemoteUrlAsSingleArgument()
    {
        var sourcePath = Path.Combine(_testRoot, "source");
        Directory.CreateDirectory(sourcePath);
        await RunGitAsync(sourcePath, "init", "-b", "main");
        await ConfigureGitIdentityAsync(sourcePath);
        await File.WriteAllTextAsync(
            Path.Combine(sourcePath, "tracked.txt"),
            "tracked",
            TestContext.Current.CancellationToken);
        await RunGitAsync(sourcePath, "add", "--", "tracked.txt");
        await RunGitAsync(sourcePath, "commit", "-m", "Initial commit");

        const string remoteUrl = "https://example.invalid/repository\"quoted.git";
        await RunGitAsync(sourcePath, "remote", "add", "origin", remoteUrl);

        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(item => item.GetSandboxByNameAndProjectAsync(
                "review",
                sourcePath,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Sandbox?)null);
        repository
            .Setup(item => item.SaveSandboxAsync(It.IsAny<Sandbox>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Sandbox sandbox, CancellationToken _) => sandbox);
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var sandbox = await service.CreateSandboxAsync(
            "review",
            sourcePath,
            TestContext.Current.CancellationToken);

        Assert.Equal(remoteUrl, sandbox.RemoteUrl);
        Assert.Equal(
            remoteUrl,
            await RunGitAsync(sandbox.Path, "remote", "get-url", "origin"));
        Assert.Equal("review", await RunGitAsync(sandbox.Path, "branch", "--show-current"));
    }

    [Fact]
    public async Task MergeLocallyAsync_UsesValidOptionSeparators()
    {
        var sourcePath = Path.Combine(_testRoot, "merge-source");
        Directory.CreateDirectory(sourcePath);
        await RunGitAsync(sourcePath, "init", "-b", "main");
        await ConfigureGitIdentityAsync(sourcePath);
        var sourceFile = Path.Combine(sourcePath, "tracked.txt");
        await File.WriteAllTextAsync(sourceFile, "before", TestContext.Current.CancellationToken);
        await RunGitAsync(sourcePath, "add", "--", "tracked.txt");
        await RunGitAsync(sourcePath, "commit", "-m", "Initial commit");

        var sandboxPath = Path.Combine(_sandboxRoot, "merge-review");
        await RunGitAsync(_sandboxRoot, "clone", "--", sourcePath, sandboxPath);
        await RunGitAsync(sandboxPath, "checkout", "-b", "review");
        await ConfigureGitIdentityAsync(sandboxPath);
        await File.WriteAllTextAsync(
            Path.Combine(sandboxPath, "tracked.txt"),
            "after",
            TestContext.Current.CancellationToken);

        var repository = RepositoryReturning(new Sandbox
        {
            Id = 14,
            Name = "review",
            Path = sandboxPath,
            ProjectPath = sourcePath,
            Branch = "review",
            SourceBranch = "main"
        });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var message = await service.MergeLocallyAsync(14, TestContext.Current.CancellationToken);

        Assert.Contains("merged", message, StringComparison.Ordinal);
        Assert.Equal(
            "after",
            await File.ReadAllTextAsync(sourceFile, TestContext.Current.CancellationToken));
        var remotes = await RunGitAsync(sourcePath, "remote");
        Assert.DoesNotContain("sandbox-review", remotes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushToRemoteAsync_UsesValidOptionSeparator()
    {
        var remotePath = Path.Combine(_testRoot, "remote.git");
        Directory.CreateDirectory(remotePath);
        await RunGitAsync(remotePath, "init", "--bare");

        var sandboxPath = Path.Combine(_sandboxRoot, "push-review");
        Directory.CreateDirectory(sandboxPath);
        await RunGitAsync(sandboxPath, "init", "-b", "review");
        await ConfigureGitIdentityAsync(sandboxPath);
        await File.WriteAllTextAsync(
            Path.Combine(sandboxPath, "tracked.txt"),
            "content",
            TestContext.Current.CancellationToken);
        await RunGitAsync(sandboxPath, "add", "--", "tracked.txt");
        await RunGitAsync(sandboxPath, "commit", "-m", "Initial commit");
        await RunGitAsync(sandboxPath, "remote", "add", "origin", remotePath);

        var repository = RepositoryReturning(new Sandbox
        {
            Id = 15,
            Name = "review",
            Path = sandboxPath,
            Branch = "review",
            RemoteUrl = remotePath
        });
        var service = new SandboxService(repository.Object, _sandboxRoot);

        var message = await service.PushToRemoteAsync(15, TestContext.Current.CancellationToken);

        Assert.Contains("pushed", message, StringComparison.Ordinal);
        await RunGitAsync(remotePath, "show-ref", "--verify", "refs/heads/review");
    }

    private static Mock<IRepository> RepositoryReturning(Sandbox sandbox)
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(item => item.GetSandboxByIdAsync(sandbox.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sandbox);
        return repository;
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await GitProcessRunner.RunAsync(
            arguments,
            workingDirectory,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
        return result.StdOut;
    }

    private static async Task ConfigureGitIdentityAsync(string workingDirectory)
    {
        await RunGitAsync(workingDirectory, "config", "user.email", "sandbox-tests@example.invalid");
        await RunGitAsync(workingDirectory, "config", "user.name", "Sandbox Tests");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    public void Dispose()
    {
        if (!Directory.Exists(_testRoot))
            return;

        foreach (var file in Directory.EnumerateFiles(_testRoot, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(_testRoot, recursive: true);
    }
}
