using System.Diagnostics;
using System.Text;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Routes;

public static class ProjectRoutes
{
    // Dangerous directories that should never be git-initialized
    private static readonly string[] DangerousWindowsPaths =
    [
        @"C:\",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Windows",
        @"C:\Users",
    ];

    private static readonly string[] DangerousUnixPaths =
    [
        "/",
        "/home",
        "/root",
        "/usr",
        "/etc",
        "/bin",
        "/sbin",
        "/var",
        "/tmp",
    ];

    public static void Map(WebApplication app, string launchDirectory)
    {
        app.MapGet("/api/v1/context", () =>
        {
            return Results.Ok(new ContextResponse(
                IsInGit: !string.IsNullOrEmpty(Utils.ParserConfigs.GetRootPath()),
                LaunchDirectory: launchDirectory,
                RootPath: Utils.ParserConfigs.GetRootPath()
            ));
        }).WithName("GetContext");

        // POST /api/v1/git/init — initialize git in the launch directory
        app.MapPost("/api/v1/git/init", async (IFileService fileService, CancellationToken cancellationToken) =>
        {
            // Sanity check: don't allow git init in dangerous directories
            var normalizedPath = Path.GetFullPath(launchDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (IsDangerousDirectory(normalizedPath))
            {
                return Results.BadRequest(new ErrorResponse("Cannot initialize git in a system directory."));
            }

            // Check if git is installed
            if (!await IsGitInstalledAsync())
            {
                return Results.BadRequest(new ErrorResponse("Git is not installed or not found in PATH."));
            }

            // Run: git init
            var initResult = await RunGitCommandAsync("init", launchDirectory);
            if (!initResult.Success)
            {
                return Results.BadRequest(new ErrorResponse($"git init failed: {initResult.Output}"));
            }

            // Run: git add .
            var addResult = await RunGitCommandAsync("add .", launchDirectory);
            if (!addResult.Success)
            {
                return Results.BadRequest(new ErrorResponse($"git add failed: {addResult.Output}"));
            }

            // Run: git commit -m "Initial commit" — may fail if nothing to commit, that's fine
            await RunGitCommandAsync("commit -m \"Initial commit\"", launchDirectory);

            // Re-detect git root and update state
            var detected = fileService.TryGetProjectRootPath();
            Utils.ParserConfigs.SetRootPath(detected.projectRoot);

            if (!detected.inGet)
            {
                return Results.BadRequest(new ErrorResponse("Git initialized but could not detect repository root."));
            }

            fileService.InitLocal(detected.projectRoot);

            return Results.Ok(new ContextResponse(
                IsInGit: true,
                LaunchDirectory: launchDirectory,
                RootPath: detected.projectRoot
            ));
        }).WithName("GitInit");

        // POST /api/v1/git/open-directory — spawn a new vb instance in the given directory
        app.MapPost("/api/v1/git/open-directory", async (GitOpenDirectoryRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Directory))
            {
                return Results.BadRequest(new ErrorResponse("Directory is required."));
            }

            if (!Directory.Exists(request.Directory))
            {
                return Results.BadRequest(new ErrorResponse("Directory does not exist."));
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return Results.BadRequest(new ErrorResponse("Unable to determine executable path."));
            }

            var bootstrapTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--vs-code-v1",
                    WorkingDirectory = request.Directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    StandardOutputEncoding = Encoding.UTF8,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                var line = args.Data.Trim();
                if (!line.StartsWith("vs-code-v1=", StringComparison.OrdinalIgnoreCase)) return;
                var url = line["vs-code-v1=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(url))
                    bootstrapTcs.TrySetResult(url);
            };

            process.Exited += (_, _) =>
            {
                if (!bootstrapTcs.Task.IsCompleted)
                    bootstrapTcs.TrySetException(new InvalidOperationException("New vb instance exited before producing bootstrap URL."));
            };

            if (!process.Start())
            {
                return Results.BadRequest(new ErrorResponse("Failed to start new vb instance."));
            }

            process.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                var bootstrapUrl = await bootstrapTcs.Task.WaitAsync(timeoutCts.Token);
                return Results.Ok(new GitOpenDirectoryResponse(bootstrapUrl));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return Results.BadRequest(new ErrorResponse("Timed out waiting for new instance to start."));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[GitOpenDirectory] Failed to get bootstrap URL from new instance");
                return Results.BadRequest(new ErrorResponse($"Failed to open directory: {ex.Message}"));
            }
        }).WithName("GitOpenDirectory");

        // PUT /api/v1/projects/name - Set custom project name (stored in AgentMetadata table)
        app.MapPut("/api/v1/projects/name", async (
            IRepository repository,
            UpdateAgentNameRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            if (string.IsNullOrEmpty(request.CustomName))
            {
                return Results.BadRequest(new ErrorResponse("CustomName is required"));
            }

            await repository.SetProjectCustomNameAsync(request.Path, request.CustomName, cancellationToken);

            return Results.Ok(new UpdateAgentNameResponse(request.Path, request.CustomName));
        }).WithName("UpdateProjectName");

        // GET /api/v1/projects/name?path={path} - Get custom project name
        app.MapGet("/api/v1/projects/name", async (
            IRepository repository,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            var customName = await repository.GetProjectCustomNameAsync(path, cancellationToken);
            return Results.Ok(new UpdateAgentNameResponse(path, customName ?? ""));
        }).WithName("GetProjectName");
    }

    private static bool IsDangerousDirectory(string normalizedPath)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var dangerous in DangerousWindowsPaths)
            {
                var normalizedDangerous = Path.GetFullPath(dangerous).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedPath, normalizedDangerous, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // Also block anything with path depth < 2 (e.g. C:\foo has depth 1 segment after drive)
            var parts = normalizedPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return true;
        }
        else
        {
            foreach (var dangerous in DangerousUnixPaths)
            {
                if (string.Equals(normalizedPath, dangerous, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static async Task<bool> IsGitInstalledAsync()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool Success, string Output)> RunGitCommandAsync(string arguments, string workingDirectory)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var combined = string.Concat(stdout, stderr).Trim();
            return (process.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
