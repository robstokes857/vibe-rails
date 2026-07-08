using System.Collections.Generic;

namespace MintLint;

internal static class BraceLanguageTokenizer
{
    public static List<Token> Tokenize(string source)
    {
        List<Token> tokens = [];
        int index = 0;
        int line = 1;

        while (index < source.Length)
        {
            char current = source[index];

            if (current == '\r' || current == '\n')
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '/' && index + 1 < source.Length)
            {
                char next = source[index + 1];
                if (next == '/')
                {
                    index += 2;
                    while (index < source.Length && source[index] != '\r' && source[index] != '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (next == '*')
                {
                    index += 2;
                    while (index + 1 < source.Length)
                    {
                        if (source[index] == '\r' || source[index] == '\n')
                        {
                            TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                            continue;
                        }

                        if (source[index] == '*' && source[index + 1] == '/')
                        {
                            index += 2;
                            break;
                        }

                        index++;
                    }

                    continue;
                }
            }

            if (TokenizerUtilities.IsIdentifierStart(current))
            {
                int start = index;
                index++;
                while (index < source.Length && TokenizerUtilities.IsIdentifierPart(source[index]))
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Identifier, source[start..index], line, start, index));
                continue;
            }

            if (char.IsDigit(current))
            {
                int start = index;
                index++;
                while (index < source.Length && TokenizerUtilities.IsNumberPart(source[index]))
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Number, source[start..index], line, start, index));
                continue;
            }

            if (current == '\'' || current == '"' || current == '`')
            {
                tokens.Add(TokenizerUtilities.ReadString(source, ref index, ref line));
                continue;
            }

            string? op = TokenizerUtilities.MatchOperator(source, index, TokenizerUtilities.ThreeCharOperators);
            if (op is not null)
            {
                tokens.Add(new Token(TokenKind.Operator, op, line, index, index + op.Length));
                index += op.Length;
                continue;
            }

            op = TokenizerUtilities.MatchOperator(source, index, TokenizerUtilities.TwoCharOperators);
            if (op is not null)
            {
                tokens.Add(new Token(TokenKind.Operator, op, line, index, index + op.Length));
                index += op.Length;
                continue;
            }

            TokenKind kind = TokenizerUtilities.IsPunctuation(current) ? TokenKind.Punctuation : TokenKind.Operator;
            tokens.Add(new Token(kind, current.ToString(), line, index, index + 1));
            index++;
        }

        return tokens;
    }
}
