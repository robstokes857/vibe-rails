using System;
using System.Collections.Generic;

namespace MintLint;

internal static class PowerShellTokenizer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "continue", "data", "do", "dynamicparam",
        "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach",
        "from", "function", "if", "in", "param", "process", "return", "static", "switch",
        "throw", "trap", "try", "until", "using", "while", "workflow"
    };

    public static List<Token> Tokenize(string source)
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

            if (current == '<' && index + 1 < source.Length && source[index + 1] == '#')
            {
                index += 2;
                while (index < source.Length)
                {
                    if (index + 1 < source.Length && source[index] == '#' && source[index + 1] == '>')
                    {
                        index += 2;
                        break;
                    }

                    if (source[index] is '\r' or '\n')
                    {
                        TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                    }
                    else
                    {
                        index++;
                    }
                }

                continue;
            }

            if (current == '@' && index + 1 < source.Length && source[index + 1] is '\'' or '"')
            {
                tokens.Add(ReadHereString(source, ref index, ref line));
                continue;
            }

            if (current is '\'' or '"')
            {
                tokens.Add(ReadString(source, ref index, ref line));
                continue;
            }

            if (current == '$')
            {
                tokens.Add(ReadVariable(source, ref index, line));
                continue;
            }

            if (current == '-' && index + 1 < source.Length && char.IsLetter(source[index + 1]))
            {
                int start = index++;
                while (index < source.Length && (char.IsLetter(source[index]) || source[index] == '-'))
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Operator, source[start..index].ToLowerInvariant(), line, start, index));
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

                string text = source[start..index];
                if (Keywords.Contains(text) || string.Equals(text, "Import-Module", StringComparison.OrdinalIgnoreCase))
                {
                    text = text.ToLowerInvariant();
                }

                tokens.Add(new Token(TokenKind.Identifier, text, line, start, index));
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

    private static Token ReadVariable(string source, ref int index, int line)
    {
        int start = index++;
        if (index < source.Length && source[index] == '{')
        {
            int contentStart = ++index;
            while (index < source.Length && source[index] != '}')
            {
                index++;
            }

            string braced = source[contentStart..index];
            if (index < source.Length)
            {
                index++;
            }

            return new Token(TokenKind.Identifier, NormalizeVariable(braced), line, start, index);
        }

        int nameStart = index;
        while (index < source.Length && (TokenizerUtilities.IsIdentifierPart(source[index]) || source[index] == ':'))
        {
            index++;
        }

        string name = source[nameStart..index];
        return new Token(TokenKind.Identifier, NormalizeVariable(name), line, start, index);
    }

    private static string NormalizeVariable(string name)
    {
        int colon = name.LastIndexOf(':');
        string unscoped = colon >= 0 ? name[(colon + 1)..] : name;

        // `$script:count` and `$count` name the same variable, so the scope is dropped — but
        // `env:` is not a scope of user variables, it is the process environment. Keeping it
        // is what lets the testability analyzer see `$env:HOME` as an ambient dependency.
        if (colon >= 0 && name.AsSpan(0, colon).Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            return "$env:" + unscoped;
        }

        return string.Equals(unscoped, "this", StringComparison.OrdinalIgnoreCase) ? "this" : "$" + unscoped;
    }

    private static Token ReadString(string source, ref int index, ref int line)
    {
        int start = index;
        int startLine = line;
        char quote = source[index++];
        while (index < source.Length)
        {
            char current = source[index];
            if (current is '\r' or '\n')
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                continue;
            }

            if (quote == '\'' && current == '\'' && index + 1 < source.Length && source[index + 1] == '\'')
            {
                index += 2;
                continue;
            }

            if (current == '`' && quote == '"')
            {
                index = Math.Min(source.Length, index + 2);
                continue;
            }

            if (current == quote)
            {
                index++;
                break;
            }

            index++;
        }

        return new Token(TokenKind.String, source[start..index], startLine, start, index);
    }

    private static Token ReadHereString(string source, ref int index, ref int line)
    {
        int start = index;
        int startLine = line;
        char quote = source[index + 1];
        index += 2;
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
            int lineStart = index;
            if (lineStart + 1 < source.Length && source[lineStart] == quote && source[lineStart + 1] == '@')
            {
                index += 2;
                break;
            }

            while (index < source.Length && source[index] is not '\r' and not '\n')
            {
                index++;
            }

            if (index < source.Length)
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
            }
        }

        return new Token(TokenKind.String, source[start..index], startLine, start, index);
    }
}
