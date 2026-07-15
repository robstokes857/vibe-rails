using System;
using System.Collections.Generic;

namespace MintLint;

internal static class RubyTokenizer
{
    private static readonly HashSet<string> LineBlockOpeners = new(StringComparer.Ordinal)
    {
        "begin", "case", "class", "def", "for", "if", "module", "unless", "until", "while"
    };

    public static List<Token> Tokenize(string source)
    {
        List<Token> raw = TokenizeRaw(source);
        List<Token> tokens = [];
        int openScopes = 0;

        int index = 0;
        while (index < raw.Count)
        {
            int line = raw[index].Line;
            int lineEnd = index;
            while (lineEnd + 1 < raw.Count && raw[lineEnd].Text != ";" && raw[lineEnd + 1].Line == line)
            {
                lineEnd++;
            }

            Token first = raw[index];
            if (first.Text == "end" && openScopes > 0)
            {
                tokens.Add(new Token(TokenKind.Punctuation, "}", line, first.Start, first.Start));
                openScopes--;
            }

            // `def name = expr` and `def name(args) = expr` are endless methods: they carry
            // their whole body on the declaration line and have no `end`. Opening a scope for
            // them would leave it unbalanced and swallow an enclosing block's `end`, so the
            // body is braced inline and the scope never opens.
            int endlessBody = FindEndlessDefinitionBody(raw, index, lineEnd);
            for (int i = index; i <= lineEnd; i++)
            {
                tokens.Add(raw[i].Text == "case" ? raw[i] with { Text = "switch" } : raw[i]);
                if (i == endlessBody)
                {
                    tokens.Add(new Token(TokenKind.Punctuation, "{", line, raw[i].End, raw[i].End));
                }
            }

            if (endlessBody >= 0)
            {
                Token last = raw[lineEnd];
                tokens.Add(new Token(TokenKind.Punctuation, "}", line, last.End, last.End));
                index = lineEnd + 1;
                continue;
            }

            bool opens = first.Text != "end" && LineBlockOpeners.Contains(first.Text);
            if (!opens)
            {
                for (int i = index; i <= lineEnd; i++)
                {
                    if (raw[i].Text == "do")
                    {
                        opens = true;
                        break;
                    }
                }
            }

            if (opens)
            {
                Token last = raw[lineEnd];
                tokens.Add(new Token(TokenKind.Punctuation, "{", line, last.End, last.End));
                openScopes++;
            }

            index = lineEnd + 1;
        }

        int finalLine = raw.Count > 0 ? raw[^1].Line : 1;
        while (openScopes-- > 0)
        {
            tokens.Add(new Token(TokenKind.Punctuation, "}", finalLine, source.Length, source.Length));
        }

        return tokens;
    }

    /// <summary>
    /// Returns the index of the `=` that introduces an endless method body, or -1 when the
    /// line is not an endless definition. The `=` must sit immediately after the method name
    /// or its parameter list: `def one = 1` and `def square(x) = x * x` are endless, while
    /// `def render title, layout = nil` is an ordinary parenless def with a default value.
    /// </summary>
    private static int FindEndlessDefinitionBody(List<Token> raw, int index, int lineEnd)
    {
        if (raw[index].Text != "def")
        {
            return -1;
        }

        int cursor = index + 1;
        if (cursor > lineEnd)
        {
            return -1;
        }

        // Skip a `self.` / `self::` receiver.
        if (raw[cursor].Text == "self" && cursor + 1 <= lineEnd && raw[cursor + 1].Text is "." or "::")
        {
            cursor += 2;
        }

        int nameIndex = cursor++;
        if (cursor > lineEnd)
        {
            return -1;
        }

        // `def name=(value)` is a setter: the `=` is glued to the name and forms part of it,
        // and the method still needs an `end`. An endless body always separates the `=`
        // (`def name = expr`), so adjacency is what tells the two apart.
        if (raw[cursor].Text == "=" && raw[nameIndex].End == raw[cursor].Start)
        {
            return -1;
        }

        // Skip a parenthesised parameter list.
        if (raw[cursor].Text == "(")
        {
            int depth = 0;
            while (cursor <= lineEnd)
            {
                if (raw[cursor].Text == "(")
                {
                    depth++;
                }
                else if (raw[cursor].Text == ")" && --depth == 0)
                {
                    cursor++;
                    break;
                }

                cursor++;
            }

            if (depth != 0 || cursor > lineEnd)
            {
                return -1;
            }
        }

        return cursor <= lineEnd && raw[cursor].Text == "=" ? cursor : -1;
    }

    private static List<Token> TokenizeRaw(string source)
    {
        List<Token> tokens = [];
        int index = 0;
        int line = 1;
        bool atLineStart = true;

        while (index < source.Length)
        {
            if (atLineStart)
            {
                int cursor = index;
                while (cursor < source.Length && source[cursor] is ' ' or '\t')
                {
                    cursor++;
                }

                if (cursor + 6 <= source.Length && source.AsSpan(cursor).StartsWith("=begin".AsSpan(), StringComparison.Ordinal) &&
                    (cursor + 6 == source.Length || char.IsWhiteSpace(source[cursor + 6])))
                {
                    index = SkipEmbeddedDocument(source, cursor, ref line);
                    atLineStart = true;
                    continue;
                }

                index = cursor;
                atLineStart = false;
                if (index >= source.Length)
                {
                    break;
                }
            }

            char current = source[index];
            if (current is '\r' or '\n')
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
                atLineStart = true;
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

            if (current == '@' && index + 1 < source.Length)
            {
                int start = index++;
                if (source[index] == '@')
                {
                    index++;
                }

                if (index < source.Length && TokenizerUtilities.IsIdentifierStart(source[index]))
                {
                    index++;
                    while (index < source.Length && TokenizerUtilities.IsIdentifierPart(source[index]))
                    {
                        index++;
                    }

                    tokens.Add(new Token(TokenKind.Identifier, source[start..index], line, start, index));
                    continue;
                }

                tokens.Add(new Token(TokenKind.Operator, "@", line, start, index));
                continue;
            }

            if (TokenizerUtilities.IsIdentifierStart(current))
            {
                int start = index++;
                while (index < source.Length && TokenizerUtilities.IsIdentifierPart(source[index]))
                {
                    index++;
                }

                if (index < source.Length && source[index] is '?' or '!')
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

    private static int SkipEmbeddedDocument(string source, int index, ref int line)
    {
        while (index < source.Length)
        {
            int lineStart = index;
            while (lineStart < source.Length && source[lineStart] is ' ' or '\t')
            {
                lineStart++;
            }

            bool isEnd = lineStart + 4 <= source.Length &&
                source.AsSpan(lineStart).StartsWith("=end".AsSpan(), StringComparison.Ordinal) &&
                (lineStart + 4 == source.Length || char.IsWhiteSpace(source[lineStart + 4]));

            while (index < source.Length && source[index] is not '\r' and not '\n')
            {
                index++;
            }

            if (index < source.Length)
            {
                TokenizerUtilities.ConsumeNewLine(source, ref index, ref line);
            }

            if (isEnd)
            {
                break;
            }
        }

        return index;
    }
}
