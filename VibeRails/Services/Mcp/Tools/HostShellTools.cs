using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.Mcp.HostShell;

namespace VibeRails.Services.Mcp.Tools;

[McpServerToolType]
public sealed class HostShellTools
{
    private readonly IHostShellCommandService _commands;

    public HostShellTools(IHostShellCommandService commands)
    {
        _commands = commands;
    }

    [McpServerTool]
    [Description("Run a host shell command through VibeRails' reusable backend shell worker pool. Supported shells: PowerShell 7+ via pwsh on Windows, bash on Linux, and zsh on macOS. Commands execute on the host as the current OS user. Short commands can return inline; long commands return a job id to poll with get_shell_command_status.")]
    public async Task<string> RunShellCommand(
        [Description("Shell command text to execute on the host. Keep commands self-contained; workers are reusable and may be recycled after cancellation or timeout.")] string command,
        [Description("Optional working directory. Defaults to the VibeRails process working directory.")] string? workingDirectory = null,
        [Description("Shell to use: auto, pwsh, bash, or zsh. auto maps to pwsh on Windows, bash on Linux, zsh on macOS.")] string? shell = "auto",
        [Description("Maximum time the command may run before VibeRails cancels and recycles the worker. Default 60, max 3600.")] int timeoutSeconds = 60,
        [Description("Whether this tool call should wait for completion. If false, returns a job id immediately.")] bool waitForCompletion = true,
        [Description("How long this MCP call should wait before returning a running job id. Default 30, max 3600.")] int waitSeconds = 30,
        [Description("Maximum retained stdout/stderr characters per stream. Default 20000, max 200000.")] int maxOutputChars = 20000)
    {
        try
        {
            var result = await _commands.RunAsync(new HostShellCommandRequest(
                command,
                workingDirectory,
                shell,
                timeoutSeconds,
                waitSeconds,
                waitForCompletion,
                maxOutputChars));

            return FormatResult(result);
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Get the status and retained output for a shell command job started by run_shell_command.")]
    public string GetShellCommandStatus(
        [Description("Job id returned by run_shell_command.")] string jobId)
    {
        var result = _commands.GetStatus(jobId);
        return result == null ? $"FAIL: shell command job not found: {jobId}" : FormatResult(result);
    }

    [McpServerTool]
    [Description("Cancel a running or queued shell command job. Running jobs are interrupted by recycling their reusable shell worker.")]
    public async Task<string> CancelShellCommand(
        [Description("Job id returned by run_shell_command.")] string jobId)
    {
        var result = await _commands.CancelAsync(jobId);
        return result == null ? $"FAIL: shell command job not found: {jobId}" : FormatResult(result);
    }

    private static string FormatResult(HostShellCommandResult result)
    {
        var sb = new StringBuilder();
        var verdict = result.Status switch
        {
            HostShellCommandStatus.Completed when result.ExitCode == 0 => "PASS",
            HostShellCommandStatus.Queued or HostShellCommandStatus.Running => "RUNNING",
            HostShellCommandStatus.Cancelled => "CANCELLED",
            HostShellCommandStatus.TimedOut => "TIMEOUT",
            _ => "FAIL"
        };

        sb.AppendLine($"{verdict}: job={result.JobId} status={result.Status} exitCode={result.ExitCode?.ToString() ?? "?"}");
        sb.AppendLine($"shell={result.Shell} worker={result.WorkerId ?? "pending"} cwd={result.WorkingDirectory}");
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            sb.AppendLine($"message={result.Message}");
        }

        if (!string.IsNullOrEmpty(result.Stdout))
        {
            sb.AppendLine();
            sb.AppendLine("stdout:");
            sb.AppendLine(result.Stdout.TrimEnd());
        }

        if (!string.IsNullOrEmpty(result.Stderr))
        {
            sb.AppendLine();
            sb.AppendLine("stderr:");
            sb.AppendLine(result.Stderr.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }
}

