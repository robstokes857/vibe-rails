using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services.Cli;

/// <summary>
/// A temp script on disk plus the invocation that runs it. Disposing best-effort deletes the file;
/// the script also deletes itself on its first line, so a detached window that outlives us still
/// leaves nothing behind in %TEMP%.
/// </summary>
internal sealed class CliScriptFile : IDisposable
{
    public CliScriptFile(string path, string executable, IReadOnlyList<string> arguments)
    {
        Path = path;
        Executable = executable;
        Arguments = arguments;
    }

    public string Path { get; }
    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* already self-deleted, or held open */ }
    }
}

/// <summary>
/// Builds the shell script a step actually runs, and the invocation that runs it.
///
/// Both the visible-window path and the hidden "Test this step" path go through here, which is the
/// whole point: a step that passes its test must behave the same at launch, so PATH resolution,
/// profile loading, the working-directory guard, and exit-code capture cannot be allowed to
/// diverge between the two.
///
/// Two deliberate departures from <c>BaseLlmCliLauncher</c>:
/// <list type="bullet">
/// <item>No <c>-NoExit</c> — the window closes when the script ends (a failing step holds it open
/// itself, with the exit code on screen).</item>
/// <item>No <c>-NoProfile</c>, and a login shell on POSIX. Steps run user commands (<c>npm</c>,
/// <c>nvm</c>, <c>pyenv</c>) that are frequently only on PATH because of the profile. This is the
/// same reasoning <c>CliSpawnCommandBuilder</c> documents for the Job path.</item>
/// </list>
/// </summary>
internal static class TerminalScriptBuilder
{
    /// <summary>Exit code reported when the working directory could not be entered.</summary>
    public const int WorkingDirectoryUnavailableExitCode = 125;

    /// <summary>Exit code reported when a step's script could not be started at all.</summary>
    public const int LaunchFailedExitCode = -1;

    /// <summary>
    /// Builds the full script text for the host platform. <paramref name="sentinelPath"/> null means
    /// "no sentinel" — the captured path already gets the exit code from the process handle.
    /// </summary>
    public static string BuildScript(
        string commandBody,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        string? sentinelPath,
        bool pauseOnFailure,
        string? selfDeletePath,
        bool isWindows)
        => isWindows
            ? BuildPowerShellScript(commandBody, workingDirectory, environmentVariables, sentinelPath, pauseOnFailure, selfDeletePath)
            : BuildPosixScript(commandBody, workingDirectory, environmentVariables, sentinelPath, pauseOnFailure, selfDeletePath);

    internal static string BuildPowerShellScript(
        string commandBody,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        string? sentinelPath,
        bool pauseOnFailure,
        string? selfDeletePath)
    {
        var script = new StringBuilder();
        script.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");

        // Delete before running, not after: a step's window can outlive this process, and a step
        // that hangs would otherwise leave its script in %TEMP% forever.
        if (selfDeletePath is not null)
            script.AppendLine($"try {{ Remove-Item -LiteralPath {Ps(selfDeletePath)} -Force -ErrorAction SilentlyContinue }} catch {{ }}");

        var writeSentinel = sentinelPath is not null;
        if (writeSentinel)
            script.AppendLine($"$__vbr_sentinel = {Ps(sentinelPath!)}");

        AppendPowerShellEnvironment(script, environmentVariables);

        // Set-Location's failure is non-terminating by default, which would silently run the
        // command in the wrong directory. -ErrorAction Stop turns it into something catchable.
        var cdFail = writeSentinel
            ? $"Set-Content -LiteralPath $__vbr_sentinel -Value '{WorkingDirectoryUnavailableExitCode}' -NoNewline; "
            : string.Empty;
        script.AppendLine(
            $"try {{ Set-Location -LiteralPath {Ps(workingDirectory)} -ErrorAction Stop }} " +
            $"catch {{ Write-Host $_.Exception.Message; {cdFail}exit {WorkingDirectoryUnavailableExitCode} }}");

        // Clear it first: a stale non-zero from an earlier native command in the same session
        // would otherwise be read back as this command's exit code.
        script.AppendLine("$global:LASTEXITCODE = $null");
        script.AppendLine(commandBody);

        // Capture $? for the LAST statement FIRST — any later evaluation resets it. A failed cmdlet
        // never touches $LASTEXITCODE and would otherwise read as success. Lifted from
        // HostShellCommandService.BuildPowerShellCommand.
        script.AppendLine("$__vbr_ok = $?");
        script.AppendLine("$__vbr_code = $global:LASTEXITCODE");
        script.AppendLine(
            "if (-not $__vbr_ok) { if ($null -ne $__vbr_code -and $__vbr_code -ne 0) { $__vbr_exit = [int]$__vbr_code } else { $__vbr_exit = 1 } } " +
            "elseif ($null -ne $__vbr_code) { $__vbr_exit = [int]$__vbr_code } else { $__vbr_exit = 0 }");

        if (writeSentinel)
            script.AppendLine("Set-Content -LiteralPath $__vbr_sentinel -Value $__vbr_exit -NoNewline");

        // Holding the window open costs us nothing: the sentinel is already written, so the caller
        // has the exit code and is not waiting on this read.
        if (pauseOnFailure)
            script.AppendLine("if ($__vbr_exit -ne 0) { Read-Host \"`nStep failed with exit code $__vbr_exit. Press Enter to close\" }");

        script.AppendLine("exit $__vbr_exit");
        return script.ToString();
    }

