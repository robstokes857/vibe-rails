using System.Diagnostics;
using System.Runtime.InteropServices;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis.Launchers
{
    public record LaunchResult(bool Success, string Message, int? ProcessId = null);

    public interface IBaseLlmCliLauncher
    {
        LLM LlmType { get; }
        string CliExecutable { get; }
        string ConfigEnvVarName { get; }
        LaunchResult LaunchInTerminal(string? envName, string workingDirectory, string[] args);
        Dictionary<string, string> GetEnvironmentVariables(string envName);
    }

    public abstract class BaseLlmCliLauncher : IBaseLlmCliLauncher
    {
        public abstract LLM LlmType { get; }
        public abstract string CliExecutable { get; }
        public abstract string ConfigEnvVarName { get; }
        protected abstract string ConfigSubdirectory { get; }

        public Dictionary<string, string> GetEnvironmentVariables(string envName)
        {
            var envBasePath = Configs.GetEnvPath();
            var configPath = Path.Combine(envBasePath, envName, ConfigSubdirectory);

            return new Dictionary<string, string>
            {
                [ConfigEnvVarName] = configPath
            };
        }

        public LaunchResult LaunchInTerminal(
            string? envName,
            string workingDirectory,
            string[] args)
        {
            try
            {
                var envVars = !string.IsNullOrEmpty(envName)
                    ? GetEnvironmentVariables(envName)
                    : new Dictionary<string, string>();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return LaunchInWindowsTerminal(workingDirectory, args, envName);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return LaunchInMacTerminal(workingDirectory, args, envName);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return LaunchInLinuxTerminal(workingDirectory, args, envName);
                }
                else
                {
                    return new LaunchResult(
                        Success: false,
                        Message: "Unsupported operating system",
                        ProcessId: null
                    );
                }
            }
            catch (Exception ex)
            {
                return new LaunchResult(
                    Success: false,
                    Message: $"Failed to launch {CliExecutable}: {ex.Message}",
                    ProcessId: null
                );
            }
        }

        private LaunchResult LaunchInWindowsTerminal(
            string workingDirectory,
            string[] args,
            string? envName)
        {
            // Get the path to the current executable (vb)
            var exePath = Environment.ProcessPath ?? "vb";

            // Build the --env command (unified flag for both base CLIs and custom environments)
            var envValue = !string.IsNullOrEmpty(envName) ? $"\"{envName}\"" : CliExecutable;
            var bootstrapArgs = $"--env {envValue} --workdir \"{workingDirectory}\"";

            // Add any additional args
            if (args.Length > 0)
            {
                bootstrapArgs += " -- " + string.Join(" ", args);
            }

            // Launch in a new pwsh (PowerShell Core) window
            // Set UTF-8 encoding first for proper Unicode box-drawing character support
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoExit -NoProfile -Command \"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; & '\"{exePath}\"' {bootstrapArgs}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            });

            if (process == null)
            {
                throw new InvalidOperationException("pwsh (PowerShell Core) is required but was not found. Please install PowerShell Core from https://github.com/PowerShell/PowerShell");
            }

            return new LaunchResult(
                Success: true,
                Message: $"{CliExecutable} launched in new terminal window via LMBootstrap",
                ProcessId: null
            );
        }

        private LaunchResult LaunchInMacTerminal(
            string workingDirectory,
            string[] args,
            string? envName)
        {
            // Get the path to the current executable (vb)
            var exePath = Environment.ProcessPath ?? "vb";

            // Build the --env command (unified flag for both base CLIs and custom environments)
            var envValue = !string.IsNullOrEmpty(envName) ? $"\"{envName}\"" : CliExecutable;
            var bootstrapArgs = $"--env {envValue} --workdir \"{workingDirectory}\"";

            // Add any additional args
            if (args.Length > 0)
            {
                bootstrapArgs += " -- " + string.Join(" ", args);
            }

            // Use osascript to open Terminal.app and run vb --env
            var script = $"tell application \"Terminal\" to do script \"\\\"{exePath}\\\" {bootstrapArgs}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{script}\"",
                UseShellExecute = true
            });

            return new LaunchResult(
                Success: true,
                Message: $"{CliExecutable} launched in new terminal window via LMBootstrap",
                ProcessId: null
            );
        }

        private LaunchResult LaunchInLinuxTerminal(
            string workingDirectory,
            string[] args,
            string? envName)
        {
            // Get the path to the current executable (vb)
            var exePath = Environment.ProcessPath ?? "vb";

            // Build the --env command (unified flag for both base CLIs and custom environments)
            var envValue = !string.IsNullOrEmpty(envName) ? $"\"{envName}\"" : CliExecutable;
            var bootstrapArgs = $"--env {envValue} --workdir \"{workingDirectory}\"";

            // Add any additional args
            if (args.Length > 0)
            {
                bootstrapArgs += " -- " + string.Join(" ", args);
            }

            // Full command to run vb --env
            var fullCommand = $"\"{exePath}\" {bootstrapArgs}; exec bash";

            // Try common terminal emulators in order of preference
            var terminals = new (string terminal, string[] terminalArgs)[]
            {
                ("gnome-terminal", new[] { "--", "bash", "-c", fullCommand }),
                ("konsole", new[] { "-e", "bash", "-c", fullCommand }),
                ("xfce4-terminal", new[] { "-e", $"bash -c '{fullCommand}'" }),
                ("xterm", new[] { "-e", $"bash -c '{fullCommand}'" })
            };

            foreach (var (terminal, terminalArgs) in terminals)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = terminal,
                        Arguments = string.Join(" ", terminalArgs),
                        UseShellExecute = true
                    });

                    return new LaunchResult(
                        Success: true,
                        Message: $"{CliExecutable} launched in {terminal} via LMBootstrap",
                        ProcessId: null
                    );
                }
                catch
                {
                    // Terminal not found, try next one
                }
            }

            return new LaunchResult(
                Success: false,
                Message: "No supported terminal emulator found (tried gnome-terminal, konsole, xfce4-terminal, xterm)",
                ProcessId: null
            );
        }
    }
}
