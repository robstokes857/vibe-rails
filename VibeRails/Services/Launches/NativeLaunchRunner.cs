using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Environments;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.Launches;

/// <summary>Executes one claimed snapshot in its own OS terminal, with the normal recording and cancellation lifecycle.</summary>
public static class NativeLaunchRunner
{
    /// <summary>Runs the immutable definition selected by the root; it never enqueues work or starts a scheduler.</summary>
    public static async Task<int> RunAsync(string id, IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        var store = scoped.GetRequiredService<ILaunchStore>();
        var run = await store.GetRunAsync(id, ct);
        if (run is not { Status: "claimed", ClaimToken: not null } || !run.Snapshot.Context.NativeTerminal)
        {
            Console.Error.WriteLine("This native launch is no longer available.");
            return 1;
        }
        var claim = new LaunchClaim(run.Id, run.ClaimToken);
        try
        {
            var service = scoped.GetRequiredService<LaunchService>();
            await service.ValidateSnapshotAsync(run.Snapshot, ct);
            var board = scoped.GetRequiredService<IBoardStore>();
            await LaunchBoardValidation.ValidateAsync(board, run, ct);
            if (run.Snapshot.Action is not null)
            {
                Console.WriteLine(run.Snapshot.Action.Name);
                await LaunchDispatcher.ExecuteActionAsync(scoped.GetRequiredService<IServiceScopeFactory>(), run, claim, ct,
                    nativeTerminal: true);
                return (await store.GetRunAsync(id, CancellationToken.None))?.ExitCode ?? 1;
            }

            var preset = run.Snapshot.Preset!;
            var context = run.Snapshot.Context;
            var card = context.CardId is null ? null : await board.FindCardAsync(context.ProjectPath, context.CardId, ct);
            var prompt = await scoped.GetRequiredService<IPromptPlaceholderService>().ResolveLaunchAsync(
                context.ComposedPrompt ?? context.PromptOverride ?? preset.InitialPrompt,
                new(context.WorkingDirectory, EnvironmentName: preset.Name, BoardCardKey: card?.Key), ct);
            var execution = new LaunchExecution(store, service, run, board);
            return await scoped.GetRequiredService<TerminalRunner>().RunCliWithWebAsync(
                preset.Cli, prompt.WorkingDirectory, null, LaunchOptionBuilder.Build(preset),
                scoped.GetRequiredService<ITerminalSessionService>(), ct: ct,
                initialPrompt: prompt.Prompt, authorizeBoardTools: context.AuthorizeBoardTools,
                contextBoardId: context.BoardId, contextProjectPath: context.Root,
                contextIncluded: context.ContextIncluded, launch: execution);
        }
        catch (Exception ex)
        {
            // A duplicate native child must not finalize another child's successful start.
            var current = await store.GetRunAsync(id, CancellationToken.None);
            if (current?.Status == "claimed")
                await store.CompleteUnstartedAsync(claim, ct.IsCancellationRequested ? "cancelled" : "failed",
                    ex.Message, CancellationToken.None);
            else if (current?.OwnerPid == Environment.ProcessId)
                await store.CompleteAsync(claim, ct.IsCancellationRequested ? "cancelled" : "failed",
                    exitCode: 1, error: ex.Message, ct: CancellationToken.None);
            Console.Error.WriteLine($"Could not run this definition: {ex.Message}");
            Serilog.Log.Warning(ex, "[Launches] Native launch failed for {RunId}", id);
            return 1;
        }
    }
}
