namespace VibeRails.Services;

public interface ILocalCodexService
{
    Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, bool jsonOutput = false);
}

public class LocalCodexService(IShellService shell) : ILocalCodexService
{
    public async Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, bool jsonOutput = false)
    {
        var args = new List<string> { "codex", "exec", prompt };

        if (model is not null) { args.Add("--model"); args.Add(model); }
        if (jsonOutput) args.Add("--json");

        return await shell.RunAsync(cancellationToken, [.. args]);
    }
}
