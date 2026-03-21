namespace VibeRails.Services;

public interface ILocalCopilotService
{
    Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, bool jsonOutput = false, bool yolo = false);
}

public class LocalCopilotService(IShellService shell) : ILocalCopilotService
{
    public async Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, bool jsonOutput = false, bool yolo = false)
    {
        var args = new List<string> { "copilot", "-p", prompt };

        if (model is not null) { args.Add("--model"); args.Add(model); }
        if (jsonOutput) args.Add("--output=json");
        if (yolo) args.Add("--yolo");

        return await shell.RunAsync(cancellationToken, [.. args]);
    }
}
