using System.Text.Json;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

/// <summary>
/// Measures the context a card puts in front of an agent (VB-63): the launch prompt plus what
/// the first Board tool calls return (<c>get_board_card</c>, <c>list_board_columns</c>).
/// </summary>
public interface IBoardContextEstimator
{
    /// <summary>What an agent launched on the card right now would receive; null when the card is not in the project.</summary>
    Task<BoardContextEstimateResponse?> EstimateAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);

    /// <summary>Measures the prompt a launch composed plus the card reads, and records the sample on the card.</summary>
    Task<BoardContextSampleRecord?> RecordLaunchAsync(string projectPath, BoardCardRecord card, BoardLaunchPrompt prompt, string intent,
        string? sessionId, string? cli, string? selection, CancellationToken cancellationToken = default);
}

/// <summary>
/// Root-backend only (it resolves the assignee's environment through the state repository). The
/// numbers are produced by rendering exactly what the agent would read, through the same code the
/// launch and the MCP tools use, and counting with <see cref="ContextTokenEstimator"/>. Nothing here
/// changes what agents receive; it only reports it, so the plan in
/// <c>vibe-books/vibe_board_context_rot</c> has data to work from.
/// </summary>
public sealed class BoardContextEstimator(
    IBoardService service,
    IBoardStore store,
    IRepository repository) : IBoardContextEstimator
{
    public async Task<BoardContextEstimateResponse?> EstimateAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null)
            return null;
        var assignee = await ResolveAssigneeAsync(projectPath, card.Assignee, cancellationToken);
        var prompt = await BoardLaunchService.ComposePromptAsync(store, projectPath, card, assignee.Label, assignee.EnvironmentPrompt, "work", cancellationToken);
        var measurement = await MeasureAsync(projectPath, card, prompt, cancellationToken);
        var last = await store.GetLatestContextSampleAsync(projectPath, card.Id, cancellationToken);
        return new BoardContextEstimateResponse(card.Id, card.Key, measurement.Tokens, measurement.Chars, ContextTokenEstimator.Method, DateTime.UtcNow,
            measurement.Sources, measurement.Contents, measurement.Extras, last is null ? null : ToDto(last));
    }

    public async Task<BoardContextSampleRecord?> RecordLaunchAsync(string projectPath, BoardCardRecord card, BoardLaunchPrompt prompt, string intent,
        string? sessionId, string? cli, string? selection, CancellationToken cancellationToken = default)
    {
        var measurement = await MeasureAsync(projectPath, card, prompt, cancellationToken);
        var breakdown = JsonSerializer.Serialize(new BoardContextBreakdown(measurement.Sources, measurement.Contents, measurement.Extras),
            AppJsonSerializerContext.Default.BoardContextBreakdown);
        var sample = new NewBoardContextSample(sessionId, intent, cli, selection, measurement.Tokens, measurement.Chars,
            measurement.PromptTokens, measurement.CardReadTokens, breakdown);
        var recorded = await store.RecordContextSampleAsync(projectPath, card.Id, sample, cancellationToken);
        if (recorded is not null)
            Log.Information("[Board] {Card} launch context ≈ {Tokens} tokens (prompt {Prompt}, card read {CardRead}) for session {SessionId}",
                card.Key, recorded.Tokens, recorded.PromptTokens, recorded.CardReadTokens, sessionId ?? "-");
        return recorded;
    }

    private sealed record Measurement(int Tokens, int Chars, int PromptTokens, int CardReadTokens,
        List<BoardContextPartDto> Sources, List<BoardContextPartDto> Contents, List<BoardContextPartDto> Extras);

    private async Task<Measurement> MeasureAsync(string projectPath, BoardCardRecord card, BoardLaunchPrompt prompt, CancellationToken cancellationToken)
    {
        var detail = await service.GetCardAsync(projectPath, card.Id, cancellationToken)
            ?? throw new BoardValidationException("The card was deleted while its context was being measured.");
        var render = await BoardTool.RenderCardAsync(service, store, projectPath, detail, BoardTool.CardReadOptions.Default, cancellationToken);
        var lanes = await BoardTool.RenderLanesAsync(service, store, projectPath, prompt.BoardId, prompt.BoardName, cancellationToken);
        var promptParts = BoardPromptComposer.Measure(prompt.Prompt, card, prompt.EnvironmentPrompt, prompt.BoardContext, prompt.Intent);
        var stats = render.Stats;

        var sources = new List<BoardContextPartDto>
        {
            Part("prompt", "Launch prompt", prompt.Prompt.Length),
            Part("cardRead", "get_board_card", render.Text.Length),
            Part("lanes", "list_board_columns", lanes.Length),
        };
        var contents = new List<BoardContextPartDto>
        {
            Part("description", "Description", promptParts.DescriptionChars + stats.DescriptionChars, null,
                prompt.Intent == "chat" ? "retrieved through MCP; long descriptions are paged"
                    : "bounded excerpts in the prompt and card read; descriptionOffset retrieves more"),
            Part("previousWork", "Previous work and file references", stats.PreviousWorkChars),
            Part("comments", "Comments", stats.Comments.Chars, detail.Comments.Count, TrimNote(stats.Comments)),
            Part("sessions", "Sessions", stats.SessionsChars, detail.Sessions.Count, stats.SessionsOmitted > 0 ? $"newest {stats.SessionsListed} listed" : null),
            Part("commits", "Linked commits", stats.CommitsChars, detail.Commits.Count, stats.CommitsOmitted > 0 ? $"newest {stats.CommitsListed} listed" : null),
        };
        if (detail.LinkedCards.Count > 0)
            contents.Add(Part("linkedCards", "Linked cards", stats.LinkedCardsChars, detail.LinkedCards.Count));
        if (detail.Attachments.Count > 0)
            contents.Add(Part("attachments", "Attachment names", stats.AttachmentsChars, detail.Attachments.Count));
        if (promptParts.BoardContextChars > 0)
            contents.Add(Part("boardContext", "Board context", promptParts.BoardContextChars));
        if (promptParts.EnvironmentPromptChars > 0)
            contents.Add(Part("environmentPrompt", "Environment initial message", promptParts.EnvironmentPromptChars));
        contents.Add(Part("guidance", "Card fields, lane list and tool guidance", promptParts.GuidanceChars + stats.OtherChars + lanes.Length));

        var extras = new List<BoardContextPartDto>();
        var trimmed = stats.Comments.TrimmedChars + stats.Notes.TrimmedChars;
        if (trimmed > 0)
            extras.Add(Part("trimmedActivity", "Older activity not sent in full", trimmed,
                stats.Comments.Previewed + stats.Comments.Omitted + stats.Notes.Previewed + stats.Notes.Omitted,
                "previews only; the agent pages back with before= or reads all with activity=all"));
        var textAttachments = detail.Attachments.Where(a => a.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (textAttachments.Count > 0)
            extras.Add(Part("textAttachments", "Markdown/TXT attachments", (int)Math.Min(int.MaxValue, textAttachments.Sum(a => a.Bytes)),
                textAttachments.Count, "only if the agent reads them"));

        var chars = sources.Sum(part => part.Chars);
        return new Measurement(ContextTokenEstimator.FromCharacters(chars), chars, sources[0].Tokens, sources[1].Tokens, sources, contents, extras);
    }

    private static string? TrimNote(BoardTool.ActivityRenderStats stats) =>
        stats.Previewed + stats.Omitted > 0 ? $"{stats.Full} in full, {stats.Previewed + stats.Omitted} previewed or omitted" : null;

    private static BoardContextPartDto Part(string key, string label, int chars, int? items = null, string? note = null) =>
        new(key, label, chars, ContextTokenEstimator.FromCharacters(chars), items, note);

    private static BoardContextSampleDto ToDto(BoardContextSampleRecord sample) =>
        new(sample.Id, sample.MeasuredUtc, sample.Intent, sample.Cli, sample.SessionId, sample.Tokens, sample.Chars, sample.PromptTokens, sample.CardReadTokens);

    private sealed record ResolvedAssignee(string? Label, string? EnvironmentPrompt);

    /// <summary>
    /// The assignee as a launch would see it, tolerant where a launch throws: an unassigned card
    /// or a vanished environment still gets a measurement, just without the environment's message.
    /// </summary>
    private async Task<ResolvedAssignee> ResolveAssigneeAsync(string projectPath, string? assignee, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assignee) || !BoardSelection.TryParse(assignee, out var parsed) || parsed is null)
            return new ResolvedAssignee(null, null);
        if (!parsed.IsEnvironment)
            return new ResolvedAssignee(parsed.Cli, null);
        var environment = await repository.GetEnvironmentByIdAsync(parsed.EnvironmentId!.Value, cancellationToken);
        if (environment is null || environment.LLM != parsed.Llm || !ProjectPathComparer.IsVisibleIn(environment.ProjectPath, projectPath))
            return new ResolvedAssignee(parsed.Cli, null);
        return new ResolvedAssignee($"{environment.CustomName} ({parsed.Cli})", environment.CustomPrompt);
    }
}
