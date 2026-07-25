using System.Diagnostics;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public sealed class VcaHookEndToEndTests : IAsyncLifetime
{
    private readonly string _repositoryPath = Path.Combine(
        Path.GetTempPath(),
        $"vca_hook_e2e_{Guid.NewGuid():N}");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_repositoryPath);
        await RunGitAsync("init");
        await RunGitAsync("config", "user.email", "vca-tests@example.invalid");
        await RunGitAsync("config", "user.name", "VCA Tests");
        await RunGitAsync("config", "core.hooksPath", ".git/hooks");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_repositoryPath))
        {
            var fullPath = Path.GetFullPath(_repositoryPath);
            var tempPath = Path.GetFullPath(Path.GetTempPath());
            if (!fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to clean test repository outside temp: {fullPath}");
            }

            foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            foreach (var directory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(directory, FileAttributes.Normal);
            }
            Directory.Delete(_repositoryPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PreCommit_BlocksStopViolation_FromRealStagedFiles()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Log all file changes (STOP)
            """);
        await WriteAsync("src/app.cs", "class App { }\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.cs");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[STOP] Log all file changes", result.Output);
        Assert.Contains("[block] Commit blocked", result.Output);
        Assert.Contains("MintLint code health", result.Output);
        Assert.Contains("Automated workflows", result.Output);
    }

    [Fact]
    public async Task PreCommit_PassesWhenDocumentedFilesSatisfyRule()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Log all file changes (STOP)
            ## Files
            - AGENTS.md
            - src/app.cs
            """);
        await WriteAsync("src/app.cs", "class App { }\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.cs");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS: All VCA rules satisfied", result.Output);
        Assert.Contains("[pass] Commit allowed", result.Output);
        Assert.Contains("[2/3]", result.Output);
    }

    [Fact]
    public async Task NestedStopRule_DoesNotApplyOutsideItsDirectory()
    {
        await WriteAsync("restricted/AGENTS.md", """
            # Restricted Agent Instructions
            ## Vibe Rails Rules
            - Log all file changes (STOP)
            """);
        await RunGitAsync("add", "restricted/AGENTS.md");
        await RunGitAsync("commit", "-m", "Add nested policy");
        await WriteAsync("outside.txt", "outside change\n");
        await RunGitAsync("add", "outside.txt");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0 applicable rule(s)", result.Output);
    }

    [Fact]
    public async Task PreCommit_UsesStagedAgentRulesAndFileContent_NotUnstagedWorkingTree()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Cyclomatic complexity < 2 (STOP)
            """);
        await WriteAsync("src/app.cs", "class App { void Run() { if (true) { } if (false) { } } }\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.cs");

        // These working-tree edits must not change what the pre-commit hook validates.
        await WriteAsync("AGENTS.md", "# Agent Instructions\n");
        await WriteAsync("src/app.cs", "class App { }\n");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[STOP] Cyclomatic complexity < 2", result.Output);
        Assert.Contains("estimated complexity", result.Output);
    }

    [Fact]
    public async Task NestedFilesEntries_AreRelativeToNestedAgentDirectory()
    {
        await WriteAsync("nested/AGENTS.md", """
            # Nested Agent Instructions
            ## Vibe Rails Rules
            - Log all file changes (STOP)
            ## Files
            - AGENTS.md
            - app.cs
            """);
        await WriteAsync("nested/app.cs", "class App { }\n");
        await RunGitAsync("add", "nested/AGENTS.md", "nested/app.cs");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS: All VCA rules satisfied", result.Output);
    }

    [Fact]
    public async Task ChangedLinesRule_UsesStagedDelta_NotWorkingTreeLengthOrUnstagedEdits()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Log file changes > 5 lines (STOP)
            ## Files
            - AGENTS.md
            """);
        await WriteAsync("src/app.cs", string.Join('\n', Enumerable.Range(1, 10).Select(i => $"// line {i}")) + "\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.cs");
        await RunGitAsync("commit", "-m", "Add baseline");

        await WriteAsync("src/app.cs", "// staged change\n" + string.Join('\n', Enumerable.Range(2, 9).Select(i => $"// line {i}")) + "\n");
        await RunGitAsync("add", "src/app.cs");
        await File.AppendAllTextAsync(
            Path.Combine(_repositoryPath, "src/app.cs"),
            string.Join('\n', Enumerable.Range(1, 20).Select(i => $"// unstaged {i}")) + "\n",
            TestContext.Current.CancellationToken);

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS: All VCA rules satisfied", result.Output);
    }

    [Fact]
    public async Task CoveragePercentageRule_IsExplicitlyUnsupportedAndDoesNotSilentlyPass()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Require test coverage minimum 80% (STOP)
            """);
        await WriteAsync("src/app.go", "package main\n");
        await WriteAsync("tests/app_test.go", "package tests\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.go", "tests/app_test.go");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("UNSUPPORTED: the Git hook has no coverage report", result.Output);
    }

    [Fact]
    public async Task CoveragePercentageRule_IsNotApplicableWhenOnlyTestsAreStaged()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Require test coverage minimum 80% (STOP)
            """);
        await WriteAsync("tests/app.test.cs", "class AppTests { }\n");
        await RunGitAsync("add", "AGENTS.md", "tests/app.test.cs");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS: All VCA rules satisfied", result.Output);
    }

    [Fact]
    public async Task CommitMessageRule_DefersAtPreCommitAndRunsAtCommitMessage()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Check commit message for: wip, temporary (STOP)
            """);
        await WriteAsync("src/app.cs", "class App { }\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.cs");

        var preCommit = await RunHookAsync("pre-commit");
        Assert.Equal(0, preCommit.ExitCode);
        Assert.Contains("[DEFERRED] Check commit message for", preCommit.Output);

        var messagePath = Path.Combine(_repositoryPath, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(
            messagePath,
            "WIP: add app\n",
            TestContext.Current.CancellationToken);
        var blocked = await RunHookAsync("commit-msg", messagePath);
        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains("Commit message contains forbidden words: wip", blocked.Output);

        await File.WriteAllTextAsync(
            messagePath,
            "Add app\n",
            TestContext.Current.CancellationToken);
        var passed = await RunHookAsync("commit-msg", messagePath);
        Assert.Equal(0, passed.ExitCode);
        Assert.Contains("No VCA commit acknowledgments were required", passed.Output);
    }

    [Fact]
    public async Task CommitMessageRule_RunsForMessageOnlyCommitWithNoStagedDiff()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Check commit message for: wip (STOP)
            """);
        await WriteAsync("src/app.cs", "class App { }\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.cs");
        await RunGitAsync("commit", "-m", "Add baseline");

        var messagePath = Path.Combine(_repositoryPath, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(
            messagePath,
            "WIP: amend message only\n",
            TestContext.Current.CancellationToken);

        var result = await RunHookAsync("commit-msg", messagePath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Commit message contains forbidden words: wip", result.Output);
        Assert.Contains("Validated 0 staged file(s)", result.Output);
    }

    [Fact]
    public async Task StagedSpecialFilename_IsParsedAsOneGitPath()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Cyclomatic complexity disabled (WARN)
            """);
        var specialPath = OperatingSystem.IsWindows()
            ? "src/quoted ü file.cs"
            : "src/line\nbreak.cs";
        await WriteAsync(specialPath, "class App { }\n");
        await RunGitAsync("add", "AGENTS.md", specialPath);

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Validated 2 staged file(s)", result.Output);
    }

    [Fact]
    public async Task CommitViolation_RequiresTokenAndNonEmptyReason()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Log all file changes (COMMIT)
            """);
        await WriteAsync("src/app.cs", "class App { }\n");
        await RunGitAsync("add", "AGENTS.md", "src/app.cs");

        var preCommit = await RunHookAsync("pre-commit");
        Assert.Equal(0, preCommit.ExitCode);
        Assert.Contains("VCA requires commit-message acknowledgment", preCommit.Output);

        const string token = "[VCA:AGENTS.md:log-all-file-changes]";
        var messagePath = Path.Combine(_repositoryPath, "COMMIT_EDITMSG");
        await RunGitAsync("config", "core.commentChar", ";");
        await File.WriteAllTextAsync(
            messagePath,
            $"Test commit\n\n; {token} Reason: Git will discard this commented token\n",
            TestContext.Current.CancellationToken);

        var commentedToken = await RunHookAsync("commit-msg", messagePath);
        Assert.Equal(1, commentedToken.ExitCode);
        Assert.Contains("missing required VCA acknowledgment", commentedToken.Output);

        await File.WriteAllTextAsync(
            messagePath,
            $"Test commit\n\n{token}\n",
            TestContext.Current.CancellationToken);

        var missingReason = await RunHookAsync("commit-msg", messagePath);
        Assert.Equal(1, missingReason.ExitCode);
        Assert.Contains("missing required VCA acknowledgment", missingReason.Output);
        Assert.DoesNotContain("[pass] Commit allowed", missingReason.Output);
        Assert.DoesNotContain("Queued", missingReason.Output, StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(
            messagePath,
            $"Test commit\n\n{token} Reason: reviewed for this change\n",
            TestContext.Current.CancellationToken);

        var acknowledged = await RunHookAsync("commit-msg", messagePath);
        Assert.Equal(0, acknowledged.ExitCode);
        Assert.Contains("acknowledgments found", acknowledged.Output);
    }

    [Fact]
    public async Task PackageRule_BlocksStagedDependencyFile()
    {
        await WriteAsync("AGENTS.md", """
            # Agent Instructions
            ## Vibe Rails Rules
            - Package file changes (STOP)
            """);
        await WriteAsync("package.json", "{}\n");
        await RunGitAsync("add", "AGENTS.md", "package.json");

        var result = await RunHookAsync("pre-commit");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("package file(s) changed", result.Output);
    }

    private async Task<(int ExitCode, string Output)> RunHookAsync(
        string kind,
        string? commitMessagePath = null)
    {
        using var transcript = new StringWriter();
        var args = new List<string>
        {
            "--vca-hook",
            kind,
            "--workdir",
            _repositoryPath
        };
        if (commitMessagePath != null)
        {
            args.Add("--commit-message");
            args.Add(commitMessagePath);
        }

        var exitCode = await VcaHookProcessHost.RunAsync(
            args.ToArray(),
            transcript,
            transcript,
            cancellationToken: TestContext.Current.CancellationToken);
        return (exitCode, transcript.ToString());
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        var path = Path.Combine(_repositoryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
    }

    private async Task RunGitAsync(params string[] arguments)
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
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed ({process.ExitCode}).\n{output}\n{error}");
    }
}
