using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// What the real pre-commit and post-commit scripts start on every commit (VIBE-374). Each git or
/// uname launch costs tens of milliseconds on Git for Windows, and the post-commit work happens after
/// the commit exists, so Git must not wait for it.
/// </summary>
public sealed class HookScriptLaunchTests : IDisposable
{
    private readonly HookScriptHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task PreCommitStartsOnlyVbInTheWorkTreeGitChose()
    {
        await _harness.RunAsync("pre-commit-hook.sh", [], _harness.RecordingVb());

        var launches = await _harness.LaunchesAsync();
        Assert.Equal(["vb --vca-hook pre-commit --enqueue-automations"], launches.Where(IsNotUname));
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(launches, launch => !IsNotUname(launch));
    }

    [Fact]
    public async Task PostCommitQueuesThisCommitWithoutMakingGitWait()
    {
        var release = HookScriptHarness.ToShellPath(Path.Combine(_harness.Directory, "release"));
        var started = Path.Combine(_harness.Directory, "vb-started");
        // The stub vb waits until the test releases it, so the hook can only exit first if it does not wait.
        // It notes its start and any error elsewhere, for a failure message that tells a slow start under
        // a loaded test run from a launch that never happened.
        var vb = string.Join("\n",
            "#!/bin/sh",
            $"exec 2>>'{HookScriptHarness.ToShellPath(started)}'",
            $"echo started >> '{HookScriptHarness.ToShellPath(started)}'",
            "i=0",
            $"while [ ! -f '{release}' ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done",
            $"echo \"vb $*\" >> '{HookScriptHarness.ToShellPath(_harness.LogPath)}'",
            "exit 0",
            "");

        await _harness.RunAsync("post-commit-hook.sh", [], vb);

        Assert.Equal(["git rev-parse HEAD"], await _harness.LaunchesAsync());
        await File.WriteAllTextAsync(Path.Combine(_harness.Directory, "release"), "", TestContext.Current.CancellationToken);
        var launches = await WaitForLaunchesAsync(2);
        Assert.True(launches.Count == 2,
            "vb: " + (File.Exists(started) ? File.ReadAllText(started) : "never started"));
        Assert.Equal(["git rev-parse HEAD", $"vb --job-trigger post-commit --commit {HookScriptHarness.HeadCommit}"], launches);
    }

    private static bool IsNotUname(string launch) => !launch.StartsWith("uname", StringComparison.Ordinal);

    private async Task<IReadOnlyList<string>> WaitForLaunchesAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var launches = await _harness.LaunchesAsync();
            if (launches.Count >= count || DateTime.UtcNow > deadline) return launches;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}
