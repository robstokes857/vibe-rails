using System.Diagnostics;
using System.Text;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Opens a native terminal running this VibeRails executable without requiring an LLM
/// Environment. Script-only Automations use this path; workflows containing a Worker continue
/// through <see cref="LlmClis.IEnvironmentLaunchService"/> so its workspace policy is preserved.
/// </summary>
public interface IJobProcessLauncher
{
    LaunchResult Launch(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool launchMinimized = false);
}

public sealed class JobProcessLauncher : IJobProcessLauncher
{
    public LaunchResult Launch(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool launchMinimized = false)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
                return new LaunchResult(false, $"The project directory no longer exists: {workingDirectory}");

            var executable = Environment.ProcessPath ?? "vb";
            if (OperatingSystem.IsWindows())
                return LaunchWindows(executable, workingDirectory, arguments, launchMinimized);
            if (OperatingSystem.IsMacOS())
                return LaunchMac(executable, arguments);
            if (OperatingSystem.IsLinux())
                return LaunchLinux(executable, arguments);

            return new LaunchResult(false, "Unsupported operating system.");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"Could not open the Automation terminal: {ex.Message}");
        }
    }

    private static LaunchResult LaunchWindows(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool launchMinimized)
    {
        // A tiny temporary script is used only to create the new terminal. PowerShell splatting
        // keeps every VibeRails argument as a distinct argv value; paths and run ids never become
        // an interpolated command line.
        var builder = new StringBuilder();
        builder.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
        builder.AppendLine($"$exe = {QuotePowerShell(executable)}");
        builder.Append("$argv = @(");
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index > 0)
                builder.Append(", ");
            builder.Append(QuotePowerShell(arguments[index]));
        }
        builder.AppendLine(")");
        builder.AppendLine("try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue } catch { }");
        builder.AppendLine("& $exe @argv");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"viberails-job-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, builder.ToString(), new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo
        {
            FileName = ShellDefaults.WindowsCommandShell,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            WindowStyle = launchMinimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        try
        {
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "pwsh (PowerShell Core) is required but was not found. Install it from https://github.com/PowerShell/PowerShell");
        }
        catch
        {
            try { File.Delete(scriptPath); } catch { }
            throw;
        }

        return new LaunchResult(true, "Automation launched in a new terminal window.");
    }

    private static LaunchResult LaunchMac(string executable, IReadOnlyList<string> arguments)
    {
        var commandLine = BuildPosixCommandLine(executable, arguments);
        var terminalCommand = MacTerminalCommandBuilder.BuildZshLaunchCommand(commandLine, keepShellOpen: false);
        _ = Process.Start(MacTerminalCommandBuilder.BuildStartInfo(terminalCommand));
        return new LaunchResult(true, "Automation launched in Terminal.app.");
    }

    private static LaunchResult LaunchLinux(string executable, IReadOnlyList<string> arguments)
    {
        var shell = ShellDefaults.LinuxShell;
        var commandLine = BuildPosixCommandLine(executable, arguments);
        (string Terminal, string[] Arguments)[] terminals =
        [
            ("gnome-terminal", ["--", shell, "-c", commandLine]),
            ("konsole", ["-e", shell, "-c", commandLine]),
            ("xfce4-terminal", ["-e", shell, "-c", commandLine]),
            ("xterm", ["-e", shell, "-c", commandLine])
        ];

        foreach (var (terminal, terminalArguments) in terminals)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = terminal,
                    UseShellExecute = false
                };
                foreach (var argument in terminalArguments)
                    startInfo.ArgumentList.Add(argument);
                _ = Process.Start(startInfo);
                return new LaunchResult(true, $"Automation launched in {terminal}.");
            }
            catch
            {
                // Try the next supported terminal emulator.
            }
        }

        return new LaunchResult(
            false,
            "No supported terminal emulator found (tried gnome-terminal, konsole, xfce4-terminal, xterm).");
    }

    private static string BuildPosixCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(MacTerminalCommandBuilder.QuotePosixSingleQuoted(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(MacTerminalCommandBuilder.QuotePosixSingleQuoted(argument));
        }
        return builder.ToString();
    }

    private static string QuotePowerShell(string value) => $"'{value.Replace("'", "''")}'";
}
