using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis;

/// <summary>
/// Builds the PTY spawn command for a Job's CLI, so that the thing the PTY waits on is the CLI
/// itself and the PTY therefore exits exactly when the CLI does.
///
/// This is deliberately NOT the interactive-tab path, which spawns a bare shell and types the
/// launch command into it. An interactive shell returns to its prompt when the CLI exits and so
/// never terminates — the PTY stays alive, <c>Terminal.HasExited</c> stays false, and a Job sits
/// Running forever with nothing left to finalize it.
///
/// A thin shell wrapper is still needed rather than handing the PTY the CLI executable directly:
///
///   * Pty.Net resolves a bare app name on Windows by trying the name itself, then .com, then .exe
///     (Pty.Net/Windows/PtyProvider.cs). codex/copilot/opencode install as npm shims — a .cmd, a
///     .ps1, and an extensionless sh script. The extensionless file matches first and CreateProcess
///     cannot execute it, so a direct spawn fails for every npm-installed CLI. A shell resolves
///     these correctly via PATHEXT.
///   * The MCP setup commands use shell redirection and must run in the same environment as the
///     launch, exactly as <c>ShellCommandBuilder</c> composes them for the interactive path.
///
/// The wrapper runs the setup lines and then hands off to the CLI, propagating its exit code:
/// POSIX shells <c>exec</c> so no extra process survives the handoff; PowerShell has no exec, so
/// the script ends by exiting with the CLI's code.
///
/// Arguments are passed as real argv — PowerShell splatting or POSIX single-quoting, the same
/// technique <c>BaseLlmCliLauncher</c> uses — never as an interpolated command string. That is what
/// lets a prompt containing quotes, <c>$</c>, backticks, or shell metacharacters survive intact.
/// </summary>
internal static class CliSpawnCommandBuilder
{
    public static (string App, string[] Argv) Build(
        string cliExecutable,
        IReadOnlyList<string> argv,
        IReadOnlyList<string> setupCommands)
    {
        return OperatingSystem.IsWindows()
            ? BuildWindows(cliExecutable, argv, setupCommands)
            : BuildPosix(cliExecutable, argv, setupCommands);
    }

    /// <summary>
    /// Writes a self-deleting .ps1 and points the PTY at it. The profile is intentionally NOT
    /// suppressed: the interactive path spawns a plain <c>pwsh.exe</c> which does load the user's
    /// profile, and CLIs are commonly only on PATH because of it. Suppressing it here would make
    /// Jobs fail to find a CLI that launches fine from a normal tab.
    /// </summary>
    internal static (string App, string[] Argv) BuildWindows(
        string cliExecutable,
        IReadOnlyList<string> argv,
        IReadOnlyList<string> setupCommands)
    {
        var script = new StringBuilder();
        script.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");

        // Delete before running, not after: the CLI is long-lived and the script would otherwise
        // linger in %TEMP% for the whole session (and forever if the run is killed).
        script.AppendLine("try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue } catch { }");

        foreach (var setupCommand in setupCommands)
            script.AppendLine(setupCommand);

        script.AppendLine($"$exe = {QuotePowerShellSingleQuoted(cliExecutable)}");
        script.Append("$argv = @(");
        for (var i = 0; i < argv.Count; i++)
        {
            if (i > 0) script.Append(", ");
            script.Append(QuotePowerShellSingleQuoted(argv[i]));
        }
        script.AppendLine(")");
        script.AppendLine("& $exe @argv");

        // $LASTEXITCODE is null when the CLI could not be started at all, which must not be
        // reported as success — `exit $null` exits 0.
        script.AppendLine("if ($null -eq $LASTEXITCODE) { exit 1 }");
        script.AppendLine("exit $LASTEXITCODE");

        var tempScript = Path.Combine(
            Path.GetTempPath(),
            $"viberails-job-{Guid.NewGuid():N}.ps1");

        // CreateNew, not Create: this file is about to be executed, so it must be one we made. An
        // unguarded write would happily open a file another process had already planted at that
        // path, handing pwsh whatever content it kept. A GUID name makes that a poor attack, but
        // "poor attack" is not the standard for something that runs as the user.
        using (var file = new FileStream(tempScript, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
        {
            writer.Write(script.ToString());
        }

        return (ShellDefaults.WindowsPtyShell, ["-File", tempScript]);
    }

    /// <summary>
    /// A single <c>-c</c> program: setup lines separated by <c>;</c>, then <c>exec</c> so the CLI
    /// replaces the shell outright and the PTY's process IS the CLI.
    /// </summary>
    internal static (string App, string[] Argv) BuildPosix(
        string cliExecutable,
        IReadOnlyList<string> argv,
        IReadOnlyList<string> setupCommands)
    {
        var program = new StringBuilder();
        foreach (var setupCommand in setupCommands)
        {
            program.Append(setupCommand);
            program.Append("; ");
        }

        program.Append("exec ");
        program.Append(QuotePosixSingleQuoted(cliExecutable));
        foreach (var argument in argv)
        {
            program.Append(' ');
            program.Append(QuotePosixSingleQuoted(argument));
        }

        return (ShellDefaults.GetUnixCommandShellPath(), ["-c", program.ToString()]);
    }

    private static string QuotePowerShellSingleQuoted(string value) =>
        "'" + value.Replace("'", "''") + "'";

    private static string QuotePosixSingleQuoted(string value) =>
        ShellArgSanitizer.QuotePosixSingleQuoted(value);
}
