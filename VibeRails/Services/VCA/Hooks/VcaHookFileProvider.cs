namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookFileProvider
{
    Task<IReadOnlyList<string>> GetStagedFilesAsync(string workingDirectory, CancellationToken cancellationToken);
}

public sealed class VcaHookFileProvider : IVcaHookFileProvider
{
    private static readonly TimeSpan StagedFileTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<string>> GetStagedFilesAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await GitProcessRunner.RunAsync(
            "--no-pager diff --cached --name-only",
            workingDirectory,
            StagedFileTimeout,
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return [];
        }

        return result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
