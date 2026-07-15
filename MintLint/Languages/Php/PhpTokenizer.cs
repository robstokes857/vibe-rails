using System;
using System.Collections.Generic;

namespace MintLint;

internal static class PhpTokenizer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "and", "as", "break", "case", "catch", "class", "clone", "const",
        "continue", "declare", "default", "do", "echo", "else", "elseif", "empty",
        "enddeclare", "endfor", "endforeach", "endif", "endswitch", "endwhile", "enum",
        "extends", "final", "finally", "fn", "for", "foreach", "function", "global",
        "goto", "if", "implements", "include", "include_once", "instanceof", "interface",
        "isset", "list", "match", "namespace", "new", "or", "print", "private",
        "protected", "public", "readonly", "require", "require_once", "return", "static",
        "switch", "throw", "trait", "try", "unset", "use", "var", "while", "xor", "yield"
    };

    public static List<Token> Tokenize(string source)
    {
        List<Token> tokens = [];
        bool hasOpenTag = source.Contains("<?", StringComparison.Ordinal);
        bool inPhp = !hasOpenTag;
        int index = 0;
        int line = 1;

        while (index < source.Length)
        {
            if (!inPhp)
            {
                if (index + 1 < source.Length && source[index] == '<' && source[index + 1] == '?')
                {
                    inPhp = true;
                    index += 2;
                    if (index + 2 < source.Length &&
                        string.Compare(source, index, "php", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        index += 3;
                    }
                    else if (index < source.Length && source[index] == '=')
                    {
                        index++;
                    }

                    continue;
                }

                if (source[index] is '\r' or '\n')
                {
                    TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (index + 1 < source.Length && source[index] == '?' && source[index + 1] == '>')
            {
                inPhp = false;
                index += 2;
                continue;
            }

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

            if (current == '/' && index + 1 < source.Length)
            {
                if (source[index + 1] == '/')
                {
                    SkipLineComment(source, ref index);
                    continue;
                }

                if (source[index + 1] == '*')
                {
                    SkipBlockComment(source, ref index, ref line);
                    continue;
                }
            }

            // PHP 8 attributes start with `#[`; every other `#` starts a line comment.
            if (current == '#' && (index + 1 >= source.Length || source[index + 1] != '['))
            {
                SkipLineComment(source, ref index);
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                tokens.Add(TokenizerUtilities.ReadString(source, ref index, ref line));
                continue;
            }

            if (current == '<' && index + 2 < source.Length && source[index + 1] == '<' && source[index + 2] == '<')
            {
                tokens.Add(ReadHeredoc(source, ref index, ref line));
                continue;
            }

            if (current == '$' && index + 1 < source.Length && TokenizerUtilities.IsIdentifierStart(source[index + 1]))
            {
                int start = index++;
                int nameStart = index;
                index++;
                while (index < source.Length && TokenizerUtilities.IsIdentifierPart(source[index]))
                {
                    index++;
                }

                string name = source[nameStart..index];
                string text = string.Equals(name, "this", StringComparison.OrdinalIgnoreCase) ? "this" : "$" + name;
                tokens.Add(new Token(TokenKind.Identifier, text, line, start, index));
                continue;
            }

            if (TokenizerUtilities.IsIdentifierStart(current))
            {
                int start = index++;
                while (index < source.Length && TokenizerUtilities.IsIdentifierPart(source[index]))
                {
                    index++;
                }

                string text = source[start..index];
                if (Keywords.Contains(text))
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

    private static void SkipLineComment(string source, ref int index)
    {
        while (index < source.Length && source[index] is not '\r' and not '\n')
        {
            index++;
        }
    }

    private static void SkipBlockComment(string source, ref int index, ref int line)
    {
        index += 2;
        while (index < source.Length)
        {
            if (index + 1 < source.Length && source[index] == '*' && source[index + 1] == '/')
            {
                index += 2;
                return;
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
    }

    private static Token ReadHeredoc(string source, ref int index, ref int line)
    {
        int start = index;
        int startLine = line;
        index += 3;
        while (index < source.Length && source[index] is ' ' or '\t')
        {
            index++;
        }

        char quote = index < source.Length && source[index] is '\'' or '"' ? source[index++] : '\0';
        int labelStart = index;
        while (index < source.Length && TokenizerUtilities.IsIdentifierPart(source[index]))
        {
            index++;
        }

        string label = source[labelStart..index];
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
            int lineStart = index;
            while (lineStart < source.Length && source[lineStart] is ' ' or '\t')
            {
                lineStart++;
            }

            if (label.Length > 0 && lineStart + label.Length <= source.Length &&
                string.CompareOrdinal(source, lineStart, label, 0, label.Length) == 0)
            {
                index = lineStart + label.Length;
                if (index < source.Length && source[index] == ';')
                {
                    index++;
                }

                return new Token(TokenKind.String, source[start..index], startLine, start, index);
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
