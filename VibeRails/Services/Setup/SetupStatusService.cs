using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using VibeRails.DTOs;

namespace VibeRails.Services.Setup;

public interface ISetupStatusService
{
    /// <summary>
    /// The saved check while it says setup is complete and was made with this process's PATH.
    /// A machine never checked, or one still missing something, is probed again, so a CLI
    /// installed outside VibeRails clears the alert.
    /// </summary>
    Task<SetupStatusResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Probes every tool again and saves the result.</summary>
    Task<SetupStatusResponse> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Setup page's tool check, also read on every app load. One probe result is kept in
/// <see cref="IGlobalCache"/> per machine. Once it says every required tool and one AI CLI are
/// installed it is returned as-is until an install finishes, so a set-up machine does no
/// filesystem work on load. Until then each read probes again (a few ms of stat calls). Ready
/// and Restart needed depend on the PATH VibeRails started with, so a process launched with a
/// different PATH (after a restart, or from VS Code instead of the desktop) probes again too.
/// </summary>
public sealed class SetupStatusService(IGlobalCache cache) : ISetupStatusService
{
    public const string StatusCacheKey = "setup.tool-status.v1";

    // Scoped service, process-wide gate: two tabs opening the page at once probe only once.
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);

    private readonly IGlobalCache _cache = cache;

    /// <summary>Replaceable so tests can describe a machine; production probes this one.</summary>
    internal Func<SetupProbeEnvironment> ProbeEnvironment { get; init; } = SetupProbeEnvironment.ForCurrentMachine;
    /// <summary>The PATH new terminals inherit, the one <see cref="SetupProbeEnvironment.LaunchPath"/> splits.</summary>
    internal Func<string?> LaunchPathValue { get; init; } = () => Environment.GetEnvironmentVariable("PATH");
    internal SetupPlatform Platform { get; init; } = SetupToolCatalog.Current;
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public async Task<SetupStatusResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var launchPathHash = HashPath(LaunchPathValue());
        var saved = await ReadAsync();
        if (saved is not null && CanReuse(saved, launchPathHash)) return BuildResponse(saved);

        await ProbeGate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have finished a probe while this one waited.
            saved = await ReadAsync();
            if (saved is null || !CanReuse(saved, launchPathHash)) saved = await ProbeAndSaveAsync();
        }
        finally { ProbeGate.Release(); }
        return BuildResponse(saved);
    }

    public async Task<SetupStatusResponse> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await ProbeGate.WaitAsync(cancellationToken);
        SetupStatusDocument saved;
        try { saved = await ProbeAndSaveAsync(); }
        finally { ProbeGate.Release(); }
        return BuildResponse(saved);
    }

    /// <summary>Every required tool and at least one AI CLI found (a pending restart counts).</summary>
    internal bool IsComplete(SetupStatusDocument document)
    {
        var installed = document.Tools
            .Where(entry => entry.State != ToWire(SetupToolState.Missing))
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        var catalog = SetupToolCatalog.For(Platform);
        return catalog.Where(tool => tool.Group == SetupToolGroup.Required).All(tool => installed.Contains(tool.Id))
            && catalog.Any(tool => tool.Group == SetupToolGroup.AgentCli && installed.Contains(tool.Id));
    }

    /// <summary>
    /// A complete check made with the same launch PATH. "Restart needed" clears only in a process
    /// started with the new PATH, which is exactly when the hash stops matching.
    /// </summary>
    private bool CanReuse(SetupStatusDocument document, string launchPathHash) =>
        IsComplete(document) && document.LaunchPathHash == launchPathHash;

    private async Task<SetupStatusDocument> ProbeAndSaveAsync()
    {
        // Read before probing, so a hash never describes a newer PATH than the result does.
        var launchPathHash = HashPath(LaunchPathValue());
        var results = SetupToolProbe.Probe(SetupToolCatalog.For(Platform), ProbeEnvironment());
        var document = new SetupStatusDocument(
            SetupStatusDocument.CurrentVersion,
            Platform.ToString(),
            Clock(),
            results.Select(result => new SetupToolStatusEntry(
                result.Id, ToWire(result.State), result.Path, result.Version)).ToList(),
            launchPathHash);
        await _cache.SetAsync(StatusCacheKey,
            JsonSerializer.Serialize(document, AppJsonSerializerContext.Default.SetupStatusDocument));
        return document;
    }

    private async Task<SetupStatusDocument?> ReadAsync()
    {
        var json = await _cache.GetAsync(StatusCacheKey);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var document = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.SetupStatusDocument);
            // A document from another catalog version or OS (a shared home directory) is re-probed.
            return document?.Version == SetupStatusDocument.CurrentVersion && document.Platform == Platform.ToString()
                ? document
                : null;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "[Setup] Ignoring an unreadable saved tool check");
            return null;
        }
    }

    private SetupStatusResponse BuildResponse(SetupStatusDocument document)
    {
        var states = document.Tools.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var tools = SetupToolCatalog.For(Platform).Select(tool =>
        {
            states.TryGetValue(tool.Id, out var entry);
            return new SetupToolResponse(
                tool.Id,
                tool.Name,
                ToWire(tool.Group),
                tool.Why,
                tool.DocsUrl,
                tool.Brand,
                // A tool added to the catalog after the last check shows as missing until the
                // next probe, rather than triggering one on read.
                entry?.State ?? ToWire(SetupToolState.Missing),
                entry?.Path,
                entry?.Version,
                tool.Methods.Select((method, index) => new SetupInstallMethodResponse(
                    method.Id, method.Label, method.Command, method.Requires, method.Note, index == 0)).ToList());
        }).ToList();
        return new SetupStatusResponse(ToWire(Platform), document.CheckedUtc, tools);
    }

    // Hashed rather than stored: the cache only needs to know whether the PATH changed.
    private static string HashPath(string? path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path ?? string.Empty)));

    internal static string ToWire(SetupToolState state) => state switch
    {
        SetupToolState.Ready => "ready",
        SetupToolState.RestartNeeded => "restart",
        _ => "missing"
    };

    private static string ToWire(SetupToolGroup group) => group switch
    {
        SetupToolGroup.Required => "required",
        SetupToolGroup.AgentCli => "agent",
        SetupToolGroup.PackageManager => "package-manager",
        _ => "optional"
    };

    private static string ToWire(SetupPlatform platform) => platform switch
    {
        SetupPlatform.Windows => "windows",
        SetupPlatform.MacOS => "macos",
        _ => "linux"
    };
}
