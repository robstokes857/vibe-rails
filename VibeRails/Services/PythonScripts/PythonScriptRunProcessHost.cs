using PyBridge;

namespace VibeRails.Services.PythonScripts;

/// <summary>
/// Lightweight host for an approved script launched inside a Web UI terminal. Its interpreter
/// (python, pwsh or bash) inherits this process's console handles, so prompts, Ctrl+C, and live output all travel
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
            Console.Error.WriteLine($"Usage: vb {Flag} <script-name>.py|.ps1|.sh");
            return 1;
        }

        var bootstrapService = new PythonScriptService(installDirectory: installDirectory);
        var scriptsDirectory = bootstrapService.GetScriptsDirectory();
        // Lazy: only a .py script needs the interpreter search; pwsh and bash resolve their own.
        var service = new PythonScriptService(
            installDirectory: installDirectory,
            allProjects: true,
            pythonRunnerProvider: () => new PythonRunner(PythonRunnerOptions.Discover(scriptsDirectory)));

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
