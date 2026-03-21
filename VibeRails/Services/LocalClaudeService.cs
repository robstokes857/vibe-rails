namespace VibeRails.Services;

public interface ILocalClaudeService
{
    Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, string? systemPrompt = null, string? outputFormat = null, string? effort = null, int? maxTurns = null, string? tools = null);
}

public class LocalClaudeService(IShellService shell) : ILocalClaudeService
{
    public async Task<string> ProcessAsync(string prompt, CancellationToken cancellationToken, string? model = null, string? systemPrompt = null, string? outputFormat = null, string? effort = null, int? maxTurns = null, string? tools = null)
    {
        var args = new List<string> { "claude", "--print", prompt };

        if (model is not null) { args.Add("--model"); args.Add(model); }
        if (systemPrompt is not null) { args.Add("--system-prompt"); args.Add(systemPrompt); }
        if (outputFormat is not null) { args.Add("--output-format"); args.Add(outputFormat); }
        if (effort is not null) { args.Add("--effort"); args.Add(effort); }
        if (maxTurns is not null) { args.Add("--max-turns"); args.Add(maxTurns.Value.ToString()); }
        if (tools is not null) { args.Add("--tools"); args.Add(tools); }

        return await shell.RunAsync(cancellationToken, [.. args]);
    }
}
