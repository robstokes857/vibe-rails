using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using VibeRails.Utils;

namespace VibeRails.Services.Mcp.HostShell;

public sealed class HostShellCommandService : IHostShellCommandService, IAsyncDisposable
{
    private const int MaxCommandChars = 24000;
    private const int MaxRetainedJobs = 200;
    private static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(30);

    private readonly ILogger<HostShellCommandService> _logger;
    private readonly Channel<ShellJob> _queue;
    private readonly ConcurrentDictionary<string, ShellJob> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private int _jobSequence;
    private int _disposed;

    public HostShellCommandService(ILogger<HostShellCommandService> logger)
    {
        _logger = logger;
        var workerCount = Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 4);
        _queue = Channel.CreateBounded<ShellJob>(new BoundedChannelOptions(workerCount * 8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _workers = Enumerable.Range(0, workerCount)
            .Select(i => Task.Run(() => RunWorkerLoopAsync(i + 1, _shutdown.Token)))
            .ToArray();
    }

    public async Task<HostShellCommandResult> RunAsync(
        HostShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRequest(request);
        CleanupCompletedJobs();

        var shell = ResolveShellName(request.Shell);
        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 3600));
        var maxOutputChars = Math.Clamp(request.MaxOutputChars, 1000, 200000);
        var job = new ShellJob(
            JobId: $"shell-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _jobSequence):x}",
            Command: request.Command,
            WorkingDirectory: workingDirectory,
            Shell: shell,
            Timeout: timeout,
            MaxOutputChars: maxOutputChars);

        if (!_jobs.TryAdd(job.JobId, job))
        {
            throw new InvalidOperationException("Failed to allocate a shell command job id.");
        }

        await _queue.Writer.WriteAsync(job, cancellationToken);

        if (!request.WaitForCompletion)
        {
            return job.ToResult("Queued for a reusable host shell worker.");
        }

        var wait = TimeSpan.FromSeconds(Math.Clamp(request.WaitSeconds, 0, 3600));
        if (wait == TimeSpan.Zero)
        {
            return job.ToResult("Queued for a reusable host shell worker.");
        }

        try
        {
            await job.Completion.Task.WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
            return job.ToResult("Still running. Poll get_shell_command_status with the job id.");
        }

