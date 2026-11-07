using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Jobs;
using VibeRails.Utils;

namespace VibeRails.Services.Launches;

/// <summary>Enqueue-only Git events. A running root owns all later execution.</summary>
public sealed class LaunchEventService(ILaunchStore store, LaunchService launches)
{
    public static LaunchEventService Create()
    {
        var state = ParserConfigs.GetStatePath();
        if (string.IsNullOrWhiteSpace(state)) state = Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.STATE_FILENAME);
        var store = SqliteStorage.CreateLaunchStore(state);
        return new(store, new LaunchService(store, new AutomationScriptService(new JobExecutableResolver()), SqliteStorage.CreateBoardStore(state)));
    }

    public async Task<int> CountAsync(string project, JobTriggerKind kind, CancellationToken ct) =>
        (await store.ListBindingsAsync(LaunchService.ProjectKey(project), ct: ct)).Count(b => b.Enabled && b.Trigger.Kind == kind);

    public async Task<int> EnqueueAsync(string project, JobTriggerKind kind, string occurrence, CancellationToken ct)
    {
        var count = 0;
        foreach (var binding in await store.ListBindingsAsync(LaunchService.ProjectKey(project), ct: ct))
        {
            if (!binding.Enabled || binding.Trigger.Kind != kind) continue;
            try
            {
                await launches.EnqueueAsync(binding.DefinitionType, binding.DefinitionId,
                    new(project, project, kind == JobTriggerKind.Commit ? "after-commit" : "before-commit",
                        $"event:{binding.Id}:{kind}:{occurrence}", binding.Id), ct);
                count++;
            }
            catch (Exception ex) when (ex is LaunchValidationException or LaunchConflictException or AutomationScriptValidationException)
            { Serilog.Log.Warning("[Launches] Event binding {Binding} is ineligible: {Reason}", binding.Id, ex.Message); }
        }
        return count;
    }
}
