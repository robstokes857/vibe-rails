using System.Diagnostics;

namespace VibeRails.Services;

public class UpdateInstaller
{
    private readonly UpdateService _updateService;

    public UpdateInstaller(UpdateService updateService)
    {
        _updateService = updateService;
    }

    public async Task<bool> InstallUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Check if update is available
        var updateInfo = await _updateService.CheckForUpdateAsync(cancellationToken);
        if (updateInfo == null || !updateInfo.UpdateAvailable)
        {
            Console.WriteLine("[VibeRails] No update available.");
            return false;
        }

        Console.WriteLine($"[VibeRails] Installing update: v{updateInfo.CurrentVersion} -> v{updateInfo.LatestVersion}");

        try
        {
            // Extract appropriate install script based on platform
            var scriptPath = ExtractInstallScript();
            if (scriptPath == null)
            {
                Console.Error.WriteLine("[VibeRails] Failed to extract install script.");
                return false;
            }

            // Start the install script with a delay parameter
            var processStartInfo = CreateProcessStartInfo(scriptPath);
            var process = Process.Start(processStartInfo);

            if (process == null)
            {
                Console.Error.WriteLine("[VibeRails] Failed to start install script.");
                return false;
            }

            Console.WriteLine("[VibeRails] Update installer started. Application will shut down in 2 seconds...");
            await Task.Delay(2000, cancellationToken);

            // Shutdown will be handled by caller
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VibeRails] Error installing update: {ex.Message}");
            return false;
        }
    }

    private string? ExtractInstallScript()
    {
        var isWindows = OperatingSystem.IsWindows();
        var scriptName = isWindows ? "install.ps1" : "install.sh";

        // Scripts are bundled alongside the executable (not embedded)
        var exeDir = AppContext.BaseDirectory;
        var bundledScriptPath = Path.Combine(exeDir, scriptName);

        if (!File.Exists(bundledScriptPath))
        {
            Console.Error.WriteLine($"[VibeRails] Could not find bundled install script: {bundledScriptPath}");
            return null;
        }

        // Copy to temp directory to avoid permission issues
        var tempDir = Path.Combine(Path.GetTempPath(), "vibe-rails-update");
        Directory.CreateDirectory(tempDir);

        var tempScriptPath = Path.Combine(tempDir, scriptName);
        File.Copy(bundledScriptPath, tempScriptPath, overwrite: true);

        // Make script executable on Unix
        if (!isWindows)
        {
            var chmod = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{tempScriptPath}\"",
                UseShellExecute = false
            });
            chmod?.WaitForExit();
        }

        return tempScriptPath;
    }

    private ProcessStartInfo CreateProcessStartInfo(string scriptPath)
    {
        var isWindows = OperatingSystem.IsWindows();

        if (isWindows)
        {
            // PowerShell script
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
        else
        {
            // Bash script
            return new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = false
            };
        }
    }
}
