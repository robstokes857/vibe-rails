using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public sealed class JobWorkspaceService
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    public async Task<string> PrepareAsync(JobRunRecord run, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(run.ProjectPath))
            throw new DirectoryNotFoundException($"Project directory no longer exists: {run.ProjectPath}");

        if (run.ExecutionMode is JobExecutionMode.Review or JobExecutionMode.LiveWrite)
            return run.ProjectPath;

        var root = Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.JOB_WORKSPACES_SUBDIR);
        Directory.CreateDirectory(root);
        var workspace = Path.GetFullPath(Path.Combine(root, run.Id));
        var normalizedRoot = Path.GetFullPath(root + Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!workspace.StartsWith(normalizedRoot, comparison))
            throw new InvalidOperationException("Refusing to create an isolated job workspace outside the Jobs workspace root.");
        if (Directory.Exists(workspace) || File.Exists(workspace))
            throw new InvalidOperationException($"The isolated workspace already exists: {workspace}");

        var clone = await GitProcessRunner.RunAsync(
            ["clone", "--no-local", "--quiet", "--", run.ProjectPath, workspace],
            root,
            CloneTimeout,
            cancellationToken);
        if (clone.TimedOut)
            throw new TimeoutException("Creating the isolated Git workspace timed out.");
        if (clone.ExitCode != 0)
            throw new InvalidOperationException($"Could not create the isolated Git workspace: {clone.StdErr}");
        return workspace;
    }
}
