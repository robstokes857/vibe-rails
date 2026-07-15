using System;
using System.Collections.Generic;

namespace MintLint;

internal static class BashTokenizer
{
    public static List<Token> Tokenize(string source)
    {
        List<Token> raw = TokenizeRaw(source);
        List<Token> tokens = [];
        Stack<string> keywordScopes = new();
        bool pendingCase = false;

        for (int index = 0; index < raw.Count; index++)
        {
            Token token = raw[index];
            switch (token.Text)
            {
                // Rewritten to `switch` so the engine scores the arms, not the header —
                // matching how Ruby's `case` and C#'s `switch` are counted.
                case "case":
                    tokens.Add(token with { Text = "switch" });
                    pendingCase = true;
                    break;

                case "in" when pendingCase:
                    tokens.Add(token);
                    tokens.Add(SyntheticBrace("{", token));
                    keywordScopes.Push("case");
                    pendingCase = false;
                    AddCaseArm(tokens, raw, index, token);
                    break;

                // `;;` closes an arm; whatever follows opens the next one unless the block
                // is over. The raw tokenizer emits `;;` as two `;` punctuation tokens.
                case ";" when IsArmTerminator(raw, index) && keywordScopes.Contains("case"):
                    tokens.Add(token);
                    tokens.Add(raw[index + 1]);
                    AddCaseArm(tokens, raw, index + 1, token);
                    index++;
                    break;

                case "then":
                    tokens.Add(SyntheticBrace("{", token));
                    keywordScopes.Push("if");
                    break;

                case "elif":
                    CloseScope(tokens, keywordScopes, "if", token);
                    tokens.Add(token);
                    break;

                case "else":
                    CloseScope(tokens, keywordScopes, "if", token);
                    tokens.Add(token);
                    tokens.Add(SyntheticBrace("{", token));
                    keywordScopes.Push("if");
                    break;

                case "fi":
                    CloseScope(tokens, keywordScopes, "if", token);
                    tokens.Add(token);
                    break;

                case "do":
                    tokens.Add(SyntheticBrace("{", token));
                    keywordScopes.Push("loop");
                    break;

                case "done":
                    CloseScope(tokens, keywordScopes, "loop", token);
                    tokens.Add(token);
                    break;

                case "esac":
                    CloseScope(tokens, keywordScopes, "case", token);
                    tokens.Add(token);
                    break;

                default:
                    tokens.Add(token);
                    break;
            }
        }

        while (keywordScopes.Count > 0)
        {
            keywordScopes.Pop();
            tokens.Add(new Token(TokenKind.Punctuation, "}", raw.Count > 0 ? raw[^1].Line : 1, source.Length, source.Length));
        }

        return tokens;
    }

    /// <summary>
    /// True when <paramref name="index"/> starts a `;;` arm terminator rather than a plain
    /// `;` statement separator. The raw tokenizer has no `;;` operator, so it arrives as two
    /// adjacent `;` punctuation tokens.
    /// </summary>
    private static bool IsArmTerminator(List<Token> raw, int index) =>
        index + 1 < raw.Count
        && raw[index + 1].Text == ";"
        && raw[index].End == raw[index + 1].Start;

    /// <summary>
    /// Emits a synthetic `case` for the arm starting after <paramref name="afterIndex"/>, so
    /// each pattern arm scores as a decision. Nothing is emitted when the block is over —
    /// the trailing `;;` before `esac` closes the last arm rather than opening a new one.
    /// </summary>
    private static void AddCaseArm(List<Token> tokens, List<Token> raw, int afterIndex, Token anchor)
    {
        int next = afterIndex + 1;
        if (next >= raw.Count || raw[next].Text == "esac")
        {
            return;
        }

        tokens.Add(new Token(TokenKind.Identifier, "case", anchor.Line, anchor.End, anchor.End));
    }

