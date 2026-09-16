using VibeRails.DTOs;
using VibeRails.Services;

namespace VibeRails.DB;

public interface IEnvironmentStore
{
    Task<LLM_Environment?> GetEnvironmentByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<LLM_Environment?> GetEnvironmentByNameAndLlmAsync(string name, LLM llm, CancellationToken cancellationToken = default);

    Task<LLM_Environment?> FindEnvironmentByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<LLM_Environment?> FindEnvironmentByNameIgnoreCaseAsync(string name, CancellationToken cancellationToken = default);

    Task<LLM_Environment> GetOrCreateEnvironmentAsync(string name, LLM llm, CancellationToken cancellationToken = default);

    Task<List<LLM_Environment>> GetAllEnvironmentsAsync(CancellationToken cancellationToken = default);

    Task<List<LLM_Environment>> GetCustomEnvironmentsAsync(CancellationToken cancellationToken = default);

    Task<LLM_Environment> SaveEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default);

    Task UpdateEnvironmentAsync(LLM_Environment environment, CancellationToken cancellationToken = default);

    /// <summary>Atomically stamps LastUsedUTC without touching any other column.</summary>
    Task TouchEnvironmentLastUsedAsync(int environmentId, CancellationToken cancellationToken = default);

    Task DeleteEnvironmentAsync(int id, CancellationToken cancellationToken = default);

    Task<List<EnvironmentStep>> GetStepsForEnvironmentAsync(int environmentId, CancellationToken cancellationToken = default);

    /// <summary>One query for many environments' steps, indexed by owner. Avoids an N+1 in the list endpoint.</summary>
    Task<Dictionary<int, List<EnvironmentStep>>> GetStepsForEnvironmentsAsync(IReadOnlyList<int> environmentIds, CancellationToken cancellationToken = default);

    /// <summary>Enabled steps for one phase, in Position order — exactly what the runner executes.</summary>
    Task<List<EnvironmentStep>> GetEnabledStepsAsync(int environmentId, EnvironmentStepPhase phase, CancellationToken cancellationToken = default);

    Task<bool> HasEnabledStepsAsync(int environmentId, EnvironmentStepPhase phase, CancellationToken cancellationToken = default);

    /// <summary>One step by GUID, scoped to its environment — backs {{step:&lt;id&gt;}} prompt references. Null = deleted.</summary>
    Task<EnvironmentStep?> GetStepByIdAsync(int environmentId, string stepId, CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the whole list, stamping Position from array order.</summary>
    Task ReplaceStepsAsync(int environmentId, IReadOnlyList<EnvironmentStep> steps, CancellationToken cancellationToken = default);
}
