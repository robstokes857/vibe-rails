using System.Collections.Generic;

namespace MintLint;

internal static class PythonTokenizer
{
    // Python has no braces, so block structure is recovered from indentation: an
    // INDENT emits a synthetic `{` and a DEDENT emits a synthetic `}`. Coupling the
    // braces to the indent stack keeps them balanced regardless of how the source is
    // formatted. The scan is a single global pass (not line-by-line) so triple-quoted
    // strings, implicit line continuation inside brackets, and backslash continuation
    // are all handled correctly.
    public static List<Token> Tokenize(string source)
    {
        List<Token> tokens = [];
        Stack<int> indents = new();
        indents.Push(0);

        int index = 0;
        int line = 1;
        int bracketDepth = 0;
        bool atLineStart = true;

        while (index < source.Length)
        {
            if (atLineStart && bracketDepth == 0)
            {
                int indent = 0;
                int cursor = index;
                while (cursor < source.Length && (source[cursor] == ' ' || source[cursor] == '\t'))
                {
                    indent += source[cursor] == '\t' ? 4 : 1;
                    cursor++;
                }

                // Blank and comment-only lines never affect indentation.
                if (cursor >= source.Length || source[cursor] is '\n' or '\r' or '#')
                {
                    index = cursor;
                    if (index < source.Length && source[index] == '#')
                    {
                        while (index < source.Length && source[index] is not '\n' and not '\r')
                        {
                            index++;
                        }
                    }

                    if (index < source.Length)
                    {
                        TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                    }

                    continue;
                }

                while (indent < indents.Peek() && indents.Count > 1)
                {
                    indents.Pop();
                    tokens.Add(new Token(TokenKind.Punctuation, "}", line, cursor, cursor));
                }

                if (indent > indents.Peek())
                {
                    indents.Push(indent);
                    tokens.Add(new Token(TokenKind.Punctuation, "{", line, cursor, cursor));
                }

                index = cursor;
                atLineStart = false;
                continue;
            }

            char current = source[index];

            if (current is '\n' or '\r')
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                if (bracketDepth == 0)
                {
                    atLineStart = true;
                }

                continue;
            }

            if (current == ' ' || current == '\t')
            {
                index++;
                continue;
            }

            if (current == '#')
            {
                while (index < source.Length && source[index] is not '\n' and not '\r')
                {
                    index++;
                }

                continue;
            }

            // Backslash before a line break is an explicit line continuation.
            if (current == '\\' && index + 1 < source.Length && source[index + 1] is '\n' or '\r')
            {
                index++;
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                continue;
            }

            if (current == '\'' || current == '"' || IsStringPrefix(source, index))
            {
                tokens.Add(ReadString(source, ref index, ref line));
                continue;
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

            if (current is '(' or '[' or '{')
            {
                bracketDepth++;
            }
            else if (bracketDepth > 0 && current is ')' or ']' or '}')
            {
                bracketDepth--;
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

        while (indents.Count > 1)
        {
            indents.Pop();
            tokens.Add(new Token(TokenKind.Punctuation, "}", line, source.Length, source.Length));
        }

        return tokens;
    }

    private static bool IsStringPrefix(string source, int index)
    {
        // String prefixes such as r"", b"", f"", u"", rb"", fr"" (one or two letters
        // immediately followed by a quote, with no intervening whitespace).
        int cursor = index;
        int letters = 0;
        while (cursor < source.Length && letters < 2 && IsStringPrefixLetter(source[cursor]))
        {
            cursor++;
            letters++;
        }

        return letters > 0 && cursor < source.Length && (source[cursor] == '\'' || source[cursor] == '"');
    }

    private static bool IsStringPrefixLetter(char value)
    {
        char lower = char.ToLowerInvariant(value);
        return lower is 'r' or 'b' or 'f' or 'u';
    }

    private static Token ReadString(string source, ref int index, ref int line)
    {
        int start = index;
        int startLine = line;

        while (index < source.Length && source[index] != '\'' && source[index] != '"')
        {
            index++;
        }

        if (index >= source.Length)
        {
            return new Token(TokenKind.String, source[start..index], startLine, start, index);
        }

        char quote = source[index];
        bool triple = index + 2 < source.Length && source[index + 1] == quote && source[index + 2] == quote;
        index += triple ? 3 : 1;

        bool escaped = false;
        while (index < source.Length)
        {
            char current = source[index];

            if (current == '\\')
            {
                escaped = !escaped;
                index++;
                continue;
            }

            if (current is '\n' or '\r')
            {
                if (!triple && !escaped)
                {
                    // A single-quoted string cannot span a newline.
                    break;
                }

                escaped = false;
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                continue;
            }

            if (!escaped && current == quote)
            {
                if (!triple)
                {
                    index++;
                    return new Token(TokenKind.String, source[start..index], startLine, start, index);
                }

                if (index + 2 < source.Length && source[index + 1] == quote && source[index + 2] == quote)
                {
                    index += 3;
                    return new Token(TokenKind.String, source[start..index], startLine, start, index);
                }

                index++;
                continue;
            }

            escaped = false;
            index++;
        }

        return new Token(TokenKind.String, source[start..index], startLine, start, index);
    }
}