    internal static string BuildPosixScript(
        string commandBody,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        string? sentinelPath,
        bool pauseOnFailure,
        string? selfDeletePath)
    {
        var script = new StringBuilder();
        var writeSentinel = sentinelPath is not null;

        if (writeSentinel)
            script.AppendLine($"__vbr_sentinel={Posix(sentinelPath!)}");

        if (selfDeletePath is not null)
            script.AppendLine($"rm -f -- {Posix(selfDeletePath)}");

        AppendPosixEnvironment(script, environmentVariables);

        var cdFail = writeSentinel
            ? $"{{ printf '%s' '{WorkingDirectoryUnavailableExitCode}' > \"$__vbr_sentinel\"; exit {WorkingDirectoryUnavailableExitCode}; }}"
            : $"exit {WorkingDirectoryUnavailableExitCode}";
        script.AppendLine($"cd {Posix(workingDirectory)} || {cdFail}");

        script.AppendLine(commandBody);
        script.AppendLine("__vbr_exit=$?");

        if (writeSentinel)
            script.AppendLine("printf '%s' \"$__vbr_exit\" > \"$__vbr_sentinel\"");

        if (pauseOnFailure)
        {
            script.AppendLine(
                "if [ \"$__vbr_exit\" -ne 0 ]; then " +
                "printf '\\nStep failed with exit code %s. Press Enter to close\\n' \"$__vbr_exit\"; " +
                "read -r __vbr_ignored; fi");
        }

        script.AppendLine("exit $__vbr_exit");
        return script.ToString();
    }

    /// <summary>
    /// How a POSIX login shell runs the script. Dot-sourcing rather than executing it avoids
    /// depending on the execute bit, and lets the script's own <c>exit</c> close the window.
    /// </summary>
    public static string BuildPosixSourceCommand(string scriptPath) => $". {Posix(scriptPath)}";

    /// <summary>
    /// Writes a fresh temp script and returns it alongside the invocation that runs it directly
    /// (used as-is by the hidden path, and by the Windows visible path; the POSIX visible path
    /// wraps <see cref="BuildPosixSourceCommand"/> in a terminal emulator instead).
    ///
    /// <paramref name="buildText"/> receives the final path so the script can delete itself.
    /// </summary>
    public static CliScriptFile WriteScript(Func<string, string> buildText, bool isWindows)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"viberails-step-{Guid.NewGuid():N}{(isWindows ? ".ps1" : ".sh")}");

        // CreateNew, not Create: this file is about to be executed, so it must be one we made.
        // Same reasoning as CliSpawnCommandBuilder — a GUID name makes planting a file a poor
        // attack, but "poor attack" is not the standard for something that runs as the user.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
        {
            writer.Write(buildText(path));
        }

        var (executable, arguments) = isWindows
            ? (ShellDefaults.WindowsCommandShell, (IReadOnlyList<string>)["-File", path])
            : (ShellDefaults.GetUnixCommandShellPath(), (IReadOnlyList<string>)["-lic", BuildPosixSourceCommand(path)]);

        return new CliScriptFile(path, executable, arguments);
    }

    /// <summary>
    /// The hidden, captured form of a step: same script, same shell, same profile behaviour, no
    /// sentinel and no pause. This is what the "Test" button runs.
    /// </summary>
    public static CliScriptFile CreateCapturedScript(
        string commandBody,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var isWindows = OperatingSystem.IsWindows();
        return WriteScript(
            scriptPath => BuildScript(
                commandBody,
                workingDirectory,
                environmentVariables,
                sentinelPath: null,
                pauseOnFailure: false,
                selfDeletePath: scriptPath,
                isWindows),
            isWindows);
    }

    private static void AppendPowerShellEnvironment(
        StringBuilder script,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (environmentVariables is null) return;

        foreach (var (name, value) in environmentVariables)
        {
            if (!IsValidEnvironmentVariableName(name)) continue;

            if (value is null)
                script.AppendLine($"Remove-Item -LiteralPath 'Env:\\{name}' -ErrorAction SilentlyContinue");
            else
                script.AppendLine($"$env:{name} = {Ps(value)}");
        }
    }

    private static void AppendPosixEnvironment(
        StringBuilder script,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (environmentVariables is null) return;

        foreach (var (name, value) in environmentVariables)
        {
            if (!IsValidEnvironmentVariableName(name)) continue;

            if (value is null)
                script.AppendLine($"unset {name}");
            else
                script.AppendLine($"export {name}={Posix(value)}");
        }
    }

    /// <summary>
    /// Env-var names are emitted unquoted into the script, so they are restricted to the portable
    /// charset rather than escaped. A name outside it is dropped, never interpolated.
    /// </summary>
    internal static bool IsValidEnvironmentVariableName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsAsciiLetter(name[0]) && name[0] != '_') return false;

        foreach (var ch in name)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
                return false;
        }

        return true;
    }

    private static string Ps(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Posix(string value) => ShellArgSanitizer.QuotePosixSingleQuoted(value);
}
