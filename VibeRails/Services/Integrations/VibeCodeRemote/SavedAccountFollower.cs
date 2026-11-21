using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.HttpRelay;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Keeps this process on the saved viberails.ai account. Signing in, switching accounts and
/// removing the key each happen in one root backend, which updates its own runtime credential.
/// Every other VibeRails process read the key once at startup: a second window, and the tab
/// children that run the CLIs, kept uploading with the previous key (or none) until restarted,
/// and a second window went on showing the previous account.
/// <para>
/// The saved settings file is the credential. This checks its write time every few seconds and
/// re-reads it only when it changed, under the same lock every writer in this process holds
/// from its save through its own activation.
/// </para>
/// </summary>
public sealed class SavedAccountFollower : BackgroundService
{
    /// <summary>Tells an open dashboard to read the saved account again. Carries no credential.</summary>
    public const string ChangedEvent = "account_settings_changed";

    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonElement NoPayload = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly SettingsFile _settings;
    private readonly IRemoteHttpRelayClient? _relay;
    private readonly IAppEventBus? _events;
    private readonly Func<(DateTime WriteTimeUtc, long Length)?> _applied;
    private (DateTime WriteTimeUtc, long Length)? _seen;

    /// <param name="applied">
    /// The stamp of the file this process took its account from (<c>Init.SavedAccountStamp</c>).
    /// Null means nothing did, as in a host composed without startup: there is then nothing to
    /// keep in step, and the settings file is never read.
    /// </param>
    /// <param name="events">Only a root backend has dashboards to tell; a tab child passes null.</param>
    internal SavedAccountFollower(SettingsFile settings, Func<(DateTime WriteTimeUtc, long Length)?> applied,
        IRemoteHttpRelayClient? relay, IAppEventBus? events)
    {
        _settings = settings;
        _applied = applied;
        _relay = relay;
        _events = events;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Check();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Applies the saved account when another process changed it. Returns true only then: a save
    /// made by this process has already activated its key, so it is not reported again.
    /// </summary>
    internal bool Check()
    {
        bool changed;
        try
        {
            _seen ??= _applied();
            if (_seen is null || _settings.Stamp() == _seen)
                return false;

            using (_settings.AcquireWriteLock())
            {
                var saved = _settings.LoadFresh();
                changed = !string.Equals(saved.ApiKey, ParserConfigs.GetApiKey(), StringComparison.Ordinal);
                if (changed) global::VibeRails.Init.ApplySavedAccount(saved);
                // Read inside the lock, after the load (which rewrites a missing file): a save
                // that lands later is seen on the next tick.
                _seen = _settings.Stamp() ?? default((DateTime, long));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException)
        {
            // Busy, mid-replacement or unreadable: keep the current key and look again next tick.
            return false;
        }

        if (!changed)
            return false;

        Serilog.Log.Information(
            "[Account] The saved viberails.ai account changed in another VibeRails process; this process now uses it. processId={ProcessId}",
            Environment.ProcessId);
        // The relay socket authenticated with the previous key at its handshake.
        try { _relay?.Reset(); }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[Account] Could not reset the HTTP relay after an account change ({ExceptionType})",
                ex.GetType().Name);
        }
        try { _events?.Publish(new AppEvent(ChangedEvent, NoPayload)); }
        catch (Exception ex)
        {
            // A broken subscriber cannot undo the change: the credential is already in use.
            Serilog.Log.Warning("[Account] An account-change subscriber failed ({ExceptionType})", ex.GetType().Name);
        }
        return true;
    }
}
