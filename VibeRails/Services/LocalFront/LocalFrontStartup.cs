using System.Diagnostics;
using System.Security.Authentication;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.LocalFront;

/// <summary>The outcome of the local Front preflight. <see cref="ExitCode"/> is 0 on success.</summary>
public sealed record LocalFrontStartResult(int ExitCode, string Message, string? Checkout = null)
{
    /// <summary>True when the desktop may start.</summary>
    public bool Success => ExitCode == 0;
}

/// <summary>Runs one process to completion, forwarding each output line. Faked in tests.</summary>
public interface ILocalFrontProcessRunner
{
    /// <summary>Returns the exit code. Cancelling stops the process.</summary>
    Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> onLine, CancellationToken cancellationToken);
}

/// <summary>
/// The Debug-only preflight behind the "VibeRails + Local Front" Visual Studio profile
/// (VB-BB4ED-171). Before the root backend builds its web host, it runs Front's startup contract
/// (VibeRails-Front docs/local-desktop-development.md):
/// <c>pwsh -NoProfile -NonInteractive -File &lt;front&gt;\run.ps1 start -RepoRoot &lt;front&gt;</c>,
/// then checks from this process that the local origin answers over normally validated HTTPS. Any
/// failure stops the desktop: there is no half-working start and no fallback to production.
/// Front's containers keep running after the desktop stops; <c>run.ps1 down</c> stops them and
/// keeps their data.
/// </summary>
public sealed class LocalFrontStartup(
    ILocalFrontProcessRunner runner,
    Func<Uri, CancellationToken, Task<string?>> probe,
    Func<Action<string>, CancellationToken, Task<string?>>? ensureDocker = null)
{
    private readonly Func<Action<string>, CancellationToken, Task<string?>> _ensureDocker =
        ensureDocker ?? ((output, cancellationToken) => EnsureDockerAsync(runner, output, cancellationToken));

    /// <summary>"1" asks the root backend to start the local Front stack before it starts.</summary>
    public const string StartVariable = "VIBERAILS_LOCAL_FRONT_START";

    /// <summary>Per-user override for the VibeRails-Front checkout (worktrees, other folders).</summary>
    public const string CheckoutVariable = "VIBERAILS_FRONT_CHECKOUT";

    /// <summary>The sibling folder name looked for beside the vibe-rails checkout.</summary>
    public const string SiblingFolderName = "VibeRails-Front";

    /// <summary>How long run.ps1 waits for readiness once its containers are up.</summary>
    public const int ReadinessTimeoutSeconds = 300;

    /// <summary>
    /// Bound on the whole run.ps1 process. The first image build downloads the SDK and SQL Server
    /// images and is outside run.ps1's own readiness timeout.
    /// </summary>
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Exit code for a preflight that never reached run.ps1 (checkout, pwsh, origin).</summary>
    public const int SetupFailedExitCode = 2;

    /// <summary>Exit code when the desktop's own HTTPS check of the started stack fails.</summary>
    public const int ProbeFailedExitCode = 9;

    /// <summary>Exit code for a timeout or cancellation of the whole preflight.</summary>
    public const int CancelledExitCode = 10;

    /// <summary>run.ps1's own code for "Docker is not installed or did not become ready".</summary>
    public const int DockerUnavailableExitCode = 3;

    /// <summary>How long Docker Desktop may take to answer after this preflight starts it (as run.ps1).</summary>
    public static readonly TimeSpan DockerStartTimeout = TimeSpan.FromMinutes(3);

    private static readonly string[] CheckoutMarkers =
    [
        "run.ps1",
        "docker-compose.local.yml",
        "Dockerfile.local",
        Path.Combine("VibeRails-Front", "VibeRails-Front.csproj")
    ];

    /// <summary>The real runner and HTTPS probe.</summary>
    public static LocalFrontStartup CreateDefault() => new(new LocalFrontProcessRunner(), ProbeAsync);

    /// <summary>True when the profile asked for the preflight.</summary>
    public static bool IsRequested(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(StartVariable);
        return value?.Trim() is "1" || string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Front checkout: <paramref name="overridePath"/> when set, otherwise the
    /// <see cref="SiblingFolderName"/> folder beside the vibe-rails checkout that contains
    /// <paramref name="startDirectory"/> (found by walking up to <c>VibeRails.slnx</c>).
    /// Returns null with an actionable error when none is usable.
    /// </summary>
    public static string? ResolveCheckout(string? overridePath, string startDirectory, out string? error)
    {
        string candidate;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            try
            {
                candidate = Path.GetFullPath(overridePath.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"{CheckoutVariable} is not a valid folder path.";
                return null;
            }
            error = ValidateCheckout(candidate);
            if (error is not null)
                error += $" It comes from {CheckoutVariable}; point it at your VibeRails-Front checkout and restart Visual Studio.";
            return error is null ? candidate : null;
        }

        var repositoryRoot = FindRepositoryRoot(startDirectory);
        if (repositoryRoot is null)
        {
            error = $"Could not find the vibe-rails checkout above {startDirectory}, so the sibling {SiblingFolderName} folder is unknown. Set {CheckoutVariable} to your VibeRails-Front checkout and restart Visual Studio.";
            return null;
        }
        candidate = Path.Combine(Path.GetDirectoryName(repositoryRoot) ?? repositoryRoot, SiblingFolderName);
        error = ValidateCheckout(candidate);
        if (error is not null)
            error += $" Clone VibeRails-Front beside vibe-rails, or set {CheckoutVariable} to its checkout (a worktree works) and restart Visual Studio.";
        return error is null ? candidate : null;
    }

    /// <summary>Null when <paramref name="path"/> looks like a VibeRails-Front checkout.</summary>
    public static string? ValidateCheckout(string path)
    {
        if (!Directory.Exists(path))
            return $"The VibeRails-Front checkout {path} does not exist.";
        var missing = CheckoutMarkers.Where(marker => !File.Exists(Path.Combine(path, marker))).ToList();
        return missing.Count == 0
            ? null
            : $"{path} is not a VibeRails-Front checkout with the local Docker stack (missing {string.Join(", ", missing)}).";
    }

    /// <summary>The directory holding <c>VibeRails.slnx</c> at or above <paramref name="startDirectory"/>.</summary>
    public static string? FindRepositoryRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        return null;
    }

    /// <summary>pwsh on PATH, else the default PowerShell 7 install folder. Null when missing.</summary>
    public static string? FindPwsh(string? pathVariable = null)
    {
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        if (FindOnPath(name, pathVariable) is { } found)
            return found;
        if (!OperatingSystem.IsWindows())
            return null;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "PowerShell", "7", name);
        return File.Exists(installed) ? installed : null;
    }

    private static string? FindOnPath(string name, string? pathVariable = null)
    {
        foreach (var directory in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped like the shell would.
            }
        }
        return null;
    }

    /// <summary>
    /// Makes sure Docker answers before run.ps1 runs, starting Docker Desktop from this process when
    /// it does not. run.ps1 would otherwise start Docker Desktop as its own child, inside the
    /// kill-on-close job, and stopping the script would stop Docker Desktop with it. Returns null to
    /// continue; when docker or Docker Desktop is missing, run.ps1 reports that itself (exit 3).
    /// </summary>
    public static async Task<string?> EnsureDockerAsync(ILocalFrontProcessRunner runner, Action<string> output, CancellationToken cancellationToken)
    {
        var docker = FindOnPath(OperatingSystem.IsWindows() ? "docker.exe" : "docker");
        if (docker is null || await DockerAnswersAsync(runner, docker, cancellationToken) || !OperatingSystem.IsWindows())
            return null;
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
        if (!File.Exists(desktop))
            return null;
        output("Starting Docker Desktop...");
        // Shell-started from the desktop, never from run.ps1: it outlives this debug session.
        Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = true })?.Dispose();
        var deadline = DateTime.UtcNow + DockerStartTimeout;
        while (!await DockerAnswersAsync(runner, docker, cancellationToken))
        {
            if (DateTime.UtcNow > deadline)
                return "Docker did not become ready within 3 minutes. Start Docker Desktop, then press Start again.";
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        return null;
    }

    private static async Task<bool> DockerAnswersAsync(ILocalFrontProcessRunner runner, string docker, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = docker,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("info");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await runner.RunAsync(info, static _ => { }, bound.Token) == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// The exact process for Front's startup contract: explicit executable and argv, the checkout
    /// as working directory, output captured, nothing interactive.
    /// </summary>
    public static ProcessStartInfo BuildStartCommand(string pwsh, string checkout, int readinessTimeoutSeconds = ReadinessTimeoutSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pwsh,
            WorkingDirectory = checkout,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-File", Path.Combine(checkout, "run.ps1"),
                     "start", "-RepoRoot", checkout, "-TimeoutSeconds", readinessTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        // run.ps1 picks `start` itself when output is redirected; the argument makes it explicit.
        // The local-mode variables describe this desktop, not Front: keep them out of its tools.
        startInfo.Environment.Remove(LocalFrontMode.OriginVariable);
        startInfo.Environment.Remove(StartVariable);
        return startInfo;
    }

    /// <summary>What run.ps1's exit code means and what to do about it.</summary>
    public static string Describe(int exitCode, string checkout) => exitCode switch
    {
        0 => $"The local Front stack from {checkout} is ready.",
        1 => "run.ps1 failed (a build or docker error). Read its output above, fix the error, then press Start again.",
        2 => $"{checkout} is not a VibeRails-Front checkout. Set {CheckoutVariable} to the right folder and restart Visual Studio.",
        3 => "Docker is not installed or did not become ready within 3 minutes. Start Docker Desktop, then press Start again.",
        4 => "The local Front stack is already running from another checkout, or another program holds port 5164, 14333 or 10000. Stop the other stack with `pwsh -File <its checkout>\\run.ps1 down` (it keeps the local data), or free the port, then press Start again.",
        5 => "The Front app container stopped while starting. Its last log lines are above.",
        6 => $"The Front app was not ready within {ReadinessTimeoutSeconds} seconds. Its last log lines are above.",
        7 => $"This Windows user does not trust the Front development certificate. Run `dotnet dev-certs https --trust`, then `pwsh -File \"{Path.Combine(checkout, "run.ps1")}\" down`, then press Start again.",
        8 => "Something other than the local Front stack answered at https://localhost:5164. Stop it, then press Start again.",
        _ => $"run.ps1 exited with code {exitCode}. Read its output above."
    };

    /// <summary>
    /// Starts (or reuses) the local Front stack. Waits for any other local-mode launch on this
    /// machine to finish first, so two Starts never run run.ps1 at the same time.
    /// </summary>
    public async Task<LocalFrontStartResult> RunAsync(LocalFrontState state, string? checkoutOverride, string startDirectory,
        string lockPath, Action<string> output, CancellationToken cancellationToken, string? pwshPath = null)
    {
        if (!state.Active)
            return new(SetupFailedExitCode, state.Error ?? $"{LocalFrontMode.OriginVariable} is not set.");
        // run.ps1 always publishes https://localhost:5164; any other origin would be a stack this
        // preflight did not start.
        if (!string.Equals(state.OriginText, LocalFrontMode.DefaultOrigin, StringComparison.OrdinalIgnoreCase))
            return new(SetupFailedExitCode,
                $"{StartVariable} starts the stack at {LocalFrontMode.DefaultOrigin}, but {LocalFrontMode.OriginVariable} is {state.OriginText}. Use {LocalFrontMode.DefaultOrigin}, or unset {StartVariable} and start that Front yourself.");

        var checkout = ResolveCheckout(checkoutOverride, startDirectory, out var checkoutError);
        if (checkout is null)
            return new(SetupFailedExitCode, checkoutError!);
        output($"Local Front checkout: {checkout}");

        var pwsh = pwshPath ?? FindPwsh();
        if (pwsh is null)
            return new(SetupFailedExitCode,
                "PowerShell 7.4 or later (pwsh) was not found on PATH. Install it from https://aka.ms/powershell, restart Visual Studio, then press Start again.", checkout);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OverallTimeout);
        try
        {
            using var startLock = await AcquireStartLockAsync(lockPath, output, deadline.Token);
            if (await _ensureDocker(output, deadline.Token) is { } dockerError)
                return new(DockerUnavailableExitCode, dockerError, checkout);
            output($"Starting the local Front stack: {pwsh} -File run.ps1 start -RepoRoot \"{checkout}\"");
            var started = Stopwatch.StartNew();
            var exitCode = await runner.RunAsync(BuildStartCommand(pwsh, checkout), line =>
            {
                // run.ps1 prints no secrets (its contract); keep its lines in the log for diagnosis.
                Log.Information("[LocalFront] run.ps1: {Line}", line);
                output(line);
            }, deadline.Token);
            Log.Information("[LocalFront] run.ps1 start exited {ExitCode} after {ElapsedSeconds:0}s", exitCode, started.Elapsed.TotalSeconds);
            if (exitCode != 0)
                return new(exitCode, Describe(exitCode, checkout), checkout);
            output($"run.ps1 start succeeded in {started.Elapsed.TotalSeconds:0} s.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(CancelledExitCode,
                $"Starting the local Front did not finish within {OverallTimeout.TotalMinutes:0} minutes. Run `pwsh -File \"{Path.Combine(checkout, "run.ps1")}\" up` in a console to watch it, then press Start again.", checkout);
        }
        catch (OperationCanceledException)
        {
            return new(CancelledExitCode, "Starting the local Front was cancelled. The desktop did not start.", checkout);
        }

        // run.ps1 validated the certificate with pwsh; confirm this process trusts it too before
        // any Front client starts.
        string? probeError;
        try
        {
            probeError = await probe(state.Origin, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(CancelledExitCode, "Starting the local Front was cancelled. The desktop did not start.", checkout);
        }
        return probeError is null
            ? new(0, Describe(0, checkout), checkout)
            : new(ProbeFailedExitCode, probeError, checkout);
    }

    /// <summary>
    /// GETs the local sign-in page with default certificate validation. Returns null when it
    /// answered 200, otherwise what went wrong, with a TLS trust failure reported separately.
    /// </summary>
    public static async Task<string?> ProbeAsync(Uri origin, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await client.GetAsync(new Uri(origin, "/dev/login"), cancellationToken);
            return response.IsSuccessStatusCode
                ? null
                : $"The local Front at {origin.GetLeftPart(UriPartial.Authority)} answered /dev/login with HTTP {(int)response.StatusCode}; it is not the local Front stack.";
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return $"This process does not trust the certificate at {origin.GetLeftPart(UriPartial.Authority)}. Run `dotnet dev-certs https --trust`, then run.ps1 down and press Start again.";
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return $"The local Front at {origin.GetLeftPart(UriPartial.Authority)} did not answer ({ex.GetType().Name}). Check `docker ps` and the app container's logs.";
        }
    }

    private static async Task<IDisposable> AcquireStartLockAsync(string lockPath, Action<string> output, CancellationToken cancellationToken)
    {
        var waiting = false;
        while (true)
        {
            var held = CrossProcessFileLock.TryAcquire(lockPath);
            if (held is not null)
                return held;
            if (!waiting)
            {
                output("Another VibeRails launch is starting the local Front; waiting for it to finish…");
                waiting = true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    /// <summary>The machine-wide lock file that serializes local Front starts.</summary>
    public static string DefaultLockPath() =>
        Path.Combine(PathConstants.GetInstallDirPath(), "local-front-start.lock");
}

/// <summary>
/// Runs one preflight command (run.ps1, docker info) with its whole process tree in a Windows
/// kill-on-close job: everything it starts, docker compose included, stops with it, and Stop
/// Debugging ends the tree too. Docker Desktop is never in the tree: <see
/// cref="LocalFrontStartup.EnsureDockerAsync"/> starts it before run.ps1 would. The tree is gone
/// before this returns, so the caller's start lock is never released while a stack-changing
/// command still runs.
/// </summary>
internal sealed class LocalFrontProcessRunner : ILocalFrontProcessRunner
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(15);

    public async Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> onLine, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        using var job = KillOnCloseJob.TryCreate();
        process.Start();
        // A process Windows refused to put in the job is stopped by a direct tree kill instead.
        var bound = job is not null && job.TryAdd(process) ? job : null;
        try
        {
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
            // Drain the asynchronous readers before reporting.
            process.WaitForExit();
            return process.ExitCode;
        }
        finally
        {
            // Normal exit leaves nothing to stop; cancellation stops the script and everything it
            // started. Either way, wait until the tree is gone.
            StopTree(process, bound);
        }
    }

    private static void StopTree(Process process, KillOnCloseJob? job)
    {
        if (job is not null)
        {
            job.Terminate();
            if (job.WaitUntilEmpty(JoinTimeout))
                return;
            Log.Warning("[LocalFront] The preflight's process tree was still running {Seconds}s after it was stopped; killing it directly",
                JoinTimeout.TotalSeconds);
        }
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        process.WaitForExit((int)JoinTimeout.TotalMilliseconds);
    }
}
