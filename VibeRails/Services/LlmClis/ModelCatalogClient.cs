using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis;

public sealed record ModelName(string Cli, string Value, string Label);
public sealed record ModelCatalogSnapshot(int Version, int Revision, ModelName[] Models);
public sealed record CachedModelCatalog(ModelCatalogSnapshot? Snapshot, DateTimeOffset RefreshAfter);

/// <summary>Lazy public catalog reads. Memory fronts the existing machine-wide persistent cache.</summary>
public sealed class ModelCatalogClient(IHttpClientFactory clients, IServiceScopeFactory scopes, IMemoryCache memory, TimeProvider clock)
{
    public const string ClientName = "model-catalog";
    public const string Endpoint = "https://viberails.ai/public/api/v1/models";
    public const string CacheKey = "llm-model-catalog:v1";
    private readonly object sync = new();
    private Task<ModelCatalogSnapshot?>? refresh;
    private CachedModelCatalog? last;

    public Task<ModelCatalogSnapshot?> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (memory.TryGetValue<CachedModelCatalog>(CacheKey, out var cached) && cached is not null && clock.GetUtcNow() < cached.RefreshAfter)
                return Task.FromResult(cached.Snapshot);
            if (refresh is null || refresh.IsCompleted) refresh = RefreshAsync();
            return refresh.WaitAsync(ct);
        }
    }

    private async Task<ModelCatalogSnapshot?> RefreshAsync()
    {
        using var scope = scopes.CreateScope();
        var global = scope.ServiceProvider.GetRequiredService<IGlobalCache>();
        CachedModelCatalog? saved = last;
        try
        {
            var json = await global.GetAsync(CacheKey);
            if (json is { Length: <= 1048576 })
            {
                var stored = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CachedModelCatalog);
                // Reject malformed or implausibly future-dated cache entries without erasing the last good copy.
                if (stored is not null && (stored.Snapshot is null || Valid(stored.Snapshot)) &&
                    stored.RefreshAfter <= clock.GetUtcNow().AddHours(1) &&
                    (saved?.Snapshot is null || stored.Snapshot?.Revision >= saved.Snapshot.Revision)) saved = stored;
            }
        }
        catch (Exception) { /* A cache failure cannot prevent opening a picker. */ }

        if (saved is null || clock.GetUtcNow() >= saved.RefreshAfter)
        {
            ModelCatalogSnapshot? next = null;
            try
            {
                using var client = clients.CreateClient(ClientName);
                using var response = await client.GetAsync(Endpoint);
                response.EnsureSuccessStatusCode();
                next = JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(), AppJsonSerializerContext.Default.ModelCatalogSnapshot);
                if (!Valid(next) || (saved?.Snapshot is { } old && next!.Revision < old.Revision)) next = null;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { }
            saved = new(next ?? saved?.Snapshot, clock.GetUtcNow() + (next is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1)));
            try { await global.SetAsync(CacheKey, JsonSerializer.Serialize(saved, AppJsonSerializerContext.Default.CachedModelCatalog)); }
            catch (Exception) { /* Keep the in-memory copy when persistence is temporarily unavailable. */ }
        }
        last = saved;
        memory.Set(CacheKey, saved, TimeSpan.FromMinutes(1));
        return saved.Snapshot;
    }

    public static bool Valid(ModelCatalogSnapshot? value) => value is { Version: 1, Revision: > 0, Models.Length: <= 500 } &&
        value.Models.All(m => m is not null && CliNames.Contains(m.Cli) &&
            m.Value is { Length: > 0 and <= 200 } && m.Value == m.Value.Trim() && BaseLlmOptionsBuilder.IsValidModelName(m.Value) &&
            (m.Cli == "claude" || !m.Value.EndsWith("[1m]", StringComparison.Ordinal)) &&
            m.Label is { Length: > 0 and <= 200 } && m.Label == m.Label.Trim() && !m.Label.Any(char.IsControl)) &&
        value.Models.Select(m => (m.Cli, m.Value)).Distinct().Count() == value.Models.Length;

    private static readonly string[] CliNames = ["codex", "claude", "copilot", "antigravity", "opencode", "grok"];
}
