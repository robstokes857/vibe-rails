using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis;

/// <summary>
/// The single application pipeline for opening an Environment in a native terminal.
///
/// The Environments API and Automation both use this service so saved arguments, the initial
/// prompt, recency tracking, and the final <c>vb --env</c> launch cannot drift between callers.
/// </summary>
public interface IEnvironmentLaunchService
{
    Task<LaunchResult> LaunchAsync(
        LLM llm,
        LaunchCliRequest request,
        string fallbackWorkingDirectory,
        string[]? vbArgs = null,
        bool keepTerminalOpen = true,
        int? environmentId = null,
        bool launchMinimized = false,
        CancellationToken cancellationToken = default);
}

public sealed class EnvironmentLaunchService(
    IRepository repository,
    ILaunchLLMService launchService) : IEnvironmentLaunchService
{
    public async Task<LaunchResult> LaunchAsync(
        LLM llm,
        LaunchCliRequest request,
        string fallbackWorkingDirectory,
        string[]? vbArgs = null,
        bool keepTerminalOpen = true,
        int? environmentId = null,
        bool launchMinimized = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? fallbackWorkingDirectory
            : request.WorkingDirectory;
        var args = request.Args?.ToList() ?? [];
        var environmentName = string.IsNullOrWhiteSpace(request.EnvironmentName)
            ? null
            : request.EnvironmentName;

        try
        {
            if (environmentId is not null || !string.IsNullOrWhiteSpace(environmentName))
            {
                var environment = await ResolveEnvironmentAsync(
                    llm,
                    environmentId,
                    environmentName,
                    cancellationToken);
                if (environment is null)
                {
                    var label = string.IsNullOrWhiteSpace(environmentName)
                        ? $"id {environmentId}"
                        : $"'{environmentName}'";
                    return new LaunchResult(false, $"Environment {label} was not found for {llm}.");
                }

                environmentName = environment.CustomName;
                if (!string.IsNullOrWhiteSpace(environment.CustomArgs))
                    args.InsertRange(0, ShellArgSanitizer.ParseAndValidate(environment.CustomArgs));
                LlmPromptArgvBuilder.AppendInitialPrompt(args, llm, environment.CustomPrompt);

                try
                {
                    // Timestamp-only on purpose: a full-record update here would write back
                    // every column from the read above, silently reverting any edit saved
                    // between that read and this launch.
                    await repository.TouchEnvironmentLastUsedAsync(environment.Id, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Recency is cosmetic bookkeeping; a busy state.db must not veto the launch.
                    Log.Warning(ex, "[Launch] Could not update LastUsedUTC for environment {EnvironmentId}", environment.Id);
                }
            }

            return launchService.LaunchInTerminal(
                llm,
                environmentName,
                workingDirectory,
                args.ToArray(),
                vbArgs,
                keepTerminalOpen,
                launchMinimized);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            // Validation the user can act on — e.g. an unparseable CustomArgs string from
            // ShellArgSanitizer. Those messages are written for the user; surface them verbatim.
            return new LaunchResult(false, $"Could not launch {llm}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Infrastructure faults (DB, IO). Their messages can carry paths and internals,
            // so keep the details in the log and hand the caller a generic failure.
            Log.Error(ex, "[Launch] Environment launch pipeline failed for {Llm}", llm);
            return new LaunchResult(
                false,
                $"Could not launch {llm}: an internal error occurred. See the VibeRails log for details.");
        }
    }

    private async Task<LLM_Environment?> ResolveEnvironmentAsync(
        LLM llm,
        int? environmentId,
        string? environmentName,
        CancellationToken cancellationToken)
    {
        if (environmentId is int id)
        {
            var byId = await repository.GetEnvironmentByIdAsync(id, cancellationToken);
            // Automation persists the Environment's stable ID. If that record is gone or changed
            // provider, do not fall back to its old display name: a newly-created Environment may
            // legitimately reuse that name but carry entirely different arguments or permissions.
            return byId is not null && byId.LLM == llm ? byId : null;
        }

        return string.IsNullOrWhiteSpace(environmentName)
            ? null
            : await repository.GetEnvironmentByNameAndLlmAsync(environmentName, llm, cancellationToken);
    }
}
