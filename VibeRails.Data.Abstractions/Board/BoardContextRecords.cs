namespace VibeRails.Services.Board;

/// <summary>
/// One measurement of the context an agent receives when a terminal is launched on a card
/// (VB-63): the launch prompt plus what its first Board tool calls return. Tokens are the
/// characters-over-four estimate the host computes; <see cref="BreakdownJson"/> is the
/// per-part breakdown as the host serialised it, stored verbatim for later analysis.
/// </summary>
public sealed record BoardContextSampleRecord(
    string Id,
    string CardId,
    string? SessionId,
    DateTime MeasuredUtc,
    string Intent,
    string? Cli,
    string? Selection,
    int Tokens,
    int Chars,
    int PromptTokens,
    int CardReadTokens,
    string BreakdownJson);

/// <summary>What a launch hands the store to record; the store assigns id and time.</summary>
public sealed record NewBoardContextSample(
    string? SessionId,
    string Intent,
    string? Cli,
    string? Selection,
    int Tokens,
    int Chars,
    int PromptTokens,
    int CardReadTokens,
    string BreakdownJson);
