using System.Security.Cryptography;
using System.Text;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Board;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>Requests one title per conversation outside the terminal input path.</summary>
public sealed class ConversationTitleService(IConversationTitleStore store, IUserInputStore inputs,
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
            var userText = work.Eligible ? UserText(await inputs.GetUserInputsForSessionAsync(work.SessionId, ct)) : "";
            if (userText.Length == 0)
            {
                await store.CompleteAsync(work, null, DateTime.UtcNow, ct);
                return;
            }

            // A Board-linked conversation is titled like any other; its story is background only.
            var story = await ReadStoryAsync(work.SessionId, ct);
            var result = await client.GenerateAsync(userText, settings, ct, story);
            if (settings != client.CurrentSettings)
            {
                await store.DeferAsync(work, DateTime.UtcNow.AddMinutes(1), ct);
                return;
            }
            if (result.Title is not null)
            {
                if (await store.CompleteAsync(work, result.Title, DateTime.UtcNow, ct))
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
            // No user text, credential, remote body or exception text enters JobBase's logs.
            // A conversation that fails to load is its own problem: never pause the key for it.
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
