using System.Diagnostics;
using VibeRails.Services.LocalFront;
using Xunit;

namespace Tests.Services.LocalFront;

/// <summary>
/// The Visual Studio preflight (VB-BB4ED-171): which Front checkout it runs, the exact run.ps1
/// command, and what each outcome lets the desktop do. The real run.ps1 is replaced by a runner
/// that records the command and returns an exit code.
/// </summary>
public sealed class LocalFrontStartupTests : IDisposable
{
    private static readonly LocalFrontState Local =
        LocalFrontMode.Resolve(LocalFrontMode.DefaultOrigin, debugBuild: true, "Development");

    // A space in every level the preflight touches: Visual Studio users keep repos in such paths.
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vb local front {Guid.NewGuid():N}");
    private string Desktop => Path.Combine(_root, "vibe rails");
    private string Front => Path.Combine(_root, LocalFrontStartup.SiblingFolderName);
    private string BinDirectory => Path.Combine(Desktop, "VibeRails", "bin", "Debug", "net10.0");
    private string LockPath => Path.Combine(_root, "local-front-start.lock");

    public LocalFrontStartupTests()
    {
        Directory.CreateDirectory(BinDirectory);
        File.WriteAllText(Path.Combine(Desktop, "VibeRails.slnx"), "<Solution />");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // Every orchestration test stubs the Docker step: the real one runs `docker info` and may start
    // Docker Desktop.
    private static LocalFrontStartup Startup(ILocalFrontProcessRunner runner,
        Func<Uri, CancellationToken, Task<string?>>? probe = null,
        Func<Action<string>, CancellationToken, Task<string?>>? ensureDocker = null) =>
        new(runner, probe ?? ((_, _) => Task.FromResult<string?>(null)),
            ensureDocker ?? ((_, _) => Task.FromResult<string?>(null)));

    private static void MakeCheckout(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "VibeRails-Front"));
        foreach (var file in new[] { "run.ps1", "docker-compose.local.yml", "Dockerfile.local", Path.Combine("VibeRails-Front", "VibeRails-Front.csproj") })
            File.WriteAllText(Path.Combine(path, file), "");
    }

    [Fact]
    public void FindsTheSiblingCheckoutFromTheBuildOutput()
    {
        MakeCheckout(Front);

        var checkout = LocalFrontStartup.ResolveCheckout(null, BinDirectory, out var error);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(Front), checkout);
    }

    [Fact]
    public void TheOverrideWinsAndSupportsWorktreesWithSpaces()
    {
        MakeCheckout(Front);
        var worktree = Path.Combine(_root, "worktrees", "front vibe 33");
        MakeCheckout(worktree);

        var checkout = LocalFrontStartup.ResolveCheckout($"\"{worktree}\"", BinDirectory, out var error);

        Assert.Null(error);
        Assert.Equal(worktree, checkout);
    }

    [Fact]
    public void AMissingOrIncompleteCheckoutIsRefusedWithTheFix()
    {
        Assert.Null(LocalFrontStartup.ResolveCheckout(null, BinDirectory, out var missing));
        Assert.Contains(LocalFrontStartup.CheckoutVariable, missing);

        Directory.CreateDirectory(Front);
        File.WriteAllText(Path.Combine(Front, "run.ps1"), "");
        Assert.Null(LocalFrontStartup.ResolveCheckout(null, BinDirectory, out var incomplete));
        Assert.Contains("docker-compose.local.yml", incomplete);

        Assert.Null(LocalFrontStartup.ResolveCheckout(Path.Combine(_root, "nowhere"), BinDirectory, out var overridden));
        Assert.Contains(LocalFrontStartup.CheckoutVariable, overridden);
    }

    [Fact]
    public void OutsideAVibeRailsCheckoutOnlyTheOverrideWorks()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        Assert.Null(LocalFrontStartup.ResolveCheckout(null, elsewhere, out var error));
        Assert.Contains("Could not find the vibe-rails checkout", error);
        Assert.Contains(LocalFrontStartup.CheckoutVariable, error);
    }

    [Fact]
    public void TheCommandIsExplicitArgvWithTheCheckoutAsWorkingDirectory()
    {
        var command = LocalFrontStartup.BuildStartCommand("C:/Program Files/PowerShell/7/pwsh.exe", Front);

        Assert.Equal("C:/Program Files/PowerShell/7/pwsh.exe", command.FileName);
        Assert.Equal(Front, command.WorkingDirectory);
        Assert.False(command.UseShellExecute);
        Assert.Empty(command.Arguments);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-File", Path.Combine(Front, "run.ps1"), "start", "-RepoRoot", Front, "-TimeoutSeconds", "300"],
            command.ArgumentList);
        Assert.False(command.Environment.ContainsKey(LocalFrontMode.OriginVariable));
        Assert.False(command.Environment.ContainsKey(LocalFrontStartup.StartVariable));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" true ", true)]
    public void OnlyAnExplicitStartRunsThePreflight(string? value, bool expected)
    {
        Assert.Equal(expected, LocalFrontStartup.IsRequested(value ?? ""));
    }

    [Fact]
    public async Task SuccessRunsRunPs1OnceThenChecksTheOriginFromThisProcess()
    {
        MakeCheckout(Front);
        var runner = new FakeRunner(0, "Ready: https://localhost:5164 runs the local stack.");
        Uri? probed = null;
        var startup = Startup(runner, (origin, _) => { probed = origin; return Task.FromResult<string?>(null); });
        var output = new List<string>();

        var result = await startup.RunAsync(Local, null, BinDirectory, LockPath, output.Add, TestContext.Current.CancellationToken, pwshPath: "pwsh");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.GetFullPath(Front), result.Checkout);
        var started = Assert.Single(runner.Commands);
        Assert.Equal(Path.GetFullPath(Front), started.WorkingDirectory);
        Assert.Equal(new Uri("https://localhost:5164/"), probed);
        Assert.Contains(output, line => line.Contains("Ready: https://localhost:5164"));
        Assert.Contains(output, line => line.Contains(Path.GetFullPath(Front)));
    }

    [Theory]
    [InlineData(1, "build or docker error")]
    [InlineData(3, "Docker Desktop")]
    [InlineData(4, "run.ps1 down")]
    [InlineData(5, "container stopped")]
    [InlineData(6, "not ready")]
    [InlineData(7, "dotnet dev-certs https --trust")]
    [InlineData(8, "Something other than")]
    public async Task EveryRunPs1FailureStopsTheDesktopWithItsFix(int exitCode, string expected)
    {
        MakeCheckout(Front);
        var probed = false;
        var startup = Startup(new FakeRunner(exitCode), (_, _) => { probed = true; return Task.FromResult<string?>(null); });

        var result = await startup.RunAsync(Local, null, BinDirectory, LockPath, _ => { }, TestContext.Current.CancellationToken, pwshPath: "pwsh");

        Assert.False(result.Success);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains(expected, result.Message);
        Assert.False(probed);
    }

    [Fact]
    public async Task ACertificateTheDesktopDoesNotTrustStopsIt()
    {
        MakeCheckout(Front);
        var startup = Startup(new FakeRunner(0), (_, _) => Task.FromResult<string?>("not trusted"));

        var result = await startup.RunAsync(Local, null, BinDirectory, LockPath, _ => { }, TestContext.Current.CancellationToken, pwshPath: "pwsh");

        Assert.Equal(LocalFrontStartup.ProbeFailedExitCode, result.ExitCode);
        Assert.Equal("not trusted", result.Message);
    }

    [Fact]
    public async Task NothingRunsForAnotherOriginAMissingCheckoutOrARefusedMode()
    {
        var runner = new FakeRunner(0);
        var startup = Startup(runner, (_, _) => Task.FromResult<string?>(null));
        var ct = TestContext.Current.CancellationToken;

        var otherOrigin = LocalFrontMode.Resolve("https://localhost:7000", debugBuild: true, "Development");
        Assert.Equal(LocalFrontStartup.SetupFailedExitCode,
            (await startup.RunAsync(otherOrigin, null, BinDirectory, LockPath, _ => { }, ct, "pwsh")).ExitCode);
        Assert.Equal(LocalFrontStartup.SetupFailedExitCode,
            (await startup.RunAsync(Local, null, BinDirectory, LockPath, _ => { }, ct, "pwsh")).ExitCode);
        var refused = LocalFrontMode.Resolve(LocalFrontMode.DefaultOrigin, debugBuild: false, "Development");
        MakeCheckout(Front);
        Assert.Equal(LocalFrontStartup.SetupFailedExitCode,
            (await startup.RunAsync(refused, null, BinDirectory, LockPath, _ => { }, ct, "pwsh")).ExitCode);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ASecondLaunchWaitsForTheFirstInsteadOfRunningAlongside()
    {
        MakeCheckout(Front);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeRunner(release.Task);
        var second = new FakeRunner(0);
        var ct = TestContext.Current.CancellationToken;
        var waitingLines = new List<string>();

        var firstRun = Startup(first, (_, _) => Task.FromResult<string?>(null))
            .RunAsync(Local, null, BinDirectory, LockPath, _ => { }, ct, "pwsh");
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var secondRun = Startup(second, (_, _) => Task.FromResult<string?>(null))
            .RunAsync(Local, null, BinDirectory, LockPath, line => { lock (waitingLines) waitingLines.Add(line); }, ct, "pwsh");
        await Task.Delay(700, ct);

        Assert.Empty(second.Commands);
        lock (waitingLines)
            Assert.Contains(waitingLines, line => line.Contains("waiting"));

        release.SetResult(0);
        Assert.True((await firstRun).Success);
        Assert.True((await secondRun).Success);
        Assert.Single(second.Commands);
    }

    [Fact]
    public async Task CancellingStopsTheScriptAndTheDesktop()
    {
        MakeCheckout(Front);
        using var cancel = new CancellationTokenSource();
        var runner = new FakeRunner(new TaskCompletionSource<int>().Task);
        var run = Startup(runner, (_, _) => Task.FromResult<string?>(null))
            .RunAsync(Local, null, BinDirectory, LockPath, _ => { }, cancel.Token, "pwsh");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        cancel.Cancel();
        var result = await run;

        Assert.Equal(LocalFrontStartup.CancelledExitCode, result.ExitCode);
        Assert.True(runner.SawCancellation);
    }

    [Fact]
    public async Task DockerThatNeverAnswersStopsBeforeRunPs1()
    {
        MakeCheckout(Front);
        var runner = new FakeRunner(0);

        var result = await Startup(runner, ensureDocker: (_, _) => Task.FromResult<string?>("Docker did not become ready within 3 minutes."))
            .RunAsync(Local, null, BinDirectory, LockPath, _ => { }, TestContext.Current.CancellationToken, "pwsh");

        Assert.Equal(LocalFrontStartup.DockerUnavailableExitCode, result.ExitCode);
        Assert.Empty(runner.Commands);
    }

    // ------------------------------------------------------------ the real runner (review R2)

    private static ProcessStartInfo PowerShell(string script)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
            info.ArgumentList.Add(argument);
        return info;
    }

    // What run.ps1 does with docker compose: start a child process and leave it running.
    private static string StartsAChild(string pidFile, int scriptSeconds) =>
        $"$c = Start-Process -PassThru -WindowStyle Hidden powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120'; " +
        $"Set-Content -LiteralPath '{pidFile}' $c.Id; Start-Sleep -Seconds {scriptSeconds}";

    private static async Task<int> ChildPidAsync(string pidFile, Task running)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !running.IsCompleted)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid))
                return pid;
            await Task.Delay(100);
        }
        throw new TimeoutException("The fixture script never reported its child.");
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task CancellingTheRealRunnerStopsEverythingTheScriptStarted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The preflight's process tree lives in a Windows job.");
        var pidFile = Path.Combine(_root, "cancelled-child.pid");
        using var cancel = new CancellationTokenSource();
        var run = new LocalFrontProcessRunner().RunAsync(PowerShell(StartsAChild(pidFile, 120)), _ => { }, cancel.Token);
        var child = await ChildPidAsync(pidFile, run);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(HasExited(child), "The script's child kept running after cancellation returned.");
    }

    [Fact]
    public async Task AScriptThatExitsLeavesNothingRunning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The preflight's process tree lives in a Windows job.");
        var pidFile = Path.Combine(_root, "finished-child.pid");

        var exit = await new LocalFrontProcessRunner().RunAsync(
            PowerShell(StartsAChild(pidFile, 0)), _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.True(HasExited(int.Parse((await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken)).Trim())));
    }

    [Fact]
    public async Task ACancelledLaunchReleasesTheStartLockOnlyAfterItsTreeIsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The preflight's process tree lives in a Windows job.");
        // The real contract runs `pwsh -File`; Windows PowerShell's default policy refuses local scripts.
        var pwsh = LocalFrontStartup.FindPwsh();
        Assert.SkipWhen(pwsh is null, "PowerShell 7 (pwsh) is not installed.");
        MakeCheckout(Front);
        var pidFile = Path.Combine(_root, "launch-child.pid");
        // The fixture run.ps1 accepts the real contract's arguments and starts a long-lived child.
        File.WriteAllText(Path.Combine(Front, "run.ps1"),
            "param([string]$Command, [string]$RepoRoot, [int]$TimeoutSeconds)" + Environment.NewLine + StartsAChild(pidFile, 120));
        using var cancel = new CancellationTokenSource();
        var first = Startup(new LocalFrontProcessRunner())
            .RunAsync(Local, null, BinDirectory, LockPath, _ => { }, cancel.Token, pwshPath: pwsh);
        var child = await ChildPidAsync(pidFile, first);

        cancel.Cancel();
        var cancelled = await first;

        Assert.Equal(LocalFrontStartup.CancelledExitCode, cancelled.ExitCode);
        Assert.True(HasExited(child), "A cancelled launch released the start lock while its child still ran.");
        var waited = new List<string>();
        var next = Startup(new FakeRunner(0))
            .RunAsync(Local, null, BinDirectory, LockPath, waited.Add, TestContext.Current.CancellationToken, "pwsh");
        Assert.True((await next.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Success);
        Assert.DoesNotContain(waited, line => line.Contains("waiting"));
    }

    [Fact]
    public void PwshIsFoundOnPath()
    {
        var bin = Path.Combine(_root, "tools dir");
        Directory.CreateDirectory(bin);
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        File.WriteAllText(Path.Combine(bin, name), "");

        Assert.Equal(Path.Combine(bin, name), LocalFrontStartup.FindPwsh($"{Path.Combine(_root, "empty")}{Path.PathSeparator}{bin}"));
    }

    private sealed class FakeRunner : ILocalFrontProcessRunner
    {
        private readonly Task<int> _exit;
        private readonly string[] _lines;

        public FakeRunner(int exitCode, params string[] lines) : this(Task.FromResult(exitCode), lines) { }

        public FakeRunner(Task<int> exit, params string[] lines)
        {
            _exit = exit;
            _lines = lines;
        }

        public List<ProcessStartInfo> Commands { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawCancellation { get; private set; }

        public async Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> onLine, CancellationToken cancellationToken)
        {
            lock (Commands)
                Commands.Add(startInfo);
            Started.TrySetResult();
            foreach (var line in _lines)
                onLine(line);
            try
            {
                return await _exit.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
        }
    }
}
