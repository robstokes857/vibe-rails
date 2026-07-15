using System.Diagnostics;
using VibeRails.Services;
using Xunit;

namespace Tests.Services;

public sealed class GitServiceTests : IDisposable
{
    private readonly string _repositoryPath = Path.Combine(
        Path.GetTempPath(),
        $"GitServiceTests_{Guid.NewGuid():N}");

    [Fact]
    public async Task StageFileAsync_StagesOnlyTheRequestedFile()
    {
        Directory.CreateDirectory(_repositoryPath);
        await RunGitAsync("init");

        var agentPath = Path.Combine(_repositoryPath, "AGENTS.md");
        await File.WriteAllTextAsync(
            agentPath,
            "## Vibe Rails Rules\n- Log all file changes (STOP)\n",
            TestContext.Current.CancellationToken);
        await RunGitAsync("add", "--", "AGENTS.md");

        await File.WriteAllTextAsync(
            agentPath,
            "## Vibe Rails Rules\n",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repositoryPath, "unrelated.txt"),
            "leave unstaged",
            TestContext.Current.CancellationToken);

        var service = new GitService(_repositoryPath);
        await service.StageFileAsync(agentPath, TestContext.Current.CancellationToken);

        var stagedFiles = await RunGitAsync("diff", "--cached", "--name-only");
        var stagedAgent = await RunGitAsync("show", ":AGENTS.md");
        var status = await RunGitAsync("status", "--short");

        Assert.Equal("AGENTS.md", stagedFiles.Trim());
        Assert.Equal("## Vibe Rails Rules", stagedAgent.Trim());
        Assert.Contains("?? unrelated.txt", status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryPath))
        {
            foreach (var file in Directory.EnumerateFiles(_repositoryPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repositoryPath, recursive: true);
        }
    }

    private async Task<string> RunGitAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {await stderr}");
        return await stdout;
    }
}
