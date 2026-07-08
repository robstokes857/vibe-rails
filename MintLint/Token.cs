namespace MintLint;

internal enum TokenKind
{
    Identifier,
    Number,
    String,
    Operator,
    Punctuation
}

internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Start, int End)
{
    public bool IsIdentifierLike => Kind == TokenKind.Identifier;
}
