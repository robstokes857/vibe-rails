using System.Diagnostics;
using System.Text;
using Serilog;

namespace VibeRails.Services.Jobs;

public interface IJobScheduleTaskInstaller
{
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(CancellationToken cancellationToken = default);
    Task UninstallAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Registers the per-minute OS task that runs <c>vb --job-tick</c>, so scheduled Jobs fire whenever
/// the user is logged in rather than only while the dashboard happens to be open.
///
/// The task must run with access to the **interactive desktop**, because a Job's whole point is to
/// open a terminal window the user can watch. On Windows that means <c>/IT</c> (interactive token,
/// "run only when the user is logged on"). Without it Task Scheduler performs a batch logon, the
/// process lands in a non-interactive window station, and any window it creates is rendered to a
/// desktop nobody is looking at — succeeding silently while being completely invisible. Never add
/// <c>/RU SYSTEM</c> or a stored password here; both force the invisible mode.
///
/// The same requirement drives the other platforms: macOS uses a LaunchAgent bootstrapped into the
/// user's <c>gui/</c> domain (a LaunchDaemon would have no desktop), and Linux uses a systemd
/// **user** timer, which inherits the graphical session's environment.
/// </summary>
public sealed class JobScheduleTaskInstaller : IJobScheduleTaskInstaller
{
    private const string WindowsTaskName = "VibeRailsJobs";
    private const string LaunchAgentLabel = "com.viberails.jobs";
    private const string SystemdUnitName = "viberails-jobs.timer";
    private const string SystemdServiceName = "viberails-jobs.service";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await RunAsync("schtasks.exe", ["/Query", "/TN", WindowsTaskName], cancellationToken, throwOnFailure: false);
            return result.ExitCode == 0;
        }
        if (OperatingSystem.IsMacOS())
            return File.Exists(LaunchAgentPath);
        if (OperatingSystem.IsLinux())
            return File.Exists(SystemdTimerPath);
        return false;
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            await InstallWindowsAsync(cancellationToken);
        else if (OperatingSystem.IsMacOS())
            await InstallMacAsync(cancellationToken);
        else if (OperatingSystem.IsLinux())
            await InstallLinuxAsync(cancellationToken);
        else
            throw new PlatformNotSupportedException("Scheduled Jobs are not supported on this operating system.");
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunIgnoringFailureAsync("schtasks.exe", ["/End", "/TN", WindowsTaskName], cancellationToken);
            await RunIgnoringFailureAsync("schtasks.exe", ["/Delete", "/TN", WindowsTaskName, "/F"], cancellationToken);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var domain = $"gui/{await GetUnixUserIdAsync(cancellationToken)}";
            await RunIgnoringFailureAsync("launchctl", ["bootout", domain, LaunchAgentPath], cancellationToken);
            TryDelete(LaunchAgentPath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await RunIgnoringFailureAsync("systemctl", ["--user", "disable", "--now", SystemdUnitName], cancellationToken);
            TryDelete(SystemdTimerPath);
            TryDelete(SystemdServicePath);
            await RunIgnoringFailureAsync("systemctl", ["--user", "daemon-reload"], cancellationToken);
        }
    }

    private async Task InstallWindowsAsync(CancellationToken cancellationToken)
    {
        var (executable, arguments) = GetTickCommand();
        var taskCommand = BuildWindowsTaskCommand(executable, arguments);

        await RunIgnoringFailureAsync("schtasks.exe", ["/End", "/TN", WindowsTaskName], cancellationToken);
        // /IT is load-bearing: it is what gives the tick — and therefore the terminal windows it
        // spawns — access to the logged-on user's desktop. See the class remarks.
        await RunAsync("schtasks.exe",
            ["/Create", "/TN", WindowsTaskName, "/TR", taskCommand, "/SC", "MINUTE", "/MO", "1", "/IT", "/F"],
            cancellationToken);
    }

    private async Task InstallMacAsync(CancellationToken cancellationToken)
    {
        var (executable, arguments) = GetTickCommand();
        Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
        var argumentNodes = new[] { executable }.Concat(arguments)
            .Select(value => $"        <string>{EscapeXml(value)}</string>");

        // StartInterval (not KeepAlive): this is a transient tick that exits, not a daemon to be
        // restarted. KeepAlive would relaunch it in a tight loop the moment it finished.
        var plist = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{{LaunchAgentLabel}}</string>
                <key>ProgramArguments</key>
                <array>
            {{string.Join(Environment.NewLine, argumentNodes)}}
                </array>
                <key>StartInterval</key>
                <integer>60</integer>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """;
        await File.WriteAllTextAsync(LaunchAgentPath, plist, Encoding.UTF8, cancellationToken);

        var domain = $"gui/{await GetUnixUserIdAsync(cancellationToken)}";
        await RunIgnoringFailureAsync("launchctl", ["bootout", domain, LaunchAgentPath], cancellationToken);
        await RunAsync("launchctl", ["bootstrap", domain, LaunchAgentPath], cancellationToken);
    }

    private async Task InstallLinuxAsync(CancellationToken cancellationToken)
    {
        var (executable, arguments) = GetTickCommand();
        Directory.CreateDirectory(Path.GetDirectoryName(SystemdTimerPath)!);
        var execStart = string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteSystemdArgument));

        // Type=oneshot: the tick runs and exits; the .timer re-runs it. A systemd *user* unit is
        // required (not --system) so it shares the graphical session and can open a terminal.
        var service = $"""
            [Unit]
            Description=VibeRails Jobs tick

            [Service]
            Type=oneshot
            ExecStart={execStart}
            """;

        var timer = $"""
            [Unit]
            Description=VibeRails Jobs tick (every minute)

            [Timer]
            OnBootSec=1min
            OnUnitActiveSec=1min
            AccuracySec=10s
            Unit={SystemdServiceName}

            [Install]
            WantedBy=timers.target
            """;

        await File.WriteAllTextAsync(SystemdServicePath, service, Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(SystemdTimerPath, timer, Encoding.UTF8, cancellationToken);
        await RunAsync("systemctl", ["--user", "daemon-reload"], cancellationToken);
        await RunAsync("systemctl", ["--user", "enable", "--now", SystemdUnitName], cancellationToken);
    }

    /// <summary>
    /// Resolves this executable plus <c>--job-tick</c>, handling the `dotnet Foo.dll` dev case where
    /// ProcessPath is the shared host rather than the app.
    /// </summary>
    private static (string Executable, IReadOnlyList<string> Arguments) GetTickCommand()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the VibeRails executable path.");
        var entryArgument = Environment.GetCommandLineArgs().FirstOrDefault();
        var isDotnetHost = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && entryArgument?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true;
        return isDotnetHost
            ? (executable, new[] { Path.GetFullPath(entryArgument!), "--job-tick" })
            : (executable, new[] { "--job-tick" });
    }

    private static string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");

    private static string SystemdTimerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", SystemdUnitName);

    private static string SystemdServicePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", SystemdServiceName);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warning(ex, "[Jobs] Could not delete {Path}", path); }
    }

    internal static string BuildWindowsTaskCommand(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteWindowsTaskArgument));

    /// <summary>
    /// Quotes one argument for the command line stored in Task Scheduler's /TR value. This returns
    /// real quote characters: /TR is already passed as one ArgumentList element, so prefixing quotes
    /// with a backslash would persist those backslashes into the scheduled action.
    /// </summary>
    private static string QuoteWindowsTaskArgument(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;

        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        // Backslashes before the closing quote must be doubled so they remain literal.
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    /// <summary>
    /// systemd's ExecStart parser expands <c>%</c> specifiers and <c>$VAR</c> references even inside
    /// double quotes, so both have to be doubled, and it processes C-style escapes, so backslashes
    /// and quotes have to be escaped too. Missing <c>$</c> here would misparse any install path
    /// containing one — <c>/home/$USER/...</c> is the realistic case.
    /// </summary>
    internal static string QuoteSystemdArgument(string value) =>
        $"\"{value.Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    internal static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static async Task<string> GetUnixUserIdAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("id", ["-u"], cancellationToken, throwOnFailure: false);
        var value = result.StandardOutput.Trim();
        return string.IsNullOrEmpty(value) ? "501" : value;
    }

    private static async Task RunIgnoringFailureAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try { await RunAsync(executable, arguments, cancellationToken, throwOnFailure: false); }
        catch (Exception ex) { Log.Debug(ex, "[Jobs] Ignoring failure of {Executable}", executable); }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{executable} did not exit within {CommandTimeout.TotalSeconds:N0}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{executable} failed with exit code {process.ExitCode}. {stderr.Trim()}".Trim());
        }
        return (process.ExitCode, stdout, stderr);
    }
}
