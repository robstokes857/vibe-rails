using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.LlmClis
{
    public abstract class BaseLlmCliEnvironment : IBaseLlmCliEnvironment
    {
        protected readonly IDbService _dbService;
        protected readonly IFileService _fileService;

        public BaseLlmCliEnvironment(IDbService dbService, IFileService fileService)
        {
            _dbService = dbService;
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
        /// </summary>
        protected void CopyDirectoryRecursive(string sourceDir, string destDir)
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
                    _fileService.CopyFile(sourceFile, destFile, overwrite: false);
                }
            }

            // Recursively copy subdirectories
            var directories = _fileService.EnumerateDirectories(sourceDir, "*", new EnumerationOptions { RecurseSubdirectories = false });
            foreach (var sourceSubDir in directories)
            {
                var dirName = Path.GetFileName(sourceSubDir);
                var destSubDir = Path.Combine(destDir, dirName);
                CopyDirectoryRecursive(sourceSubDir, destSubDir);
            }
        }
    }
}