        return job.ToResult();
    }

    public HostShellCommandResult? GetStatus(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        return _jobs.TryGetValue(jobId.Trim(), out var job) ? job.ToResult() : null;
    }

    public async Task<HostShellCommandResult?> CancelAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return null;
        }

        if (!_jobs.TryGetValue(jobId.Trim(), out var job))
        {
            return null;
        }

        job.RequestCancel();
        if (job.Status is HostShellCommandStatus.Queued)
        {
            job.Complete(HostShellCommandStatus.Cancelled, null, "Cancelled before a worker started it.");
            return job.ToResult();
        }

        try
        {
            await job.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch
        {
            // The worker owns the process and will finish cancellation cleanup.
        }

        return job.ToResult();
    }

    private async Task RunWorkerLoopAsync(int number, CancellationToken cancellationToken)
    {
        var workerId = $"shell-worker-{number}";
        await using var worker = new HostShellWorker(workerId, _logger);

        await foreach (var job in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            if (job.CancelRequested)
            {
                job.Complete(HostShellCommandStatus.Cancelled, null, "Cancelled before a worker started it.");
                continue;
            }

            try
            {
                await worker.ExecuteAsync(job, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                job.Complete(HostShellCommandStatus.Cancelled, null, "VibeRails is shutting down.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Host shell worker failed while running job {JobId}", job.JobId);
                job.Complete(HostShellCommandStatus.Failed, null, ex.Message);
                await worker.ResetAsync();
            }
        }
    }

    private static void ValidateRequest(HostShellCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("Command is required.");
        }

        if (request.Command.Length > MaxCommandChars)
        {
            throw new ArgumentException($"Command exceeds {MaxCommandChars} characters.");
        }

        if (request.Command.Any(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("Command contains unsupported control characters.");
        }
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        var path = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : Environment.ExpandEnvironmentVariables(workingDirectory.Trim());

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {fullPath}");
        }

        return fullPath;
    }

    private static string ResolveShellName(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return OperatingSystem.IsWindows()
                ? "pwsh"
                : OperatingSystem.IsMacOS() ? "zsh" : "bash";
        }

        var shell = requested.Trim().ToLowerInvariant();
        return shell switch
        {
            "pwsh" or "powershell" when OperatingSystem.IsWindows() => "pwsh",
            "bash" when !OperatingSystem.IsWindows() => "bash",
            "zsh" when !OperatingSystem.IsWindows() => "zsh",
            _ => throw new ArgumentException("Unsupported shell. Supported shells: PowerShell 7+ (pwsh) on Windows, bash on Linux, zsh on macOS.")
        };
    }

    private void CleanupCompletedJobs()
    {
        if (_jobs.Count <= MaxRetainedJobs)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - JobRetention;
        foreach (var (id, job) in _jobs)
        {
            if (job.CompletedUtc is { } completed && completed < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }

        // A burst of short jobs can leave the map over the cap even though none are old
        // enough for the age sweep above. Evict the oldest COMPLETED jobs by completion
        // time until we're back under the cap; running/queued jobs are never evicted.
        if (_jobs.Count <= MaxRetainedJobs)
        {
            return;
        }

        var removeCount = _jobs.Count - MaxRetainedJobs;
        var evictable = _jobs
            .Where(kvp => kvp.Value.CompletedUtc is not null)
            .OrderBy(kvp => kvp.Value.CompletedUtc)
            .Take(removeCount)
            .ToList();

        foreach (var (id, _) in evictable)
        {
            _jobs.TryRemove(id, out _);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HostShellCommandService));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort shutdown.
        }
        _shutdown.Dispose();
    }

    private sealed class HostShellWorker : IAsyncDisposable
    {
        private readonly string _workerId;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private Process? _process;
        private ShellJob? _activeJob;
        private TaskCompletionSource<int>? _activeStdoutMarker;
        private TaskCompletionSource? _activeStderrMarker;
        private string? _activeMarkerText;
        private string? _shell;
        private Task? _stdoutTask;
        private Task? _stderrTask;

        public HostShellWorker(string workerId, ILogger logger)
        {
            _workerId = workerId;
            _logger = logger;
        }

        public async Task ExecuteAsync(ShellJob job, CancellationToken hostCancellationToken)
        {
            job.Start(_workerId);
            await EnsureProcessAsync(job.Shell);

            using var timeoutCts = new CancellationTokenSource(job.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token,
                hostCancellationToken,
                job.CancelToken);

            var marker = $"__VIBERAILS_SHELL_DONE_{Guid.NewGuid():N}__";
            var stdoutMarkerTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrMarkerTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // If the shell dies (e.g. the command ran `exit`) the stdout reader faults this
            // task after we've already stopped awaiting it (timeout/cancel path). Observe the
            // fault here so it never bubbles up as an UnobservedTaskException.
            _ = stdoutMarkerTcs.Task.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = stderrMarkerTcs.Task.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            lock (_gate)
            {
                _activeJob = job;
                _activeStdoutMarker = stdoutMarkerTcs;
                _activeStderrMarker = stderrMarkerTcs;
                _activeMarkerText = marker;
            }

            try
            {
                var commandText = BuildWrappedCommand(job, marker);
                var process = _process ?? throw new InvalidOperationException("Host shell process was not started.");
                using var commandReader = new StringReader(commandText);
                while (await commandReader.ReadLineAsync(linked.Token) is { } line)
                {
                    await process.StandardInput.WriteLineAsync(line.AsMemory(), linked.Token);
                }
                await process.StandardInput.FlushAsync(linked.Token);

                var exitCode = await stdoutMarkerTcs.Task.WaitAsync(linked.Token);
                await stderrMarkerTcs.Task.WaitAsync(linked.Token);
                job.Complete(HostShellCommandStatus.Completed, exitCode, null);
            }
            catch (OperationCanceledException) when (job.CancelRequested)
            {
                job.Complete(HostShellCommandStatus.Cancelled, null, "Cancelled by caller.");
                await ResetAsync();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                job.Complete(HostShellCommandStatus.TimedOut, null, $"Timed out after {(int)job.Timeout.TotalSeconds} seconds.");
                await ResetAsync();
            }
            catch (Exception ex)
            {
                job.Complete(HostShellCommandStatus.Failed, null, ex.Message);
                await ResetAsync();
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeJob, job))
                    {
                        _activeJob = null;
                        _activeStdoutMarker = null;
                        _activeStderrMarker = null;
                        _activeMarkerText = null;
                    }
                }
            }
        }

        private async Task EnsureProcessAsync(string shell)
        {
            if (_process is { HasExited: false } && string.Equals(_shell, shell, StringComparison.Ordinal))
            {
                return;
            }

            await ResetAsync();
            _shell = shell;
            var startInfo = BuildStartInfo(shell);
            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            _process.Start();
            _stdoutTask = Task.Run(() => ReadLinesAsync(_process.StandardOutput, isError: false));
            _stderrTask = Task.Run(() => ReadLinesAsync(_process.StandardError, isError: true));
        }

        private async Task ReadLinesAsync(StreamReader reader, bool isError)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    ShellJob? job;
                    TaskCompletionSource<int>? stdoutMarker;
                    TaskCompletionSource? stderrMarker;
                    string? markerText;
                    lock (_gate)
                    {
                        job = _activeJob;
                        stdoutMarker = _activeStdoutMarker;
                        stderrMarker = _activeStderrMarker;
                        markerText = _activeMarkerText;
                    }

                    if (job == null)
                    {
                        continue;
                    }

                    if (!isError && markerText != null && TryReadStdoutMarker(line, markerText, out var exitCode, out var stdoutPrefix))
                    {
                        if (!string.IsNullOrEmpty(stdoutPrefix))
                        {
                            job.AppendOutput(stdoutPrefix, isError: false);
                        }
                        stdoutMarker?.TrySetResult(exitCode);
                        continue;
                    }

                    if (isError && markerText != null && TryReadStderrMarker(line, markerText, out var stderrPrefix))
                    {
                        if (!string.IsNullOrEmpty(stderrPrefix))
                        {
                            job.AppendOutput(stderrPrefix, isError: true);
                        }
                        stderrMarker?.TrySetResult();
                        continue;
                    }

                    job.AppendOutput(line + Environment.NewLine, isError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Host shell output reader stopped for {WorkerId}", _workerId);
            }
            finally
            {
                if (!isError)
                {
                    // stdout closed → the shell process exited (or was killed). If a job is
                    // still waiting for its completion marker, unblock it now instead of
                    // hanging the worker until the job timeout (e.g. the command ran `exit`).
                    TaskCompletionSource<int>? marker;
                    lock (_gate)
                    {
                        marker = _activeStdoutMarker;
                    }

                    marker?.TrySetException(new InvalidOperationException(
                        "The host shell process exited before the command completed."));
                }
                else
                {
                    TaskCompletionSource? marker;
                    lock (_gate)
                    {
                        marker = _activeStderrMarker;
                    }

                    marker?.TrySetException(new InvalidOperationException(
                        "The host shell stderr stream closed before the command completed."));
                }
            }
        }

        private static ProcessStartInfo BuildStartInfo(string shell)
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            switch (shell)
            {
                case "pwsh":
                    startInfo.FileName = ShellDefaults.WindowsCommandShell;
                    startInfo.ArgumentList.Add("-NoLogo");
                    startInfo.ArgumentList.Add("-NoProfile");
                    startInfo.ArgumentList.Add("-NonInteractive");
                    break;
                case "zsh":
                    startInfo.FileName = ShellDefaults.MacOSShell;
                    startInfo.ArgumentList.Add("-f");
                    break;
                default:
                    startInfo.FileName = ShellDefaults.LinuxShell;
                    startInfo.ArgumentList.Add("--noprofile");
                    startInfo.ArgumentList.Add("--norc");
                    break;
            }

            return startInfo;
        }

        private static string BuildWrappedCommand(ShellJob job, string marker)
        {
            return job.Shell == "pwsh"
                ? BuildPowerShellCommand(job, marker)
                : BuildPosixCommand(job, marker);
        }

        private static string BuildPowerShellCommand(ShellJob job, string marker)
        {
            var wd = PowerShellSingleQuote(job.WorkingDirectory);
            var markerValue = PowerShellSingleQuote(marker);
            return string.Join(Environment.NewLine, new[]
            {
                "$global:LASTEXITCODE = $null",
                "$__vbr_exit = 0",
                $"$__vbr_marker = '{markerValue}'",
                $"Push-Location -LiteralPath '{wd}'",
                job.Command,
                // Capture $? for the LAST statement FIRST — any later evaluation resets it.
                // A failed cmdlet (which never touches $LASTEXITCODE) must not be reported as
                // success just because an earlier native command left a stale 0 behind.
                "$__vbr_ok = $?",
                "$__vbr_code = $global:LASTEXITCODE",
                "if (-not $__vbr_ok) { if ($null -ne $__vbr_code -and $__vbr_code -ne 0) { $__vbr_exit = [int]$__vbr_code } else { $__vbr_exit = 1 } } elseif ($null -ne $__vbr_code) { $__vbr_exit = [int]$__vbr_code } else { $__vbr_exit = 0 }",
                "Pop-Location",
                "Write-Output ($__vbr_marker + ':' + $__vbr_exit)",
                "[Console]::Error.WriteLine($__vbr_marker + ':stderr')"
            });
        }

        private static string BuildPosixCommand(ShellJob job, string marker)
        {
            var wd = ShellArgSanitizer.QuotePosixSingleQuoted(job.WorkingDirectory);
            var markerValue = ShellArgSanitizer.QuotePosixSingleQuoted(marker);
            return string.Join("\n", new[]
            {
                $"__vbr_marker={markerValue}",
                "(",
                $"  cd {wd} || exit 125",
                job.Command,
                // Redirect the subshell's stdin from /dev/null. The worker feeds commands into a
                // persistent shell over a single stdin pipe; without this a command that reads
                // stdin (cat, read, ssh, an interactive prompt) would consume the wrapper's own
                // trailing marker lines instead of hitting EOF, so the completion marker is never
                // emitted and the job hangs until timeout (then the worker is force-recycled).
                ") </dev/null",
                "__vbr_exit=$?",
                "printf '%s:%s\\n' \"$__vbr_marker\" \"$__vbr_exit\"",
                "printf '%s:stderr\\n' \"$__vbr_marker\" >&2"
            });
        }

        private static bool TryReadStdoutMarker(string line, string marker, out int exitCode, out string prefix)
        {
            exitCode = 1;
            prefix = string.Empty;
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            prefix = idx > 0 ? line[..idx] : string.Empty;
            var suffix = line[(idx + marker.Length)..].TrimStart();
            if (suffix.StartsWith(":", StringComparison.Ordinal))
            {
                suffix = suffix[1..].Trim();
            }

            return int.TryParse(suffix, out exitCode);
        }

        private static bool TryReadStderrMarker(string line, string marker, out string prefix)
        {
            prefix = string.Empty;
            var markerText = marker + ":stderr";
            var idx = line.IndexOf(markerText, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            prefix = idx > 0 ? line[..idx] : string.Empty;
            return true;
        }

        private static string PowerShellSingleQuote(string value) => value.Replace("'", "''");

        public async Task ResetAsync()
        {
            var process = _process;
            _process = null;
            _shell = null;

            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort cleanup.
            }
            finally
            {
                process.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ResetAsync();
            if (_stdoutTask != null)
            {
                try { await _stdoutTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            }
            if (_stderrTask != null)
            {
                try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            }
        }
    }

    private sealed class ShellJob
    {
        private readonly object _gate = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly CancellationTokenSource _cancel = new();

        public ShellJob(
            string JobId,
            string Command,
            string WorkingDirectory,
            string Shell,
            TimeSpan Timeout,
            int MaxOutputChars)
        {
            this.JobId = JobId;
            this.Command = Command;
            this.WorkingDirectory = WorkingDirectory;
            this.Shell = Shell;
            this.Timeout = Timeout;
            this.MaxOutputChars = MaxOutputChars;
        }

        public string JobId { get; }
        public string Command { get; }
        public string WorkingDirectory { get; }
        public string Shell { get; }
        public TimeSpan Timeout { get; }
        public int MaxOutputChars { get; }
        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedUtc { get; private set; }
        public DateTimeOffset? CompletedUtc { get; private set; }
        public int? ExitCode { get; private set; }
        public string? Message { get; private set; }
        public string? WorkerId { get; private set; }
        public HostShellCommandStatus Status { get; private set; } = HostShellCommandStatus.Queued;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancelRequested => _cancel.IsCancellationRequested;
        public CancellationToken CancelToken => _cancel.Token;

        public void Start(string workerId)
        {
            lock (_gate)
            {
                Status = HostShellCommandStatus.Running;
                StartedUtc = DateTimeOffset.UtcNow;
                WorkerId = workerId;
            }
        }

        public void RequestCancel() => _cancel.Cancel();

        public void AppendOutput(string value, bool isError)
        {
            lock (_gate)
            {
                AppendBounded(isError ? _stderr : _stdout, value, MaxOutputChars);
            }
        }

        public void Complete(HostShellCommandStatus status, int? exitCode, string? message)
        {
            lock (_gate)
            {
                if (CompletedUtc != null)
                {
                    return;
                }

                Status = status;
                ExitCode = exitCode;
                Message = message;
                CompletedUtc = DateTimeOffset.UtcNow;
            }

            Completion.TrySetResult();
        }

        public HostShellCommandResult ToResult(string? messageOverride = null)
        {
            lock (_gate)
            {
                return new HostShellCommandResult(
                    JobId,
                    Status,
                    Shell,
                    WorkingDirectory,
                    CreatedUtc,
                    StartedUtc,
                    CompletedUtc,
                    ExitCode,
                    _stdout.ToString(),
                    _stderr.ToString(),
                    messageOverride ?? Message,
                    WorkerId);
            }
        }

        private static void AppendBounded(StringBuilder builder, string value, int maxChars)
        {
            builder.Append(value);
            if (builder.Length <= maxChars)
            {
                return;
            }

            var overflow = builder.Length - maxChars;
            builder.Remove(0, overflow);

            // After the trim above builder.Length == maxChars, so the notice would never
            // fit if simply prepended. Reserve room for it (dropping a few more of the
            // oldest chars) so the truncation is always visible to the caller.
            var notice = $"[VibeRails truncated {overflow}+ earlier character(s)]{Environment.NewLine}";
            if (notice.Length < maxChars)
            {
                builder.Remove(0, Math.Min(notice.Length, builder.Length));
                builder.Insert(0, notice);
            }
        }
    }
}
