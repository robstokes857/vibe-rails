using PyBridge;

namespace VibeRails.Services.PythonScripts;

/// <summary>
/// Lightweight host for an approved script launched inside a Web UI terminal. Python inherits
/// this process's console handles, so <c>input()</c>, prompts, Ctrl+C, and live output all travel
/// through the existing PTY/WebSocket instead of captured HTTP response buffers.
/// </summary>
public static class PythonScriptRunProcessHost
{
    public const string Flag = "--run-python-script";
    private const string ArgumentSeparator = "--";

    public static bool IsRequested(string[] args) => FindFlagIndex(args) >= 0;

    internal static int FindFlagIndex(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], ArgumentSeparator, StringComparison.Ordinal))
                return -1;

            if (string.Equals(args[index], Flag, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    /// <param name="installDirectory">Overrides <c>~/.vibe_rails</c>; tests only.</param>
    public static async Task<int> RunAsync(
        string[] args,
        string? installDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var flagIndex = FindFlagIndex(args);
        var name = flagIndex >= 0 && flagIndex + 1 < args.Length ? args[flagIndex + 1] : null;
        if (string.Equals(name, ArgumentSeparator, StringComparison.Ordinal))
            name = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine($"Usage: vb {Flag} <script-name>.py");
            return 1;
        }

        var bootstrapService = new PythonScriptService(installDirectory: installDirectory);
        var scriptsDirectory = bootstrapService.GetScriptsDirectory();
        var runner = new PythonRunner(PythonRunnerOptions.Discover(scriptsDirectory));
        var service = new PythonScriptService(runner, installDirectory);

        try
        {
            return await service.RunInteractiveAsync(name, cancellationToken);
        }
        catch (PythonScriptValidationException exception)
        {
            Console.Error.WriteLine($"Run failed: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
    }
}
