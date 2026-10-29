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
        /// <paramref name="skipLockedFiles"/>, a file another process holds locked is left out
        /// instead of failing the whole copy.
        /// </summary>
        protected void CopyDirectoryRecursive(string sourceDir, string destDir, HashSet<string>? excludedDirNames = null,
            HashSet<string>? excludedTopLevelDirNames = null, bool skipLockedFiles = false)
        {
            if (!_fileService.DirectoryExists(sourceDir))
            {
                return;
            }

            EnsureDirectoryExists(destDir);

            // Copy all files
            var files = _fileService.EnumerateFiles(sourceDir, "*", new EnumerationOptions { RecurseSubdirectories = false });
            foreach (var sourceFile in files)
            {
                var fileName = _fileService.GetFileName(sourceFile);
                var destFile = Path.Combine(destDir, fileName);

                // Only copy if destination doesn't exist
                if (!_fileService.FileExists(destFile))
                {
                    try
                    {
                        _fileService.CopyFile(sourceFile, destFile, overwrite: false);
                    }
                    catch (IOException ex) when (skipLockedFiles && IsLockedByAnotherProcess(ex))
                    {
                        Serilog.Log.Warning("[Environments] Skipped {Path}: another process holds it locked", sourceFile);
                    }
                }
            }

            // Recursively copy subdirectories
            var directories = _fileService.EnumerateDirectories(sourceDir, "*", new EnumerationOptions { RecurseSubdirectories = false });
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
