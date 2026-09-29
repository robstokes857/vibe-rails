using System.Text;

namespace VibeRails.Utils;

/// <summary>
/// Replaces a small file so that a reader in another process sees the old contents or the new ones,
/// never a torn mix. The text goes to a private temporary file beside the target, which is then
/// renamed over it (MoveFileEx / rename(2), atomic within one volume).
/// </summary>
internal static class AtomicFile
{
    // Windows keeps a replaced file's name taken while any reader still has it open, so the rename
    // retries for about half a second before the save reports the failure.
    private const int MoveAttempts = 20;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(25);

    internal static void WriteAllText(string path, string contents)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, PrivateOptions()))
            {
                file.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
                file.Flush(flushToDisk: true);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                // Windows refuses to replace a file another process holds open without delete
                // sharing (ReadAllText here shares it; an older binary's reader does not). Reads are
                // brief, so a short retry beats failing the save.
                catch (IOException) when (OperatingSystem.IsWindows() && attempt < MoveAttempts)
                {
                    Thread.Sleep(MoveRetryDelay);
                }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt < MoveAttempts)
                {
                    Thread.Sleep(MoveRetryDelay);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Reads without blocking <see cref="WriteAllText"/>'s rename in another process: Windows only
    /// replaces an open file whose readers share delete access, and the reader keeps the old contents.
    /// </summary>
    internal static string ReadAllText(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static FileStreamOptions PrivateOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
