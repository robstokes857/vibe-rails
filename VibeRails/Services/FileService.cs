using System;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Serilog;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services
{
    public class FileService : IFileService
    {
        private static readonly TimeSpan GitRootTimeout = TimeSpan.FromSeconds(5);
        private readonly string _hiddenDir;
        private const string EMPTY_JSON = @"{}";

        public FileService(IConfiguration configuration)
        {
            _hiddenDir = configuration["VibeRails:InstallDirName"] ?? PathConstants.DEFAULT_INSTALL_DIR_NAME;
        }

        public string GetGlobalSavePath()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, _hiddenDir);
        }

        public void InitGlobalSave()
        {
            string globalDir = GetGlobalSavePath();
            string envDir = Path.Combine(globalDir, PathConstants.ENVS_SUBDIR);
            string sandboxDir = Path.Combine(globalDir, PathConstants.SANDBOXES_SUBDIR);
            string historyDir = Path.Combine(globalDir, PathConstants.HISTORY_SUBDIR);
            string stateFile = Path.Combine(globalDir, PathConstants.STATE_FILENAME);
            string configFile = Path.Combine(globalDir, PathConstants.CONFIG_FILENAME);
            ParserConfigs.SetConfigPath(configFile);
            ParserConfigs.SetStatePath(stateFile);
            ParserConfigs.SetEnvPath(envDir);
            ParserConfigs.SetSandboxPath(sandboxDir);
            ParserConfigs.SetHistoryPath(historyDir);

            if (!Directory.Exists(globalDir))
            {
                Directory.CreateDirectory(globalDir);
            }
            if (!Directory.Exists(envDir))
            {
                Directory.CreateDirectory(envDir);
            }
            if (!Directory.Exists(sandboxDir))
            {
                Directory.CreateDirectory(sandboxDir);
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
            if (!Directory.Exists(Path.Combine(rootPath, _hiddenDir)))
            {
                Directory.CreateDirectory(rootPath);
            }
        }

        public async Task<(bool inGet, string projectRoot)> TryGetProjectRootPathAsync(
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
            Log.Information("[GitDetect] TryGetProjectRootPath called with workingDirectory={WorkDir}, resolved cwd={Cwd}", workingDirectory, cwd);

            try
            {
                var result = await GitProcessRunner.RunAsync(
                    "rev-parse --show-toplevel",
                    cwd,
                    GitRootTimeout,
                    cancellationToken);

                Log.Information(
                    "[GitDetect] git exit={ExitCode}, timedOut={TimedOut}, stdout={Output}, stderr={Stderr}",
                    result.ExitCode,
                    result.TimedOut,
                    result.StdOut,
                    result.StdErr);

                if (!result.TimedOut && result.ExitCode == 0 && !string.IsNullOrEmpty(result.StdOut))
                {
                    return (true, result.StdOut);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[GitDetect] Failed to run git rev-parse");
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
        {
            try
            {
                Directory.Delete(path, recursive);
            }
            catch (UnauthorizedAccessException) when (recursive)
            {
                // Git pack files (*.idx, *.pack) under embedded repos are written
                // read-only, which blocks Directory.Delete on Windows. Clear the
                // attribute and retry; let any other failure propagate.
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
            }
        }

        private static void ClearReadOnlyAttributes(string path)
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
            }
        }

        public void DeleteFile(string path)
            => File.Delete(path);

        public void MoveDirectory(string sourceDirName, string destDirName)
            => Directory.Move(sourceDirName, destDirName);
    }
}
