using System.Globalization;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Board;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Terminal;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public interface IJobTerminalTabLauncher
{
    Task<LaunchResult> LaunchAsync(JobRunRecord run, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the ordinary self-bookkeeping workflow process inside a recorded shell tab. The same
/// child still owns ordering, deadlines and cancellation; the outer PTY records every action.
/// </summary>
public sealed class JobTerminalTabLauncher(
    ITerminalTabHostService tabs,
    IRepository repository,
    IJobStore store,
    IRunWorkspaceService workspaces,
    IBoardStore boards,
    IAppEventBus? events = null) : IJobTerminalTabLauncher
{
    public async Task<LaunchResult> LaunchAsync(JobRunRecord run, CancellationToken cancellationToken = default)
    {
        var directory = run.ProjectPath;
        var arguments = JobLaunchService.BuildStandaloneVbArgs(run);
        var worker = run.Actions?.SingleOrDefault(action => action.Kind == JobActionKind.Worker);
        var environmentId = worker?.EnvironmentId ?? run.EnvironmentId;
        if (environmentId is int id)
        {
            // Resolve the immutable id, not a reusable name, before provisioning the workspace.
            var environment = await repository.GetEnvironmentByIdAsync(id, cancellationToken);
            var llm = worker?.Llm ?? run.Llm;
            if (environment is null || environment.LLM != llm
                || !ProjectPathComparer.IsVisibleIn(environment.ProjectPath, run.ProjectPath))
                return new LaunchResult(false, "The Automation Worker is no longer available in this project.");

            var workspace = await workspaces.ResolveAsync(environment, run.ProjectPath, cancellationToken);
            if (!workspace.Success)
                return new LaunchResult(false, workspace.Error!);
            directory = workspace.WorkingDirectory;
            var cliArgs = string.IsNullOrWhiteSpace(environment.CustomArgs)
                ? [] : ShellArgSanitizer.ParseAndValidate(environment.CustomArgs).ToArray();
            arguments = BaseLlmCliLauncher.BuildVbArgv(llm, directory, cliArgs, environment.CustomName,
                [.. JobLaunchService.BuildVbArgs(run), "--env-id", id.ToString(CultureInfo.InvariantCulture)]);
            try { await repository.TouchEnvironmentLastUsedAsync(id, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[Jobs] Could not update Worker {EnvironmentId} recency", id);
            }
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the VibeRails executable path.");
        var tab = await tabs.CreateAutomationTabAsync(run.Id, run.JobName, cancellationToken);
        try
        {
            var session = await tabs.StartSessionAsync(tab.TabId,
                new StartTerminalRequest(WorkingDirectory: directory, Cli: "shell", Title: $"Automation: {run.JobName}", MakeRemote: false),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(session.SessionId))
                throw new InvalidOperationException("The Automation terminal did not start a session.");
            await store.LinkRunTerminalSessionAsync(run.Id, session.SessionId, cancellationToken);
            try
            {
                await BoardAutomationSessionLinker.LinkAsync(boards, run, session.SessionId, tab.TabId, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Jobs] Could not link Automation {RunId} to its Board card", run.Id);
            }
            var input = await tabs.SendInputAsync(tab.TabId,
                new TerminalInputRequest(BuildRunCommand(executable, arguments, OperatingSystem.IsWindows()), Submit: true,
                    ExpectedSessionId: session.SessionId), cancellationToken);
            if (!input.Success)
                throw new InvalidOperationException(input.Message);
            try
            {
                events?.Publish("automation_terminal_started",
                    new AutomationTerminalStartedPayload(tab.TabId, session.SessionId, run.JobName, directory),
                    AppJsonSerializerContext.Default.AutomationTerminalStartedPayload);
            }
            catch (Exception ex) { Log.Warning(ex, "[Jobs] Could not announce Automation tab {TabId}", tab.TabId); }
            return new LaunchResult(true, "Automation launched in a terminal tab.");
        }
        catch
        {
            try { await tabs.DeleteTabAsync(tab.TabId, CancellationToken.None); }
            catch (Exception ex) { Log.Warning(ex, "[Jobs] Could not clean up Automation tab {TabId}", tab.TabId); }
            throw;
        }
    }

    internal static string BuildRunCommand(string executable, IReadOnlyList<string> arguments, bool windows)
    {
        static string PowerShellLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
        static string PosixLiteral(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
        // Every value is a shell literal. End the wrapper when the workflow exits, so history
        // and tab status do not leave a completed Automation looking active indefinitely.
        return windows
            ? $"& {PowerShellLiteral(executable)} @({string.Join(", ", arguments.Select(PowerShellLiteral))}); exit $LASTEXITCODE"
            : $"exec {PosixLiteral(executable)} {string.Join(" ", arguments.Select(PosixLiteral))}";
    }
}
