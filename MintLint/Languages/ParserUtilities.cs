using System;
using System.Collections.Generic;

namespace MintLint;

internal static class ParserUtilities
{
    private static readonly HashSet<string> InvalidNames = new(StringComparer.Ordinal)
    {
        "catch", "class", "def", "do", "else", "enum", "except", "finally", "fn", "for",
        "foreach", "func", "function", "if", "impl", "import", "interface", "match",
        "namespace", "package", "return", "select", "struct", "switch", "trait", "try",
        "type", "union", "use", "while"
    };

    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "abstract", "accessor", "async", "declare", "export", "extern", "get", "internal",
        "final", "friend", "inline", "mut", "mutable", "new", "override", "private",
        "protected", "public", "pub", "readonly", "required", "sealed", "set", "static",
        "synchronized", "unsafe", "virtual", "volatile"
    };

    // The tokenizers discard all trivia (whitespace and comments), so every emitted token
    // is already significant. These helpers therefore reduce to a bounds-checked index,
    // but are kept as named operations so call sites read as "the next/previous real token"
    // and so trivia handling could be reintroduced in one place if a tokenizer ever keeps it.
    public static int NextSignificant(List<Token> tokens, int start)
    {
        return start >= 0 && start < tokens.Count ? start : -1;
    }

    public static int PreviousSignificant(List<Token> tokens, int start)
    {
        return start >= 0 && start < tokens.Count ? start : -1;
    }

    public static bool IsNameToken(Token token)
    {
        return token.IsIdentifierLike && !InvalidNames.Contains(token.Text);
    }

    public static string StringLiteralValue(string text)
    {
        if (text.Length >= 2)
        {
            char first = text[0];
            if (first is '"' or '\'' or '`' && text[^1] == first)
            {
                return text[1..^1];
            }
        }

        return text;
    }

    public static bool IsMemberModifier(string text)
    {
        return MemberModifiers.Contains(text);
    }

    public static bool IsBuiltInType(string text)
    {
        return text is "bool" or "boolean" or "byte" or "char" or "decimal" or "double"
            or "dynamic" or "float" or "f32" or "f64" or "i8" or "i16" or "i32" or "i64"
            or "i128" or "int" or "int8" or "int16" or "int32" or "int64" or "isize"
            or "long" or "nint" or "nuint" or "object" or "rune" or "sbyte" or "short"
            or "string" or "str" or "uint" or "uint8" or "uint16" or "uint32" or "uint64"
            or "uintptr" or "ulong" or "usize" or "ushort" or "var" or "void";
    }

    public static int FindNextToken(List<Token> tokens, string text, int start)
    {
        for (int i = Math.Max(0, start); i < tokens.Count; i++)
        {
            if (tokens[i].Text == text)
            {
                return i;
            }
        }

        return -1;
    }

    public static int SkipPaired(ParsedSource parsed, int index)
    {
        string text = parsed.Tokens[index].Text;
        if (text == "{")
        {
            return parsed.BracePartner[index] >= 0 ? parsed.BracePartner[index] : index;
        }

        if (text == "(")
        {
            return parsed.ParenPartner[index] >= 0 ? parsed.ParenPartner[index] : index;
        }

        if (text == "[")
        {
            return parsed.BracketPartner[index] >= 0 ? parsed.BracketPartner[index] : index;
        }

        return index;
    }

    public static void AssignFunctionParents(ParsedSource parsed)
    {
        for (int i = 0; i < parsed.Functions.Count; i++)
        {
            FunctionSpan child = parsed.Functions[i];
            int parentIndex = -1;
            int parentWidth = int.MaxValue;

            for (int j = 0; j < parsed.Functions.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                FunctionSpan candidate = parsed.Functions[j];
                if (child.StartIndex > candidate.StartIndex && child.EndIndex <= candidate.EndIndex)
                {
                    int width = candidate.EndIndex - candidate.StartIndex;
                    if (width < parentWidth)
                    {
                        parentIndex = j;
                        parentWidth = width;
                    }
                }
            }

            child.ParentIndex = parentIndex;
        }
    }

    public static void AddUnique(List<string> values, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        foreach (string existing in values)
        {
            if (string.Equals(existing, value, StringComparison.Ordinal))
            {
                return;
            }
        }

        values.Add(value);
    }
}
