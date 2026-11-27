using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// Runs the real commit-msg hook script with a stub <c>vb</c> that records each launch (VIBE-327).
/// Message cleanup is its own launch only when a chained hook has to see the cleaned message;
/// otherwise the validation launch cleans it, saving one VibeRails start on every commit.
/// </summary>
public sealed class CommitMsgHookScriptTests : IDisposable
{
    private readonly HookScriptHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task WithoutAChainedHookTheValidationLaunchCleansTheMessage()
    {
        var launches = await RunHookAsync(chained: false);
        var launch = Assert.Single(launches);
        Assert.StartsWith("vb --vca-hook commit-msg --enqueue-automations --commit-message ", launch);
        Assert.DoesNotContain("--co-authors-cleaned", launch);
    }

    [Fact]
    public async Task AChainedHookSeesTheCleanedMessageAndValidationIsToldItWasCleaned()
    {
        var launches = await RunHookAsync(chained: true);
        Assert.Equal(3, launches.Count);
        Assert.StartsWith("vb --vca-hook clean-commit-msg --commit-message ", launches[0]);
        Assert.StartsWith("chained ", launches[1]);
        Assert.StartsWith("vb --vca-hook commit-msg --enqueue-automations --co-authors-cleaned --commit-message ", launches[2]);
    }

    [Fact]
    public async Task ValidationRunsInTheWorkTreeGitChoseWithoutAskingGitForIt()
    {
        // VIBE-374: vb resolves the repository from its working directory, which Git sets to the top of
        // the work tree, so the script starts no git of its own and passes no --workdir.
        var launches = await RunHookAsync(chained: false);
        Assert.DoesNotContain(launches, launch => launch.StartsWith("git ", StringComparison.Ordinal));
        Assert.DoesNotContain(launches, launch => launch.Contains("--workdir", StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(launches, launch => launch.StartsWith("uname", StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<string>> RunHookAsync(bool chained)
    {
        var chainedHook = chained
            ? await _harness.WriteScriptAsync(Path.Combine(_harness.Directory, "chained-hook"),
                $"#!/bin/sh\necho \"chained $*\" >> '{HookScriptHarness.ToShellPath(_harness.LogPath)}'\nexit 0\n")
            : "";
        var message = Path.Combine(_harness.Directory, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(message, "Message\n", TestContext.Current.CancellationToken);
        await _harness.RunAsync("commit-msg-hook.sh", [HookScriptHarness.ToShellPath(message)], _harness.RecordingVb(), chainedHook);
        return await _harness.LaunchesAsync();
    }
}
