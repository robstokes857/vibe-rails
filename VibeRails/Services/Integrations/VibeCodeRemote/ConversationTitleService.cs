using System.Security.Cryptography;
using System.Text;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services.Board;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>Processes one durable title milestone outside the terminal input path.</summary>
public sealed class ConversationTitleService(IConversationTitleStore store, ISessionTranscriptService transcripts,
    IBoardStore boards, ConversationTitleClient client, IAppEventBus events)
{
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
            if (!work.Eligible || await HasBoardAsync(work, ct))
            {
                await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                return;
            }

            // The transcript parser observes cancellation. Finish inside the two-minute lease,
            // even for a large recording; an interrupted root leaves the claim recoverable.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var transcript = await transcripts.BuildCurrentAsync(work.SessionId, deadline.Token);
            if (await HasBoardAsync(work, deadline.Token))
            {
                await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                return;
            }
            var result = await client.GenerateAsync(transcript, settings, deadline.Token,
                work.Generation > 0 ? work.LastExcerptHash : null);
            if (await HasBoardAsync(work, ct))
            {
                await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                return;
            }
            if (settings != client.CurrentSettings)
            {
                await store.DeferAsync(work, DateTime.UtcNow.AddMinutes(1), ct);
                return;
            }
            if (result.Unchanged)
            {
                // The one refresh cannot improve the title with the same opening. End its work
                // durably without rewriting the name or publishing a title-change event.
                await store.SkipUnchangedRefreshAsync(work, DateTime.UtcNow, ct);
            }
            else if (result.Title is not null)
            {
                if (await store.CompleteAsync(work, result.Title, DateTime.UtcNow, ct, result.ExcerptHash))
                    events.Publish("session_title_updated", new ConversationTitleChanged(work.SessionId),
                        ConversationTitleJsonContext.Default.ConversationTitleChanged);
            }
            else if (result.Permanent || (!result.KeyWide && work.Attempts >= MaxSessionAttempts))
                await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
            else
            {
                // Only a key-wide failure pauses other conversations; this one's own retry backs off either way.
                var backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(work.Attempts - 1, 10)));
                await store.DeferAsync(work, DateTime.UtcNow + (result.RetryAfter > backoff ? result.RetryAfter : backoff), ct,
                    result.KeyWide ? fingerprint : null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // No transcript, credential, remote body or exception text enters JobBase's logs.
            // A transcript that fails to build is this conversation's problem: never pause the key for it.
            if (work is null) return;
            try
            {
                if (work.Attempts >= MaxSessionAttempts) await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                else await store.DeferAsync(work, DateTime.UtcNow.AddMinutes(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* The durable lease will expire. */ }
        }
    }

    private async Task<bool> HasBoardAsync(ConversationTitleWork work, CancellationToken ct) =>
        (await boards.GetSessionCardsAsync([work.SessionId], ct)).Count != 0;
}

internal sealed record ConversationTitleChanged(string? SessionId);
