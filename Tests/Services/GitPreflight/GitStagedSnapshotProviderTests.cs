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
