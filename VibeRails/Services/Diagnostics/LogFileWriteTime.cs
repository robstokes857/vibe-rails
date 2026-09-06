namespace VibeRails.Services.Diagnostics;

/// <summary>
/// Directory enumeration reports the last write time stored in the directory entry, which NTFS
/// refreshes lazily while a writer still holds the file open, so a log that is being written right
/// now can look older than files closed hours ago. Querying through a handle returns the live value.
/// </summary>
internal static class LogFileWriteTime
{
    internal static DateTime ResolveUtc(FileInfo file)
    {
        try
        {
            using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return File.GetLastWriteTimeUtc(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ranking falls back to the enumerated value; the caller's read reports the failure.
            return file.LastWriteTimeUtc;
        }
    }
}
