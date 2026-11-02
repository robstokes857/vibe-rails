using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Board;
using VibeRails.Services.Diagnostics;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>Requests one title per conversation outside the terminal input path.</summary>
public sealed class ConversationTitleService(IConversationTitleStore store, IUserInputStore inputs,
    IBoardStore boards, ConversationTitleClient client, IAppEventBus events, IFeatureLog? featureLog = null)
{
    private readonly IFeatureLog log = featureLog ?? NullFeatureLog.Instance;

    /// <summary>A conversation whose own requests keep failing stops asking after this many tries.</summary>
    internal const int MaxSessionAttempts = 5;

    /// <summary>Only a root backend calls this; claims coordinate all open roots.</summary>
    public async Task ProcessOneAsync(CancellationToken ct)
    {
        ConversationTitleWork? work = null;
        string? fingerprint = null;
        try
        {
            var settings = client.CurrentSettings;
            if (!settings.CanSend) return;
            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey)));
            work = await store.ClaimAsync(DateTime.UtcNow, ct, fingerprint);
            if (work is null) return;
            var userText = work.Eligible ? UserText(await inputs.GetUserInputsForSessionAsync(work.SessionId, ct)) : "";
            if (userText.Length == 0)
            {
                await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                Write(work, "skipped", "No eligible user text; no title request sent.", "skipped");
                return;
            }

            // A Board-linked conversation is titled like any other; its story is background only.
            var story = await ReadStoryAsync(work.SessionId, ct);
            Write(work, "request-started",
                $"Starting automatic chat-title request to {ConversationTitleClient.Endpoint}; attempt={work.Attempts}; storyContext={story is not null}.", "started");
            var started = Stopwatch.GetTimestamp();
            var result = await client.GenerateAsync(userText, settings, ct, story);
            var status = result.Title is not null ? "succeeded"
                : result.Outcome is "no_topic" or "no_user_text" or "settings_changed_or_disabled" ? "skipped" : "failed";
            Write(work, "request-finished",
                $"Automatic chat-title request finished; HTTP={result.HttpStatusCode?.ToString() ?? "none"}; elapsedMs={(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds}; outcome={result.Outcome}.",
                status, status == "failed" ? LogLevel.Warning : LogLevel.Information);
            if (settings != client.CurrentSettings)
            {
                await store.DeferAsync(work, DateTime.UtcNow.AddMinutes(1), ct);
                Write(work, "deferred", "Settings changed; result discarded and request deferred for 60 seconds.", "skipped");
                return;
            }
            if (result.Title is not null)
            {
                if (await store.CompleteAsync(work, result.Title, DateTime.UtcNow, ct))
                {
                    Write(work, "title-applied", "Generated chat title saved.", "succeeded");
                    events.Publish("session_title_updated", new ConversationTitleChanged(work.SessionId),
                        ConversationTitleJsonContext.Default.ConversationTitleChanged);
                }
                else
                    Write(work, "title-discarded", "Generated title not applied: session changed or request lease expired.", "skipped");
            }
            else if (result.Permanent || (!result.KeyWide && work.Attempts >= MaxSessionAttempts))
            {
                await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                Write(work, "stopped", $"Title requests stopped without changing the name; outcome={result.Outcome}; attempt={work.Attempts}; permanent={result.Permanent}.",
                    result.Outcome == "no_topic" ? "skipped" : "failed",
                    result.Outcome == "no_topic" ? LogLevel.Information : LogLevel.Warning);
            }
            else
            {
                // Only a key-wide failure pauses other conversations; this one's own retry backs off either way.
                var backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(work.Attempts - 1, 10)));
                var delay = result.RetryAfter > backoff ? result.RetryAfter : backoff;
                await store.DeferAsync(work, DateTime.UtcNow + delay, ct,
                    result.KeyWide ? fingerprint : null);
                Write(work, "retry-scheduled", $"Retry scheduled in {delay.TotalSeconds:F0} seconds; scope={(result.KeyWide ? "all conversations for this API key" : "this conversation")}; outcome={result.Outcome}.",
                    "failed", LogLevel.Warning);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (work is not null) Write(work, "cancelled", "Title processing cancelled by host shutdown.", "cancelled");
            throw;
        }
        catch (Exception)
        {
            // No user text, credential, remote body or exception text enters JobBase's logs.
            // A conversation that fails to load is its own problem: never pause the key for it.
            if (work is null) return;
            Write(work, "processing-failed", "Title processing failed locally; no exception text was recorded.", "failed", LogLevel.Warning);
            try
            {
                if (work.Attempts >= MaxSessionAttempts)
                {
                    await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                    Write(work, "stopped", "Title requests stopped after repeated local failures.", "failed", LogLevel.Warning);
                }
                else
                {
                    await store.DeferAsync(work, DateTime.UtcNow.AddMinutes(5), ct);
                    Write(work, "retry-scheduled", "Retry scheduled in 300 seconds after a local failure; scope=this conversation.", "failed", LogLevel.Warning);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                Write(work, "retry-not-saved", "Could not save the retry state; the durable lease will expire.", "failed", LogLevel.Warning);
            }
        }
    }

    private void Write(ConversationTitleWork work, string eventName, string message, string status,
        LogLevel level = LogLevel.Information) =>
        log.Write("conversation-titles", eventName, message, operationId: work.Lease,
            subject: work.SessionId, status: status, level: level);

    /// <summary>
    /// The user's captured messages in order. Bare slash commands (<c>/clear</c>, <c>/model</c>) say
    /// nothing about the topic, so they are left out unless they are all there is.
    /// </summary>
    internal static string UserText(IEnumerable<UserInputRecord> inputs)
    {
        var messages = inputs.OrderBy(input => input.Sequence).ThenBy(input => input.Id)
            .Select(input => input.InputText.Trim()).Where(text => text.Length > 0).ToList();
        var topical = messages.Where(text => !IsBareCommand(text)).ToList();
        return string.Join('\n', topical.Count > 0 ? topical : messages);
    }

    private static bool IsBareCommand(string text) => text[0] == '/' && !text.Any(char.IsWhiteSpace);

    /// <summary>The conversation's primary card, if any. A Board read failure never blocks the title.</summary>
    private async Task<ConversationTitleStory?> ReadStoryAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var link = (await boards.GetSessionCardsAsync([sessionId], ct)).FirstOrDefault();
            if (link is null) return null;
            var card = await boards.FindLocalCardAsync(link.CardId, ct);
            return new(link.DisplayId ?? link.Key, card?.Title ?? link.Title, card?.Description ?? "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}

internal sealed record ConversationTitleChanged(string? SessionId);
