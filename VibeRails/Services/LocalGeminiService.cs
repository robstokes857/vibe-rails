namespace VibeRails.Services;

public interface ILocalGeminiService
{
    Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, string? outputFormat = null, string? allowedTools = null);
}

public class LocalGeminiService(IShellService shell) : ILocalGeminiService
{
    public async Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, string? outputFormat = null, string? allowedTools = null)
    {
        var args = new List<string> { "gemini", "-p", prompt };

        if (model is not null) { args.Add("--model"); args.Add(model); }
        if (outputFormat is not null) { args.Add("--output-format"); args.Add(outputFormat); }
        if (allowedTools is not null) { args.Add("--allowed-tools"); args.Add(allowedTools); }

        return await shell.RunAsync(cancellationToken, [.. args]);
    }
}
