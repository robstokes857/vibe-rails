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
                var lines = await File.ReadAllLinesAsync(filePath, ct);
                return lines.Length;
            }
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
