namespace VibeRails.Services.Terminal;

/// <summary>
/// Builds a chain of shell commands to execute before and including the CLI launch.
/// Commands are joined with ";" so failures in setup steps don't block the CLI.
/// </summary>
public class ShellCommandBuilder
{
    private readonly List<string> _setupCommands = [];
    private string _launchCommand = "";

    /// <summary>
    /// Add a setup command to run before the CLI launches.
    /// Commands run in order, failures don't block subsequent commands.
    /// </summary>
    public ShellCommandBuilder AddSetup(string command)
    {
        if (!string.IsNullOrWhiteSpace(command))
            _setupCommands.Add(command);
        return this;
    }

    /// <summary>
    /// Set the final CLI launch command (e.g. "claude", "claude --resume").
    /// </summary>
    public ShellCommandBuilder SetLaunchCommand(string command)
    {
        _launchCommand = command;
        return this;
    }

    /// <summary>
    /// Render the full command string for the shell.
    /// </summary>
    public string Build()
    {
        if (_setupCommands.Count == 0)
            return _launchCommand;

        return string.Join("; ", [.. _setupCommands, _launchCommand]);
    }
}
