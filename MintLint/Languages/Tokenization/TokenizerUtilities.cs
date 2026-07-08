using System;

namespace MintLint;

internal static class TokenizerUtilities
{
    public static readonly string[] ThreeCharOperators =
    [
        "===", "!==", ">>>", "**=", "<<=", ">>=", "&&=", "||=", "??=", "..."
    ];

    public static readonly string[] TwoCharOperators =
    [
        "=>", "->", "::", "&&", "||", "??", "?.", "==", "!=", "<=", ">=", "++", "--",
        "+=", "-=", "*=", "/=", "%=", "**", "<<", ">>", "&=", "|=", "^=", ":="
    ];

    public static Token ReadString(string source, ref int index, ref int line)
    {
        // The token text keeps the surrounding quotes (matching the Python tokenizer). This
        // is what stops a literal such as "{" or "class" from later being mistaken for a
        // structural token or keyword, since the quoted lexeme can never equal a bare symbol.
        char quote = source[index];
        int start = index;
        int startLine = line;
        index++;
        bool escaped = false;

        while (index < source.Length)
        {
            char current = source[index];
            if (current == '\r' || current == '\n')
            {
                ConsumeNewLine(source, ref index, ref line);
                escaped = false;
                continue;
            }

            if (escaped)
            {
                escaped = false;
                index++;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                index++;
                continue;
            }

            if (current == quote)
            {
                index++;
                return new Token(TokenKind.String, source[start..index], startLine, start, index);
            }

            index++;
        }

        return new Token(TokenKind.String, source[start..index], startLine, start, index);
    }

    public static void ConsumeNewLine(string source, ref int index, ref int line)
    {
        if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
        {
            index += 2;
        }
        else
        {
            index++;
        }

        line++;
    }

    public static string? MatchOperator(string source, int index, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (index + candidate.Length <= source.Length &&
                string.CompareOrdinal(source, index, candidate, 0, candidate.Length) == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsIdentifierStart(char value)
    {
        return value == '_' || value == '$' || char.IsLetter(value);
    }

    public static bool IsIdentifierPart(char value)
    {
        return value == '_' || value == '$' || char.IsLetterOrDigit(value);
    }

    public static bool IsNumberPart(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_' || value == '.' || value == 'x' || value == 'X';
    }

    public static bool IsPunctuation(char value)
    {
        return value is '(' or ')' or '{' or '}' or '[' or ']' or ';' or ',' or ':' or '.';
    }
}
