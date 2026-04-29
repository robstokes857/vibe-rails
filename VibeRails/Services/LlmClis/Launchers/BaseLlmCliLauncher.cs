using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

        public virtual Dictionary<string, string> GetEnvironmentVariables(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
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

        private string[] BuildVbArgv(string workingDirectory, string[] args, string? envName)
        {
            var envValue = !string.IsNullOrEmpty(envName) ? envName : CliExecutable;
            var argv = new List<string> { "--env", envValue, "--workdir", workingDirectory };
            if (args.Length > 0)
            {
                argv.Add("--");
                argv.AddRange(args);
            }
            return argv.ToArray();
        }

        private LaunchResult LaunchInWindowsTerminal(
            string workingDirectory,
            string[] args,
            string? envName)
        {
            var exePath = Environment.ProcessPath ?? "vb";
            var argv = BuildVbArgv(workingDirectory, args, envName);

            // Build a temp .ps1 script and let PowerShell's call operator (`&`) plus
            // splatting (`@argv`) pass each arg as its own argv element. This avoids
            // every nested-quote pitfall of `pwsh -Command "..."` when args contain
            // spaces, quotes, $, or backticks.
            var sb = new StringBuilder();
            sb.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            sb.AppendLine($"$exe = {QuotePowerShellSingleQuoted(exePath)}");
            sb.Append("$argv = @(");
            for (int i = 0; i < argv.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(QuotePowerShellSingleQuoted(argv[i]));
            }
            sb.AppendLine(")");
            sb.AppendLine("try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue } catch { }");
            sb.AppendLine("& $exe @argv");

            var tempScript = Path.Combine(
                Path.GetTempPath(),
                $"viberails-launch-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempScript, sb.ToString(), new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add("-NoExit");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(tempScript);

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch
            {
                try { File.Delete(tempScript); } catch { }
                throw;
            }

            if (process == null)
            {
                try { File.Delete(tempScript); } catch { }
                throw new InvalidOperationException("pwsh (PowerShell Core) is required but was not found. Please install PowerShell Core from https://github.com/PowerShell/PowerShell");
            }

            return new LaunchResult(
                Success: true,
                Message: $"{CliExecutable} launched in new terminal window with session tracking",
                ProcessId: null
            );
        }

        private LaunchResult LaunchInMacTerminal(
            string workingDirectory,
            string[] args,
            string? envName)
        {
            var exePath = Environment.ProcessPath ?? "vb";
            var argv = BuildVbArgv(workingDirectory, args, envName);

            // Build a single bash command line with POSIX single-quote escaping per arg.
            var bashCommand = BuildPosixCommandLine(exePath, argv);

            // Wrap it in AppleScript's `do script "..."` (double-quoted) — escape \ and " for AppleScript.
            var appleScript = $"tell application \"Terminal\" to do script \"{EscapeForAppleScriptDoubleQuoted(bashCommand)}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(appleScript);

            Process.Start(startInfo);

            return new LaunchResult(
                Success: true,
                Message: $"{CliExecutable} launched in new terminal window with session tracking",
                ProcessId: null
            );
        }

        private LaunchResult LaunchInLinuxTerminal(
            string workingDirectory,
            string[] args,
            string? envName)
        {
            var exePath = Environment.ProcessPath ?? "vb";
            var argv = BuildVbArgv(workingDirectory, args, envName);

            // POSIX-quoted single command line for `bash -c <fullCommand>`.
            var fullCommand = BuildPosixCommandLine(exePath, argv) + "; exec bash";

            // Pass via ArgumentList so each element is a discrete argv entry — no
            // string-join quoting collisions with the inner POSIX-quoted command.
            (string terminal, string[] terminalArgs)[] terminals =
            [
                ("gnome-terminal", ["--", "bash", "-c", fullCommand]),
                ("konsole",        ["-e", "bash", "-c", fullCommand]),
                ("xfce4-terminal", ["-e", "bash", "-c", fullCommand]),
                ("xterm",          ["-e", "bash", "-c", fullCommand]),
            ];

            foreach (var (terminal, terminalArgs) in terminals)
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = terminal,
                        UseShellExecute = false,
                    };
                    foreach (var a in terminalArgs)
                        startInfo.ArgumentList.Add(a);

                    Process.Start(startInfo);

                    return new LaunchResult(
                        Success: true,
                        Message: $"{CliExecutable} launched in {terminal} with session tracking",
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

        private static string BuildPosixCommandLine(string exePath, string[] argv)
        {
            var sb = new StringBuilder();
            sb.Append(QuotePosixSingleQuoted(exePath));
            foreach (var a in argv)
            {
                sb.Append(' ');
                sb.Append(QuotePosixSingleQuoted(a));
            }
            return sb.ToString();
        }

        private static string QuotePosixSingleQuoted(string s)
            => "'" + s.Replace("'", "'\\''") + "'";

        private static string QuotePowerShellSingleQuoted(string s)
            => "'" + s.Replace("'", "''") + "'";

        private static string EscapeForAppleScriptDoubleQuoted(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
