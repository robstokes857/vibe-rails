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
        var startup = new LocalFrontStartup(runner, (origin, _) => { probed = origin; return Task.FromResult<string?>(null); });
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
        var startup = new LocalFrontStartup(new FakeRunner(exitCode), (_, _) => { probed = true; return Task.FromResult<string?>(null); });

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
        var startup = new LocalFrontStartup(new FakeRunner(0), (_, _) => Task.FromResult<string?>("not trusted"));

        var result = await startup.RunAsync(Local, null, BinDirectory, LockPath, _ => { }, TestContext.Current.CancellationToken, pwshPath: "pwsh");

        Assert.Equal(LocalFrontStartup.ProbeFailedExitCode, result.ExitCode);
        Assert.Equal("not trusted", result.Message);
    }

    [Fact]
    public async Task NothingRunsForAnotherOriginAMissingCheckoutOrARefusedMode()
    {
        var runner = new FakeRunner(0);
        var startup = new LocalFrontStartup(runner, (_, _) => Task.FromResult<string?>(null));
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

        var firstRun = new LocalFrontStartup(first, (_, _) => Task.FromResult<string?>(null))
            .RunAsync(Local, null, BinDirectory, LockPath, _ => { }, ct, "pwsh");
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var secondRun = new LocalFrontStartup(second, (_, _) => Task.FromResult<string?>(null))
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
        var run = new LocalFrontStartup(runner, (_, _) => Task.FromResult<string?>(null))
            .RunAsync(Local, null, BinDirectory, LockPath, _ => { }, cancel.Token, "pwsh");
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        cancel.Cancel();
        var result = await run;

        Assert.Equal(LocalFrontStartup.CancelledExitCode, result.ExitCode);
        Assert.True(runner.SawCancellation);
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
