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
        /// <summary>
        /// Opens a real OS terminal window running <c>vb --env …</c>.
        /// <paramref name="launchLlm"/> is the requested CLI identity written to <c>--env</c> for
        /// base launches; it can differ from this launcher's type for OpenCode-backed pseudo-CLIs.
        /// <paramref name="args"/> are passed through to the underlying CLI (after <c>--</c>);
        /// <paramref name="vbArgs"/> are vb's own flags (e.g. <c>--job-run</c>, <c>--max-runtime</c>)
        /// and must precede <c>--</c>, because ArgumentParser stops parsing there.
        /// <paramref name="keepTerminalOpen"/> leaves a usable shell behind after vb exits, which is
        /// what an interactive launch wants; Job launches pass false so each run's window closes
        /// with the run instead of accumulating one idle shell per run.
        /// <paramref name="launchMinimized"/> requests a minimized terminal window where the
        /// operating system supports it. It is currently honored on Windows and ignored elsewhere.
        /// </summary>
        LaunchResult LaunchInTerminal(
            LLM launchLlm,
            string? envName,
            string workingDirectory,
            string[] args,
            string[]? vbArgs = null,
            bool keepTerminalOpen = true,
            bool launchMinimized = false);
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
            // Containment guard: envName can reach here unvalidated from the launch routes.
            var envDir = EnvironmentNameValidator.ResolveEnvironmentDirectory(envBasePath, envName);
            var configPath = Path.Combine(envDir, ConfigSubdirectory);

            return new Dictionary<string, string>
            {
                [ConfigEnvVarName] = configPath
            };
        }

        public LaunchResult LaunchInTerminal(
            LLM launchLlm,
            string? envName,
            string workingDirectory,
            string[] args,
            string[]? vbArgs = null,
            bool keepTerminalOpen = true,
            bool launchMinimized = false)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return LaunchInWindowsTerminal(
                        launchLlm,
                        workingDirectory,
                        args,
                        envName,
                        vbArgs,
                        keepTerminalOpen,
                        launchMinimized);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return LaunchInMacTerminal(
                        launchLlm, workingDirectory, args, envName, vbArgs, keepTerminalOpen);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return LaunchInLinuxTerminal(
                        launchLlm, workingDirectory, args, envName, vbArgs, keepTerminalOpen);
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

        internal static string[] BuildVbArgv(
            LLM launchLlm,
            string workingDirectory,
            string[] args,
            string? envName,
            string[]? vbArgs = null)
        {
            // Base-CLI launches pass the requested LLM's wire name as --env (that's what the
            // bootstrap resolves), NOT the selected launcher's type or executable. These coincide
            // for most CLIs, but Antigravity's binary is `agy`, native Grok's is `grok` (wire
            // name `grok-4.6`), and the OpenCode-backed GLM pseudo-CLIs reuse an OpenCode
            // launcher while retaining hyphenated wire names.
            var envValue = !string.IsNullOrEmpty(envName)
                ? envName
                : LlmParser.ToWireName(launchLlm).ToLowerInvariant();
            var argv = new List<string> { "--env", envValue, "--workdir", workingDirectory };
            // vb's own flags must land BEFORE the `--` separator: ArgumentParser treats everything
            // after `--` as CLI passthrough and stops parsing entirely.
            if (vbArgs is { Length: > 0 })
            {
                argv.AddRange(vbArgs);
            }
            if (args.Length > 0)
            {
                argv.Add("--");
                argv.AddRange(args);
            }
            return argv.ToArray();
        }

        private LaunchResult LaunchInWindowsTerminal(
            LLM launchLlm,
            string workingDirectory,
            string[] args,
            string? envName,
            string[]? vbArgs,
            bool keepTerminalOpen,
            bool launchMinimized)
        {
            var exePath = Environment.ProcessPath ?? "vb";
            var argv = BuildVbArgv(launchLlm, workingDirectory, args, envName, vbArgs);

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
                FileName = ShellDefaults.WindowsCommandShell,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                WindowStyle = launchMinimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
            };
            if (keepTerminalOpen)
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
            LLM launchLlm,
            string workingDirectory,
            string[] args,
            string? envName,
            string[]? vbArgs,
            bool keepTerminalOpen)
        {
            var exePath = Environment.ProcessPath ?? "vb";
            var argv = BuildVbArgv(launchLlm, workingDirectory, args, envName, vbArgs);

            // Build a zsh-targeted Terminal.app command with POSIX single-quote escaping per arg.
            var commandLine = BuildPosixCommandLine(exePath, argv);
            var terminalCommand = MacTerminalCommandBuilder.BuildZshLaunchCommand(commandLine, keepTerminalOpen);

            // Wrap it in AppleScript's `do script "..."` (double-quoted) — escape \ and " for AppleScript.
            var startInfo = MacTerminalCommandBuilder.BuildStartInfo(terminalCommand);

            Process.Start(startInfo);

            return new LaunchResult(
                Success: true,
                Message: $"{CliExecutable} launched in new terminal window with session tracking",
                ProcessId: null
            );
        }

        private LaunchResult LaunchInLinuxTerminal(
            LLM launchLlm,
            string workingDirectory,
            string[] args,
            string? envName,
            string[]? vbArgs,
            bool keepTerminalOpen)
        {
            var exePath = Environment.ProcessPath ?? "vb";
            var argv = BuildVbArgv(launchLlm, workingDirectory, args, envName, vbArgs);

            // POSIX-quoted single command line for `<shell> -c <fullCommand>`.
            var shell = ShellDefaults.LinuxShell;
            var fullCommand = BuildPosixCommandLine(exePath, argv);
            if (keepTerminalOpen)
                fullCommand += $"; exec {shell}";

            // Pass via ArgumentList so each element is a discrete argv entry — no
            // string-join quoting collisions with the inner POSIX-quoted command.
            (string terminal, string[] terminalArgs)[] terminals =
            [
                ("gnome-terminal", ["--", shell, "-c", fullCommand]),
                ("konsole",        ["-e", shell, "-c", fullCommand]),
                ("xfce4-terminal", ["-e", shell, "-c", fullCommand]),
                ("xterm",          ["-e", shell, "-c", fullCommand]),
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
            sb.Append(MacTerminalCommandBuilder.QuotePosixSingleQuoted(exePath));
            foreach (var a in argv)
            {
                sb.Append(' ');
                sb.Append(MacTerminalCommandBuilder.QuotePosixSingleQuoted(a));
            }
            return sb.ToString();
        }

        private static string QuotePowerShellSingleQuoted(string s)
            => "'" + s.Replace("'", "''") + "'";
    }
}
