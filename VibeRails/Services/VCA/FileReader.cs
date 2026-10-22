namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Implements file reading operations for validation
    /// </summary>
    public class FileReader : IFileReader
    {
        public Task<bool> ExistsAsync(string filePath, CancellationToken ct)
        {
            return Task.FromResult(File.Exists(filePath));
        }

        public async Task<int> GetLineCountAsync(string filePath, CancellationToken ct)
        {
            try
            {
                using var reader = new StreamReader(filePath);
                var buffer = new char[4096];
                var lines = 0;
                var previousCr = false;
                var endsWithNewline = true;
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    foreach (var c in buffer.AsSpan(0, read))
                    {
                        if (c == '\r' || (c == '\n' && !previousCr)) lines++;
                        previousCr = c == '\r';
                        endsWithNewline = c is '\r' or '\n';
                    }
                }
                return endsWithNewline ? lines : checked(lines + 1);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return 0;
            }
        }

        public Task<string> ReadAllTextAsync(string filePath, CancellationToken ct)
        {
            return File.ReadAllTextAsync(filePath, ct);
        }
    }
}
