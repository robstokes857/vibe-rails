using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// A commit's Git Guard window opens only when the check fails (VIBE-374). A passing commit used to
/// flash a console open and shut on every commit, with nothing in it to read or answer. The real hook
/// host runs against a throwaway repository; the window is replaced, so none ever opens here.
/// </summary>
public sealed class GitGuardWindowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "git-guard-window-" + Guid.NewGuid().ToString("N"));
    private readonly List<IReadOnlyList<string>> _windows = [];
    private int? _windowResult = 1;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await GitAsync("init", "-q");
        await GitAsync("config", "user.email", "test@example.invalid");
        await GitAsync("config", "user.name", "Test");
        await GitAsync("config", "core.hooksPath", ".git/hooks");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ACleanPassOpensNoWindowAndSaysSoInOneLine()
    {
        await StageAsync(documented: true);

        var (code, output) = await HookAsync("pre-commit", "--enqueue-automations", "--prompt-acknowledgment", "--console-window");

        Assert.Equal(0, code);
        Assert.Empty(_windows);
        Assert.Equal(VcaHookProcessHost.PassedLine(VcaHookKind.PreCommit) + Environment.NewLine, output);
    }

    [Fact]
    public async Task APassWithAWarningHandsGitTheWholeTranscript()
    {
        // A WARN finding never blocks, so no window opens; the person still gets to read it in Git's output.
        await StageAsync(documented: false, level: "WARN");

        var (code, output) = await HookAsync("pre-commit", "--enqueue-automations", "--prompt-acknowledgment", "--console-window");

        Assert.Equal(0, code);
        Assert.Empty(_windows);
        Assert.Contains("[WARN] Log all file changes", output);
        Assert.Contains("[pass] Commit allowed", output);
        Assert.DoesNotContain("popup", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABlockedCommitOpensTheWindowWithTheHookArgumentsAndReturnsItsResult()
    {
        await StageAsync(documented: false);
        string[] args = ["--vca-hook", "pre-commit", "--enqueue-automations", "--workdir", _root, "--prompt-acknowledgment", "--console-window"];
        _windowResult = 7;

        var (code, output) = await RunAsync(args);

        Assert.Equal(7, code);
        Assert.Equal(args, Assert.Single(_windows));
        // The window checks again and shows its own transcript. The first one would say nobody could be asked.
        Assert.DoesNotContain("[STOP] Log all file changes", output);
    }

    [Fact]
    public async Task ACommitMessageCheckFollowsTheSameRule()
    {
        await StageAsync(documented: true);
        var message = Path.Combine(_root, ".git", "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(message, "Add the app\n", Ct);

        var (code, output) = await HookAsync("commit-msg", "--enqueue-automations", "--commit-message", message, "--prompt-acknowledgment", "--console-window");

        Assert.Equal(0, code);
        Assert.Empty(_windows);
        Assert.Equal(VcaHookProcessHost.PassedLine(VcaHookKind.CommitMessage) + Environment.NewLine, output);
    }

    [Fact]
    public async Task WithoutAWindowTheFirstCheckIsTheResult()
    {
        if (VcaHookProcessLaunch.CanPromptInCurrentConsole())
            Assert.Skip("This test host has an interactive console, where the hook prompts in place instead.");
        await StageAsync(documented: false);
        _windowResult = null;

        var (code, output) = await HookAsync("pre-commit", "--enqueue-automations", "--prompt-acknowledgment", "--console-window");

        Assert.Equal(1, code);
        Assert.Single(_windows);
        Assert.Contains("[STOP] Log all file changes", output);
        Assert.Contains("[block] Commit blocked", output);
    }

    [Fact]
    public async Task TheRulesPageConsoleStillOpensTheWindowForAPass()
    {
        // VcaHookConsoleLauncher asks for the window to show the checks; it never queues Automations.
        await StageAsync(documented: true);
        _windowResult = 0;

        var (code, output) = await HookAsync("pre-commit", "--console-window");

        Assert.Equal(0, code);
        Assert.Single(_windows);
        Assert.DoesNotContain("[pass] Commit allowed", output);
    }

    private async Task StageAsync(bool documented, string level = "STOP")
    {
        var files = documented ? "\n## Files\n- vc.rules.md\n- src/app.cs\n" : "\n";
        await File.WriteAllTextAsync(Path.Combine(_root, "vc.rules.md"),
            $"# Agent Instructions\n## Vibe Rails Rules\n- Log all file changes ({level})\n" + files, Ct);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.cs"), "class App { }\n", Ct);
        await GitAsync("add", "vc.rules.md", "src/app.cs");
    }

    private Task<(int Code, string Output)> HookAsync(string kind, params string[] extra) =>
        RunAsync(["--vca-hook", kind, "--workdir", _root, .. extra]);

    private async Task<(int Code, string Output)> RunAsync(string[] args)
    {
        var launches = new Mock<ILaunchStore>();
        launches.Setup(x => x.ListBindingsAsync(It.IsAny<string?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LaunchTriggerBinding>());
        using var output = new StringWriter();
        var code = await VcaHookProcessHost.RunCoreAsync(
            args,
            services =>
            {
                services.AddSingleton(new LaunchEventService(launches.Object, new(launches.Object, Mock.Of<IAutomationScriptService>())));
                services.AddSingleton(Mock.Of<IBoardStore>());
                services.AddSingleton(Mock.Of<VibeRails.DB.IJobStore>(MockBehavior.Strict));
                services.AddSingleton(Mock.Of<VibeRails.DB.IReviewRequestStore>(MockBehavior.Strict));
            },
            output,
            output,
            TextReader.Null,
            Ct,
            (windowArgs, _, _, _) =>
            {
                lock (_windows) _windows.Add(windowArgs.ToArray());
                return Task.FromResult(_windowResult);
            });
        return (code, output.ToString());
    }

    private async Task GitAsync(params string[] args)
    {
        var result = await GitCli.RunAsync(_root, args, Ct);
        Assert.True(result.Succeeded, result.StdErr);
    }
}
