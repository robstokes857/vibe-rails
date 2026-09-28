namespace VibeRails.DTOs;

/// <summary>
/// One measured piece of the context an agent receives for a card (VB-63). <see cref="Items"/>
/// is a count where one applies (comments, notes, sessions…); <see cref="Note"/> is a short
/// qualifier for the UI ("tail shown", "if read").
/// </summary>
public sealed record BoardContextPartDto(string Key, string Label, int Chars, int Tokens, int? Items = null, string? Note = null);

/// <summary>The stored breakdown of a launch sample; serialised into <c>BoardContextSamples.Breakdown</c>.</summary>
public sealed record BoardContextBreakdown(
    List<BoardContextPartDto> Sources,
    List<BoardContextPartDto> Contents,
    List<BoardContextPartDto> Extras);

/// <summary>A recorded launch measurement, as the card editor shows it.</summary>
public sealed record BoardContextSampleDto(
    string Id,
    DateTime MeasuredAt,
    string Intent,
    string? Cli,
    string? SessionId,
    int Tokens,
    int Chars,
    int PromptTokens,
    int CardReadTokens);

/// <summary>
/// <c>GET /api/v1/board/cards/{card}/context</c>: the context an agent launched on this card
/// right now would receive. <see cref="Sources"/> are the pieces that add up to
/// <see cref="Tokens"/> (launch prompt, <c>get_board_card</c>, <c>list_board_columns</c>);
/// <see cref="Contents"/> are the same characters grouped by what they are (description,
/// comments, notes…); <see cref="Extras"/> is what is available but not sent unless asked for
/// (trimmed older activity, text attachments). <see cref="Method"/> names the token rule.
/// </summary>
public sealed record BoardContextEstimateResponse(
    string CardId,
    string Key,
    int Tokens,
    int Chars,
    string Method,
    DateTime MeasuredAt,
    List<BoardContextPartDto> Sources,
    List<BoardContextPartDto> Contents,
    List<BoardContextPartDto> Extras,
    BoardContextSampleDto? LastLaunch);