    private static List<Token> TokenizeRaw(string source)
    {
        List<Token> tokens = [];
        int index = 0;
        int line = 1;

        while (index < source.Length)
        {
            char current = source[index];
            if (current is '\r' or '\n')
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '#')
            {
                while (index < source.Length && source[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                tokens.Add(TokenizerUtilities.ReadString(source, ref index, ref line));
                continue;
            }

            if (current == '<' && index + 1 < source.Length && source[index + 1] == '<')
            {
                tokens.Add(ReadHereDocument(source, ref index, ref line));
                continue;
            }

            if (current == '$')
            {
                int start = index++;
                if (index < source.Length && source[index] == '{')
                {
                    index++;
                    while (index < source.Length && source[index] != '}')
                    {
                        index++;
                    }

                    if (index < source.Length)
                    {
                        index++;
                    }
                }
                else
                {
                    while (index < source.Length && (TokenizerUtilities.IsIdentifierPart(source[index]) || char.IsDigit(source[index])))
                    {
                        index++;
                    }
                }

                tokens.Add(new Token(TokenKind.Identifier, source[start..index], line, start, index));
                continue;
            }

            if (TokenizerUtilities.IsIdentifierStart(current))
            {
                int start = index++;
                while (index < source.Length &&
                    (TokenizerUtilities.IsIdentifierPart(source[index]) ||
                     (source[index] == '-' && index + 1 < source.Length && TokenizerUtilities.IsIdentifierPart(source[index + 1]))))
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Identifier, source[start..index], line, start, index));
                continue;
            }

            if (char.IsDigit(current))
            {
                int start = index++;
                while (index < source.Length && TokenizerUtilities.IsNumberPart(source[index]))
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Number, source[start..index], line, start, index));
                continue;
            }

            string? op = TokenizerUtilities.MatchOperator(source, index, TokenizerUtilities.ThreeCharOperators)
                ?? TokenizerUtilities.MatchOperator(source, index, TokenizerUtilities.TwoCharOperators);
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

    private static Token ReadHereDocument(string source, ref int index, ref int line)
    {
        int start = index;
        int startLine = line;
        index += 2;
        bool stripTabs = index < source.Length && source[index] == '-';
        if (stripTabs)
        {
            index++;
        }

        while (index < source.Length && source[index] is ' ' or '\t')
        {
            index++;
        }

        char quote = index < source.Length && source[index] is '\'' or '"' ? source[index++] : '\0';
        int delimiterStart = index;
        while (index < source.Length && !char.IsWhiteSpace(source[index]) && source[index] != ';' &&
            (quote == '\0' || source[index] != quote))
        {
            index++;
        }

        string delimiter = source[delimiterStart..index];
        if (quote != '\0' && index < source.Length && source[index] == quote)
        {
            index++;
        }

        while (index < source.Length && source[index] is not '\r' and not '\n')
        {
            index++;
        }

        if (index < source.Length)
        {
            TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
        }

        while (index < source.Length)
        {
            int contentStart = index;
            if (stripTabs)
            {
                while (contentStart < source.Length && source[contentStart] == '\t')
                {
                    contentStart++;
                }
            }

            int contentEnd = contentStart;
            while (contentEnd < source.Length && source[contentEnd] is not '\r' and not '\n')
            {
                contentEnd++;
            }

            if (delimiter.Length > 0 && source.AsSpan(contentStart, contentEnd - contentStart).SequenceEqual(delimiter.AsSpan()))
            {
                index = contentEnd;
                return new Token(TokenKind.String, source[start..index], startLine, start, index);
            }

            index = contentEnd;
            if (index < source.Length)
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
            }
        }

        return new Token(TokenKind.String, source[start..index], startLine, start, index);
    }

    private static Token SyntheticBrace(string text, Token anchor)
    {
        return new Token(TokenKind.Punctuation, text, anchor.Line, anchor.Start, anchor.Start);
    }

    private static void CloseScope(List<Token> tokens, Stack<string> scopes, string expected, Token anchor)
    {
        if (scopes.Count > 0 && scopes.Peek() == expected)
        {
            scopes.Pop();
            tokens.Add(SyntheticBrace("}", anchor));
        }
    }
}
