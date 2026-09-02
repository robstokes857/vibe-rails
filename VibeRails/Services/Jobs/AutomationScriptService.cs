using System.Security.Cryptography;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

public interface IAutomationScriptService
{
    Task<JobActionRequest> NormalizeAsync(
        string projectRoot,
        JobActionRequest action,
        CancellationToken cancellationToken = default,
        bool approveCurrentVersion = true);

    Task<PreparedAutomationScript> PrepareAsync(
        string workspaceRoot,
        string projectRoot,
        JobRunActionRecord action,
        CancellationToken cancellationToken = default);

    string? GetRuntimeUnavailableMessage(JobScriptRuntime runtime);
}

public sealed record PreparedAutomationScript(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentVariables);

public sealed class AutomationScriptValidationException(string message) : Exception(message);

/// <summary>
/// Validates, pins, and prepares repository-local Automation scripts. Paths are persisted relative
/// to the Git root so a Worker workspace clone can execute its own copy of the script; when that
/// copy is absent or differs from the approved bytes, the project copy stands in (see
/// <see cref="ResolveApprovedScriptAsync"/>). Execution uses an explicit interpreter and real argv
/// elements; no user value is concatenated into a shell command.
/// </summary>
public sealed class AutomationScriptService(IJobExecutableResolver executableResolver)
    : IAutomationScriptService
{
    public const int MaxScriptBytes = 5 * 1024 * 1024;
    public const int MaxArguments = 64;
    public const int MaxArgumentChars = 8_000;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3_600;

    public async Task<JobActionRequest> NormalizeAsync(
        string projectRoot,
        JobActionRequest action,
        CancellationToken cancellationToken = default,
        bool approveCurrentVersion = true)
    {
        if (action.ScriptRuntime is not JobScriptRuntime runtime
            || !Enum.IsDefined(typeof(JobScriptRuntime), runtime))
        {
            throw new AutomationScriptValidationException("Choose Python, PowerShell, or Bash for every script action.");
        }

        var script = ResolveRegularFile(projectRoot, action.ScriptPath, "Script");
        ValidateRuntimeExtension(runtime, script.FullPath);
        ValidateArguments(action.Arguments);

        if (action.TimeoutSeconds is int timeout
            && timeout is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            throw new AutomationScriptValidationException(
                $"A script timeout must be {MinTimeoutSeconds}–{MaxTimeoutSeconds} seconds, or left blank.");
        }

        var workingDirectory = NormalizeWorkingDirectory(projectRoot, action.WorkingDirectory);
        var unavailable = GetRuntimeUnavailableMessage(runtime);
        if (unavailable is not null)
            throw new AutomationScriptValidationException(unavailable);

        var hash = await ComputeHashAsync(script.FullPath, cancellationToken);
        if (!approveCurrentVersion
            && !string.Equals(action.ApprovedHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new AutomationScriptValidationException(
                $"Script '{script.RelativePath}' has changed since this Automation was saved. Review and save the Automation before changing its enabled state.");
        }
        var id = Guid.TryParse(action.Id, out var parsedId)
            ? parsedId.ToString()
            : Guid.NewGuid().ToString();

        return action with
        {
            Id = id,
            EnvironmentId = null,
            ScriptPath = script.RelativePath,
            ScriptRuntime = runtime,
            Arguments = action.Arguments?.ToList() ?? [],
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = action.TimeoutSeconds,
            ApprovedHash = hash
        };
    }

    public async Task<PreparedAutomationScript> PrepareAsync(
        string workspaceRoot,
        string projectRoot,
        JobRunActionRecord action,
        CancellationToken cancellationToken = default)
    {
        if (action.Kind != JobActionKind.Script
            || action.ScriptRuntime is not JobScriptRuntime runtime
            || !Enum.IsDefined(typeof(JobScriptRuntime), runtime))
        {
            throw new AutomationScriptValidationException("The Automation action is not a runnable script.");
        }

        ValidateArguments(action.Arguments);
        if (action.TimeoutSeconds is int timeout
            && timeout is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            throw new AutomationScriptValidationException(
                $"A script timeout must be {MinTimeoutSeconds}–{MaxTimeoutSeconds} seconds, or left blank.");
        }

        var expectedHash = (action.ApprovedHash ?? string.Empty).Trim();
        if (expectedHash.Length != 64)
        {
            throw new AutomationScriptValidationException(
                $"Script '{action.ScriptPath}' has no approved version. Save the Automation to approve the current script.");
        }

        var script = await ResolveApprovedScriptAsync(
            workspaceRoot,
            projectRoot,
            action,
            runtime,
            expectedHash,
            cancellationToken);

        var executable = executableResolver.Resolve(runtime)
            ?? throw new AutomationScriptValidationException(GetRuntimeUnavailableMessage(runtime)!);
        var workingDirectory = ResolveWorkingDirectory(workspaceRoot, action.WorkingDirectory);
        var arguments = new List<string>(executable.PrefixArguments);

        switch (runtime)
        {
            case JobScriptRuntime.PowerShell:
                // The hash pin plus path containment is the trust decision for this file; the
                // machine's policy for downloaded or unsigned scripts (Restricted, AllSigned, a
                // Zone.Identifier on a copied file) must not veto an approved repository script.
                // NonInteractive turns a stray Read-Host into an error instead of a hung run.
                arguments.AddRange([
                    "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", script.FullPath
                ]);
                break;
            case JobScriptRuntime.Bash:
                arguments.Add(ToBashPath(workingDirectory, script.FullPath));
                break;
            case JobScriptRuntime.Python:
                arguments.Add(script.FullPath);
                break;
        }

        arguments.AddRange(action.Arguments);
        return new PreparedAutomationScript(
            executable.Path,
            arguments,
            workingDirectory,
            new Dictionary<string, string?>
            {
                ["VIBERAILS_JOB_RUN_ID"] = action.RunId,
                ["VIBERAILS_ACTION_ID"] = action.Id,
                ["VIBERAILS_WORKSPACE_ROOT"] = Path.GetFullPath(workspaceRoot)
            });
    }

    public string? GetRuntimeUnavailableMessage(JobScriptRuntime runtime)
    {
        if (executableResolver.Resolve(runtime) is not null)
            return null;

        return runtime switch
        {
            JobScriptRuntime.PowerShell => "PowerShell 7 (pwsh) is required to run this script but was not found.",
            JobScriptRuntime.Bash when OperatingSystem.IsWindows() =>
                "Git Bash is required to run Bash scripts on Windows but was not found.",
            JobScriptRuntime.Bash => "Bash is required to run this script but was not found.",
            JobScriptRuntime.Python => "Python 3 is required to run this script but was not found.",
            _ => "The selected script runtime is not available."
        };
    }

    /// <summary>
    /// Finds the copy of the script whose bytes are the ones approved at save time. The Worker's
    /// workspace copy is preferred so a clone runs its own file, but a clone is made from HEAD
    /// (PerRun) or from a first-launch snapshot (Persistent), so it can lag the project tree the
    /// hash was pinned from — or lack an uncommitted script entirely. The project copy then stands
    /// in, still from the workspace's working directory. Either way only approved bytes ever run:
    /// a copy the Worker edited is never executed, and neither copy matching fails closed.
    /// </summary>
    private static async Task<(string FullPath, string RelativePath)> ResolveApprovedScriptAsync(
        string workspaceRoot,
        string projectRoot,
        JobRunActionRecord action,
        JobScriptRuntime runtime,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var roots = new List<string> { workspaceRoot };
        if (!string.IsNullOrWhiteSpace(projectRoot)
            && !string.Equals(NormalizeRoot(projectRoot), NormalizeRoot(workspaceRoot), PathComparison))
        {
            roots.Add(projectRoot);
        }

        AutomationScriptValidationException? firstError = null;
        var sawMismatch = false;
        foreach (var root in roots)
        {
            (string FullPath, string RelativePath) script;
            try
            {
                script = ResolveRegularFile(root, action.ScriptPath, "Script");
                ValidateRuntimeExtension(runtime, script.FullPath);
            }
            catch (AutomationScriptValidationException ex)
            {
                firstError ??= ex;
                continue;
            }

            var actualHash = await ComputeHashAsync(script.FullPath, cancellationToken);
            if (string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                return script;
            sawMismatch = true;
        }

        if (sawMismatch)
        {
            throw new AutomationScriptValidationException(
                $"Script '{action.ScriptPath}' does not match the version approved when this Automation was saved. Review and save the Automation again to approve the current script.");
        }

        throw firstError
            ?? new AutomationScriptValidationException($"Script was not found: {action.ScriptPath}");
    }

    private static string ToBashPath(string workingDirectory, string scriptPath)
    {
        // A relative slash path works in Git Bash as well as POSIX Bash; passing a raw Windows
        // drive path to bash is not portable between those runtimes.
        var bashPath = Path.GetRelativePath(workingDirectory, scriptPath).Replace('\\', '/');
        if (Path.IsPathRooted(bashPath))
        {
            // No relative form exists across Windows drives (a project-copy fallback from a
            // workspace on another volume). Git Bash spells that /c/path rather than C:/path.
            if (bashPath.Length >= 2 && bashPath[1] == ':' && char.IsAsciiLetter(bashPath[0]))
                bashPath = "/" + char.ToLowerInvariant(bashPath[0]) + bashPath[2..];
            return bashPath;
        }

        if (!bashPath.StartsWith(".", StringComparison.Ordinal))
            bashPath = "./" + bashPath;
        return bashPath;
    }

    private static (string FullPath, string RelativePath) ResolveRegularFile(
        string root,
        string? requestedPath,
        string label)
    {
        var rootPath = NormalizeRoot(root);
        var raw = (requestedPath ?? string.Empty).Trim();
        if (raw.Length == 0)
            throw new AutomationScriptValidationException($"{label} path is required.");
        if (IsNetworkOrDevicePath(raw))
            throw new AutomationScriptValidationException($"{label} must be inside the current repository.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(raw)
                ? raw
                : Path.Combine(rootPath, FromPortablePath(raw)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AutomationScriptValidationException($"{label} path is invalid.");
        }

        RequireContained(rootPath, fullPath, label);
        RequireNoLinkedComponents(rootPath, fullPath, label);

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
                throw new AutomationScriptValidationException($"{label} was not found: {raw}");
            if ((info.Attributes & FileAttributes.Directory) != 0)
                throw new AutomationScriptValidationException($"{label} must be a file.");
            if (info.Length > MaxScriptBytes)
                throw new AutomationScriptValidationException($"{label} is larger than the 5 MB limit.");
        }
        catch (AutomationScriptValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AutomationScriptValidationException($"{label} could not be inspected: {ex.Message}");
        }

        return (fullPath, ToPortablePath(Path.GetRelativePath(rootPath, fullPath)));
    }

    private static string? NormalizeWorkingDirectory(string root, string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath)
            || requestedPath.Trim() is "." or "./" or @".\")
        {
            return null;
        }

        var rootPath = NormalizeRoot(root);
        var raw = requestedPath.Trim();
        if (IsNetworkOrDevicePath(raw))
            throw new AutomationScriptValidationException("Working directory must be inside the current repository.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(raw)
                ? raw
                : Path.Combine(rootPath, FromPortablePath(raw)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AutomationScriptValidationException("Script working directory is invalid.");
        }

        RequireContained(rootPath, fullPath, "Working directory");
        RequireNoLinkedComponents(rootPath, fullPath, "Working directory");
        if (!Directory.Exists(fullPath))
            throw new AutomationScriptValidationException($"Script working directory was not found: {raw}");
        return ToPortablePath(Path.GetRelativePath(rootPath, fullPath));
    }

    private static string ResolveWorkingDirectory(string root, string? relativePath)
    {
        var rootPath = NormalizeRoot(root);
        if (string.IsNullOrWhiteSpace(relativePath))
            return rootPath;

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, FromPortablePath(relativePath)));
        RequireContained(rootPath, fullPath, "Working directory");
        RequireNoLinkedComponents(rootPath, fullPath, "Working directory");
        if (!Directory.Exists(fullPath))
            throw new AutomationScriptValidationException(
                $"Script working directory was not found in this workspace: {relativePath}");
        return fullPath;
    }

    private static void ValidateRuntimeExtension(JobScriptRuntime runtime, string path)
    {
        var expected = runtime switch
        {
            JobScriptRuntime.Python => ".py",
            JobScriptRuntime.PowerShell => ".ps1",
            JobScriptRuntime.Bash => ".sh",
            _ => string.Empty
        };
        if (!Path.GetExtension(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new AutomationScriptValidationException(
                $"{runtime} actions require a {expected} script file.");
        }
    }

    private static void ValidateArguments(IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
            return;
        if (arguments.Count > MaxArguments)
            throw new AutomationScriptValidationException($"A script can have at most {MaxArguments} arguments.");
        if (arguments.Any(value => value is null || value.Length > MaxArgumentChars || value.Contains('\0')))
        {
            throw new AutomationScriptValidationException(
                $"Each script argument must be at most {MaxArgumentChars} characters and cannot contain NUL.");
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void RequireNoLinkedComponents(string root, string target, string label)
    {
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                // A component that does not exist has nothing below it to link through; the
                // caller's own existence check reports "not found" rather than a misleading
                // reparse-point error (Attributes reads as -1, all flags set, for a missing path).
                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current)
                        ? new FileInfo(current)
                        : null;
                if (info is null)
                    return;
                if (info.LinkTarget is not null
                    || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new AutomationScriptValidationException(
                        $"{label} cannot pass through a symbolic link or reparse point.");
                }
            }
            catch (AutomationScriptValidationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AutomationScriptValidationException($"{label} could not be inspected: {ex.Message}");
            }
        }
    }

    private static void RequireContained(string root, string target, string label)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.Equals(root, PathComparison) && !target.StartsWith(prefix, PathComparison))
            throw new AutomationScriptValidationException($"{label} must stay inside the current repository.");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new AutomationScriptValidationException("Project path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal)
        || path.StartsWith(@"\\?\", StringComparison.Ordinal)
        || path.StartsWith(@"\\.\", StringComparison.Ordinal);

    private static string FromPortablePath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string ToPortablePath(string path) => path.Replace('\\', '/');
}
