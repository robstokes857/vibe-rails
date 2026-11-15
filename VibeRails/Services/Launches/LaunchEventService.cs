using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Jobs;
using VibeRails.Utils;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.Launches;

/// <summary>Enqueue-only Git events. A running root owns all later execution.</summary>
public sealed class LaunchEventService(ILaunchStore store, LaunchService launches)
{
    /// <summary>Queues actual violations independently of the commit gate; never executes the targets.</summary>
    public async Task<IReadOnlyList<string>> EnqueueRuleViolationsAsync(GitStagedSnapshot snapshot,
        VcaHookKind stage, IReadOnlyList<VcaRuleViolation> violations, CancellationToken ct)
    {
        if (stage is not (VcaHookKind.PreCommit or VcaHookKind.CommitMessage) || violations.Count == 0) return [];
        var bindings = (await store.ListBindingsAsync(LaunchService.ProjectKey(snapshot.RepositoryPath), ct: ct))
            .Where(binding => binding.Enabled && binding.Trigger.Kind == JobTriggerKind.RuleViolation && binding.Rule is not null)
            .ToArray();
        if (bindings.Length == 0) return [];
        List<string> output = [];
        foreach (var violation in violations)
        {
            if (violation.Enforcement is not ("WARN" or "COMMIT" or "STOP")) continue;
            var ruleId = RuleLaunchIdentity.Id(violation.SourcePath, violation.Rule);
            foreach (var binding in bindings.Where(binding => binding.Rule!.Matches(violation.SourcePath, violation.Rule)))
            {
                var label = $"VCA response for {violation.SourcePath}: {violation.Rule} → {binding.DefinitionId}";
                if (RuleLaunchDelivery.IsResponseProcess)
                {
                    output.Add(label + ": suppressed inside a rule response to prevent a launch loop.");
                    continue;
                }
                if (snapshot.StagedIdentity is not { } identity)
                {
                    output.Add(label + ": not queued because staged snapshot identity is unavailable.");
                    continue;
                }
                // Deliberately exclude the binding ID and hook stage: duplicate associations, retries
                // and pre-commit/commit-msg evaluation of the same snapshot share one occurrence.
                var occurrence = "rule:" + RuleLaunchIdentity.Hash(ruleId, binding.DefinitionType, binding.DefinitionId,
                    identity.BaseCommit ?? "", identity.BaseTree, identity.StagedTree);
                var context = new RuleLaunchContext(ruleId, violation.SourcePath, violation.Rule, violation.Enforcement,
                    violation.Reason, violation.ChangedFiles, snapshot.RepositoryPath, identity.BaseTree,
                    identity.StagedTree, identity.BaseCommit, stage == VcaHookKind.PreCommit ? "pre-commit" : "commit-msg");
                try
                {
                    var run = await launches.EnqueueAsync(binding.DefinitionType, binding.DefinitionId,
                        new(snapshot.RepositoryPath, snapshot.RepositoryPath, "rule-violation", occurrence,
                            binding.Id, RuleViolation: context), ct);
                    output.Add(label + $": recorded run {run.Id} ({run.Status}). Execution requires an open VibeRails root.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output.Add(label + ": could not queue: " + ex.Message);
                    Serilog.Log.Warning(ex, "[Launches] Rule binding {Binding} could not queue", binding.Id);
                }
            }
        }
        return output;
    }

    public static LaunchEventService Create()
    {
        var state = ParserConfigs.GetStatePath();
        if (string.IsNullOrWhiteSpace(state)) state = Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.STATE_FILENAME);
        return Create(state);
    }

    /// <summary>
    /// Git hooks only enqueue, and must keep working when board.db is busy, newer or damaged, so
    /// this never opens it: the LaunchService has no Board store, and the root that starts each
    /// session adds the nudge and App/Board context (VIBE-296).
    /// </summary>
    internal static LaunchEventService Create(string statePath)
    {
        var store = SqliteStorage.CreateLaunchStore(statePath);
        return new(store, new LaunchService(store, new AutomationScriptService(new JobExecutableResolver())));
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
