using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services
{
    public class FileService : IFileService
    {
        private static readonly Lazy<string> _hiddenDir = new Lazy<string>(LoadInstallDirName);
        private const string ENV_DIR = @"envs";
        private const string HISTORY_DIR = @"history";
        private const string CONFIG_FILE = @"config.json";
        private const string STATE_FILE = @"state.db";
        private const string EMPTY_JSON = @"{}";

        private static string LoadInstallDirName()
        {
            try
            {
                // Try to read app_config.json from the same directory as the executable
                var exeDir = AppContext.BaseDirectory;
                var configPath = Path.Combine(exeDir, "app_config.json");

                if (!File.Exists(configPath))
                {
                    Console.Error.WriteLine($"[VibeRails] Warning: Could not find app_config.json at '{configPath}', using fallback directory name");
                    return ".vibe_rails"; // Fallback
                }

                var json = File.ReadAllText(configPath);

                // Simple JSON parsing for AOT compatibility
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("installDirName", out var dirElement))
                {
                    return dirElement.GetString() ?? ".vibe_rails";
                }

                return ".vibe_rails";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[VibeRails] Error loading install directory name from app_config.json: {ex.Message}");
                return ".vibe_rails"; // Fallback
            }
        }

        public string GetGlobalSavePath()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, _hiddenDir.Value);
        }

        public void InitGlobalSave()
        {
            string globalDir = GetGlobalSavePath();
            string envDir = Path.Combine(globalDir, ENV_DIR);
            string historyDir = Path.Combine(globalDir, HISTORY_DIR);
            string stateFile = Path.Combine(globalDir, STATE_FILE);
            string configFile = Path.Combine(globalDir, CONFIG_FILE);
            Configs.SetConfigPath(configFile);
            Configs.SetStatePath(stateFile);
            Configs.SetEnvPath(envDir);
            Configs.SetHistoryPath(historyDir);

            if (!Directory.Exists(globalDir))
            {
                Directory.CreateDirectory(globalDir);
            }
            if (!Directory.Exists(envDir))
            {
                Directory.CreateDirectory(envDir);
            }
            if (!Directory.Exists(historyDir))
            {
                Directory.CreateDirectory(historyDir);
            }
            // SQLite database will be created by StateService.InitializeDatabase()
            if (!File.Exists(configFile))
            {
                File.WriteAllText(configFile, EMPTY_JSON);
            }
        }

        public void InitLocal(string rootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            if (!Directory.Exists(Path.Combine(rootPath, _hiddenDir.Value)))
            {
                Directory.CreateDirectory(rootPath);
            }
        }

        public (bool inGet, string projectRoot) TryGetProjectRootPath()
        {

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "rev-parse --show-toplevel",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Directory.GetCurrentDirectory()
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(2000);

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    return (true, output);
                }
            }
            catch
            {
                return (false, string.Empty);
            }

            return (false, string.Empty);
        }



        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options)
            => Directory.EnumerateFiles(path, searchPattern, options);

        public IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions options)
            => Directory.EnumerateDirectories(path, searchPattern, options);

        public async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        public async Task WriteAllTextAsync(string path, string content, FileMode mode, FileShare share, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, mode, FileAccess.Write, share);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }

        public async Task AppendAllTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        public string GetCurrentDirectory() => Directory.GetCurrentDirectory();

        public string GetTempPath() => Path.GetTempPath();

        public string GetUserProfilePath() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public string GetFileName(string? path) => Path.GetFileName(path) ?? string.Empty;

        public string GetDirectoryName(string? path) => Path.GetDirectoryName(path) ?? string.Empty;

        public string GetFileNameWithoutExtension(string? path) => Path.GetFileNameWithoutExtension(path) ?? string.Empty;

        public string Combine(params string[] paths) => Path.Combine(paths);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void CopyFile(string sourceFileName, string destFileName, bool overwrite)
            => File.Copy(sourceFileName, destFileName, overwrite);

        public void DeleteDirectory(string path, bool recursive)
            => Directory.Delete(path, recursive);

        public void DeleteFile(string path)
            => File.Delete(path);
    }
}
