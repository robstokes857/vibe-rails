using System.Diagnostics;
using System.Reflection;
using System.Text;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.VCA.Hooks;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public interface IJobWorkerSupervisor
{
    Task<JobWorkerStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task EnsureInstalledAndRunningAsync(CancellationToken cancellationToken = default);
    Task RepairAsync(CancellationToken cancellationToken = default);
    Task DisableAsync(CancellationToken cancellationToken = default);
}

public sealed class JobWorkerSupervisor(IJobStore store) : IJobWorkerSupervisor
{
    private const string WindowsTaskName = "VibeRails Jobs Worker";
    private const string LaunchAgentLabel = "ai.viberails.jobs";
    private const string SystemdUnitName = "viberails-jobs.service";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HealthyHeartbeatAge = TimeSpan.FromSeconds(20);

    public async Task<JobWorkerStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var installed = await IsInstalledAsync(cancellationToken);
        var lease = await store.GetWorkerLeaseAsync(cancellationToken);
        return BuildStatus(installed, lease, DateTime.UtcNow, CurrentVersion);
    }

    public async Task EnsureInstalledAndRunningAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.NeedsRepair)
            return;
        await InstallAndStartAsync(cancellationToken);
    }

    public Task RepairAsync(CancellationToken cancellationToken = default) =>
        InstallAndStartAsync(cancellationToken);

    private async Task InstallAndStartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            await InstallWindowsAsync(cancellationToken);
        else if (OperatingSystem.IsMacOS())
            await InstallMacAsync(cancellationToken);
        else if (OperatingSystem.IsLinux())
            await InstallLinuxAsync(cancellationToken);
        else
            throw new PlatformNotSupportedException("Jobs supervision is supported on Windows, macOS, and Linux.");
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunIgnoringFailureAsync("schtasks.exe", ["/End", "/TN", WindowsTaskName], cancellationToken);
            await RunIgnoringFailureAsync("schtasks.exe", ["/Delete", "/TN", WindowsTaskName, "/F"], cancellationToken);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var domain = $"gui/{await GetUnixUserIdAsync(cancellationToken)}";
            await RunIgnoringFailureAsync("launchctl", ["bootout", domain, LaunchAgentPath], cancellationToken);
            if (File.Exists(LaunchAgentPath))
                File.Delete(LaunchAgentPath);
        }
        else if (OperatingSystem.IsLinux())
        {
            await RunIgnoringFailureAsync("systemctl", ["--user", "disable", "--now", SystemdUnitName], cancellationToken);
            if (File.Exists(SystemdUnitPath))
                File.Delete(SystemdUnitPath);
            await RunIgnoringFailureAsync("systemctl", ["--user", "daemon-reload"], cancellationToken);
        }

        await store.MarkRunningRunsInterruptedAsync(
            "The Jobs worker was disabled while this run was active.",
            cancellationToken);
        var lease = await store.GetWorkerLeaseAsync(cancellationToken);
        if (lease is not null)
            await store.DeleteWorkerLeaseAsync(lease.InstanceId, cancellationToken);
    }

    private async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await RunAsync("schtasks.exe", ["/Query", "/TN", WindowsTaskName], cancellationToken, throwOnFailure: false);
            return result.ExitCode == 0;
        }
        if (OperatingSystem.IsMacOS())
            return File.Exists(LaunchAgentPath);
        if (OperatingSystem.IsLinux())
            return File.Exists(SystemdUnitPath);
        return false;
    }

    private async Task InstallWindowsAsync(CancellationToken cancellationToken)
    {
        var command = GetWorkerCommand();
        var taskCommand = string.Join(' ', new[] { command.Executable }.Concat(command.Arguments).Select(QuoteWindowsTaskArgument));

        // A per-minute user task acts as both initial trigger and crash recovery. Duplicate
        // launches exit immediately because the worker also owns a durable SQLite lease.
        await RunIgnoringFailureAsync("schtasks.exe", ["/End", "/TN", WindowsTaskName], cancellationToken);
        await RunAsync("schtasks.exe",
            ["/Create", "/TN", WindowsTaskName, "/TR", taskCommand, "/SC", "MINUTE", "/MO", "1", "/F"],
            cancellationToken);
        await RunAsync("schtasks.exe", ["/Run", "/TN", WindowsTaskName], cancellationToken);
    }

    private async Task InstallMacAsync(CancellationToken cancellationToken)
    {
        var command = GetWorkerCommand();
        Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
        var arguments = new[] { command.Executable }.Concat(command.Arguments)
            .Select(value => $"        <string>{EscapeXml(value)}</string>");
        var plist = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{{LaunchAgentLabel}}</string>
                <key>ProgramArguments</key>
                <array>
            {{string.Join(Environment.NewLine, arguments)}}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>ProcessType</key>
                <string>Background</string>
            </dict>
            </plist>
            """;
        await File.WriteAllTextAsync(LaunchAgentPath, plist, Encoding.UTF8, cancellationToken);

        var domain = $"gui/{await GetUnixUserIdAsync(cancellationToken)}";
        await RunIgnoringFailureAsync("launchctl", ["bootout", domain, LaunchAgentPath], cancellationToken);
        await RunAsync("launchctl", ["bootstrap", domain, LaunchAgentPath], cancellationToken);
        await RunAsync("launchctl", ["kickstart", "-k", $"{domain}/{LaunchAgentLabel}"], cancellationToken);
    }

    private async Task InstallLinuxAsync(CancellationToken cancellationToken)
    {
        var command = GetWorkerCommand();
        Directory.CreateDirectory(Path.GetDirectoryName(SystemdUnitPath)!);
        var execStart = string.Join(' ', new[] { command.Executable }.Concat(command.Arguments).Select(QuoteSystemdArgument));
        var unit = $$"""
            [Unit]
            Description=VibeRails Jobs worker

            [Service]
            Type=simple
            ExecStart={{execStart}}
            Restart=always
            RestartSec=5

            [Install]
            WantedBy=default.target
            """;
        await File.WriteAllTextAsync(SystemdUnitPath, unit, Encoding.UTF8, cancellationToken);
        await RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken);
        await RunAsync("systemctl", ["--user", "enable", SystemdUnitName], cancellationToken);
        await RunAsync("systemctl", ["--user", "restart", SystemdUnitName], cancellationToken);
    }

    private static WorkerCommand GetWorkerCommand()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the VibeRails executable path.");
        var entryArgument = Environment.GetCommandLineArgs().FirstOrDefault();
        var isDotnetHost = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && entryArgument?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true;
        return isDotnetHost
            ? new WorkerCommand(executable, [Path.GetFullPath(entryArgument!), "--jobs-worker"])
            : new WorkerCommand(executable, ["--jobs-worker"]);
    }

    private static string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");

    private static string SystemdUnitPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", SystemdUnitName);

    private static string QuoteWindowsTaskArgument(string value) =>
        VcaHookProcessLaunch.QuoteArgument(value);

    private static string QuoteSystemdArgument(string value) =>
        $"\"{value.Replace("%", "%%", StringComparison.Ordinal).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private static async Task<int> GetUnixUserIdAsync(CancellationToken cancellationToken)
    {
        var value = Environment.GetEnvironmentVariable("UID");
        if (int.TryParse(value, out var uid))
            return uid;

        var result = await RunAsync("id", ["-u"], cancellationToken);
        return int.TryParse(result.StdOut.Trim(), out uid)
            ? uid
            : throw new InvalidOperationException("Unable to determine the current macOS user id.");
    }

    private static async Task RunIgnoringFailureAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _ = await RunAsync(executable, arguments, cancellationToken, throwOnFailure: false);
    }

    private static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            return new CommandResult(-1, string.Empty, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"Timed out while running {executable}.");
            throw;
        }

        var result = new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        if (throwOnFailure && result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new InvalidOperationException($"{executable} failed: {details.Trim()}");
        }
        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Jobs] Could not stop supervision command process {ProcessId}", process.Id);
        }
    }

    private sealed record WorkerCommand(string Executable, IReadOnlyList<string> Arguments);
    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

    private static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    internal static JobWorkerStatusResponse BuildStatus(
        bool installed,
        JobWorkerLeaseRecord? lease,
        DateTime nowUtc,
        string currentVersion)
    {
        var heartbeatUtc = lease?.HeartbeatUtc;
        var running = heartbeatUtc is not null && nowUtc.ToUniversalTime() - heartbeatUtc.Value <= HealthyHeartbeatAge;
        var versionCurrent = running && string.Equals(lease?.Version, currentVersion, StringComparison.Ordinal);
        var healthy = installed && versionCurrent;
        var state = healthy
            ? "healthy"
            : running
                ? installed ? "outdated" : "unregistered"
                : installed ? "stopped" : "not-installed";
        var message = healthy
            ? "The Jobs worker is running independently of the web app."
            : running && !installed
                ? "A Jobs worker is running but is not registered for automatic restart; repair it."
            : running
                ? $"Jobs worker version {lease?.Version ?? "unknown"} is still running; repair it to use VibeRails {currentVersion}."
                : installed
                    ? "The Jobs worker is registered but has not reported a recent heartbeat."
                    : "The Jobs worker is not registered for this user.";

        return new JobWorkerStatusResponse(
            state,
            installed,
            running,
            NeedsRepair: !healthy,
            lease?.Version,
            heartbeatUtc,
            message);
    }
}
