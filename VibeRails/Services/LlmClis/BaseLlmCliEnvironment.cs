using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.LlmClis
{
    public abstract class BaseLlmCliEnvironment : IBaseLlmCliEnvironment
    {
        protected readonly IFileService _fileService;

        public BaseLlmCliEnvironment(IFileService fileService)
        {
            _fileService = fileService;
        }

        public async Task SaveEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            await CreateEnvironment(environment, cancellationToken);
        }

        public abstract Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken);
        public abstract string GetConfigSubdirectory();

        protected void EnsureDirectoryExists(string path)
        {
            if (!_fileService.DirectoryExists(path))
            {
                _fileService.CreateDirectory(path);
            }
        }

        /// <summary>
        /// Recursively copies all files and subdirectories from source to destination.
        /// Only copies if source exists and skips files that already exist in destination.
        /// <paramref name="excludedTopLevelDirNames"/> apply to the source root only. With
        /// <paramref name="skipLockedFiles"/>, the source is treated as live: a file another
        /// process holds locked, or one that disappears before it is copied, is left out instead
        /// of failing the whole copy, and a SQLite database left out takes its <c>-wal</c>,
        /// <c>-shm</c> and <c>-journal</c> with it.
        /// </summary>
        protected void CopyDirectoryRecursive(string sourceDir, string destDir, HashSet<string>? excludedDirNames = null,
            HashSet<string>? excludedTopLevelDirNames = null, bool skipLockedFiles = false)
        {
            if (!_fileService.DirectoryExists(sourceDir))
            {
                return;
            }

            EnsureDirectoryExists(destDir);

            List<string> files;
            try
            {
                files = _fileService.EnumerateFiles(sourceDir, "*", new EnumerationOptions { RecurseSubdirectories = false }).ToList();
            }
            catch (DirectoryNotFoundException) when (skipLockedFiles)
            {
                // Removed by the running CLI after the existence check above.
                return;
            }

            // Main files first, so a database that is left out can take its companions with it:
            // a -wal copied without the database it belongs to would be replayed onto the fresh
            // database the CLI creates in its place.
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceFile in files.OrderBy(file => SqliteDatabaseOf(_fileService.GetFileName(file)) is null ? 0 : 1))
            {
                var fileName = _fileService.GetFileName(sourceFile);
                var destFile = Path.Combine(destDir, fileName);

                if (skipLockedFiles && SqliteDatabaseOf(fileName) is { } database && skipped.Contains(database))
                {
                    Serilog.Log.Warning("[Environments] Skipped {Path}: its database {Database} was left out", sourceFile, database);
                    continue;
                }

                // Only copy if destination doesn't exist
                if (!_fileService.FileExists(destFile))
                {
                    try
                    {
                        _fileService.CopyFile(sourceFile, destFile, overwrite: false);
                    }
                    catch (IOException ex) when (skipLockedFiles && IsLockedByAnotherProcess(ex))
                    {
                        skipped.Add(fileName);
                        Serilog.Log.Warning("[Environments] Skipped {Path}: another process holds it locked", sourceFile);
                    }
                    catch (IOException ex) when (skipLockedFiles && ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        skipped.Add(fileName);
                        Serilog.Log.Warning("[Environments] Skipped {Path}: it was removed while copying", sourceFile);
                    }
                }
            }

            // Recursively copy subdirectories
            List<string> directories;
            try
            {
                directories = _fileService.EnumerateDirectories(sourceDir, "*", new EnumerationOptions { RecurseSubdirectories = false }).ToList();
            }
            catch (DirectoryNotFoundException) when (skipLockedFiles)
            {
                return;
            }
            foreach (var sourceSubDir in directories)
            {
                var dirName = Path.GetFileName(sourceSubDir);
                if ((excludedDirNames != null && excludedDirNames.Contains(dirName))
                    || (excludedTopLevelDirNames != null && excludedTopLevelDirNames.Contains(dirName)))
                {
                    continue;
                }
                var destSubDir = Path.Combine(destDir, dirName);
                CopyDirectoryRecursive(sourceSubDir, destSubDir, excludedDirNames, skipLockedFiles: skipLockedFiles);
            }
        }

        // The database a SQLite companion file belongs to ("state.db-wal" -> "state.db"), or null.
        private static string? SqliteDatabaseOf(string fileName)
        {
            foreach (var suffix in SqliteCompanionSuffixes)
            {
                if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return fileName[..^suffix.Length];
            }
            return null;
        }

        private static readonly string[] SqliteCompanionSuffixes = ["-wal", "-shm", "-journal"];

        // A Windows sharing or lock violation, or on macOS/Linux the advisory flock that .NET
        // takes when opening the source failing with EWOULDBLOCK (raw errno as the HResult).
        private static bool IsLockedByAnotherProcess(IOException ex)
        {
            if (OperatingSystem.IsWindows())
                return ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
            return ex.HResult == (OperatingSystem.IsMacOS() ? 35 : 11);
        }
    }
}
