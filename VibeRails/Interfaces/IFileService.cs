namespace VibeRails.Interfaces
{
    public interface IFileService
    {
        public (bool inGet, string projectRoot) TryGetProjectRootPath();
        void InitGlobalSave();
        void InitLocal(string rootPath);
        Task AppendAllTextAsync(string path, string content, CancellationToken cancellationToken);
        string Combine(params string[] paths);
        void CopyFile(string sourceFileName, string destFileName, bool overwrite);
        void CreateDirectory(string path);
        void DeleteDirectory(string path, bool recursive);
        void DeleteFile(string path);
        bool DirectoryExists(string path);
        IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions options);
        IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options);
        bool FileExists(string path);
        string GetCurrentDirectory();
        string GetDirectoryName(string? path);
        string GetFileName(string? path);
        string GetFileNameWithoutExtension(string? path);
        string GetGlobalSavePath();
        string GetTempPath();
        string GetUserProfilePath();
        Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);
        Task WriteAllTextAsync(string path, string content, FileMode mode, FileShare share, CancellationToken cancellationToken);
        
    }
}