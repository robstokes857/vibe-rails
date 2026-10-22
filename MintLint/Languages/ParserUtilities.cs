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

    /// <summary>
    /// Counts generic lists closed by a token; the tokenizer emits nested closes as shift operators.
    /// </summary>
    public static int CloseAngleCount(string text) => text switch
    {
        ">" => 1,
        ">>" => 2,
        ">>>" => 3,
        _ => 0
    };

    /// <summary>Finds the end of a generic list, including a combined nested closing token.</summary>
    public static int FindMatchingCloseAngle(List<Token> tokens, int openAngle, int end)
    {
        int depth = 0;
        for (int i = openAngle; i <= end && i < tokens.Count; i++)
        {
            if (tokens[i].Text == "<")
            {
                depth++;
                continue;
            }

            int closes = CloseAngleCount(tokens[i].Text);
            if (closes > 0)
            {
                depth -= closes;
                if (depth <= 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Finds the start of a generic list when scanning backward from its closing token.</summary>
    public static int FindMatchingOpenAngleBack(List<Token> tokens, int closeAngle)
    {
        int depth = 0;
        for (int i = closeAngle; i >= 0; i--)
        {
            depth += CloseAngleCount(tokens[i].Text);
            if (tokens[i].Text == "<")
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    public static void AssignFunctionParents(ParsedSource parsed)
    {
        // A function's parent is the narrowest function that starts strictly before it and ends
        // at or after it; on a width tie the lowest index wins. Spans need not nest cleanly, so
        // rather than a stack this sweeps by start and keeps the best candidate per end position
        // in a suffix-min Fenwick tree: O(F log F) instead of the all-pairs O(F²).
        List<FunctionSpan> functions = parsed.Functions;
        int count = functions.Count;
        if (count == 0)
        {
            return;
        }

        int[] ends = new int[count];
        int[] starts = new int[count];
        int[] byStart = new int[count];
        for (int i = 0; i < count; i++)
        {
            ends[i] = functions[i].EndIndex;
            starts[i] = functions[i].StartIndex;
            byStart[i] = i;
        }

        Array.Sort(ends);
        int distinctEnds = 0;
        for (int i = 0; i < count; i++)
        {
            if (distinctEnds == 0 || ends[distinctEnds - 1] != ends[i])
            {
                ends[distinctEnds++] = ends[i];
            }
        }

        Array.Sort(starts, byStart);

        // Slot p covers ends ranked >= distinctEnds - p, so a prefix query reads a suffix of ends.
        int[] tree = new int[distinctEnds + 1];
        Array.Fill(tree, -1);

        int group = 0;
        while (group < count)
        {
            int groupEnd = group;
            while (groupEnd < count && starts[groupEnd] == starts[group])
            {
                groupEnd++;
            }

            // Same-start functions cannot parent each other, so query the group before inserting it.
            for (int k = group; k < groupEnd; k++)
            {
                FunctionSpan child = functions[byStart[k]];
                int best = -1;
                for (int p = distinctEnds - Array.BinarySearch(ends, 0, distinctEnds, child.EndIndex); p > 0; p -= p & -p)
                {
                    best = NarrowerParent(functions, best, tree[p]);
                }

                child.ParentIndex = best;
            }

            for (int k = group; k < groupEnd; k++)
            {
                int index = byStart[k];
                for (int p = distinctEnds - Array.BinarySearch(ends, 0, distinctEnds, functions[index].EndIndex); p <= distinctEnds; p += p & -p)
                {
                    tree[p] = NarrowerParent(functions, tree[p], index);
                }
            }

            group = groupEnd;
        }
    }

    private static int NarrowerParent(List<FunctionSpan> functions, int left, int right)
    {
        if (left < 0 || right < 0)
        {
            return left < 0 ? right : left;
        }

        int leftWidth = functions[left].EndIndex - functions[left].StartIndex;
        int rightWidth = functions[right].EndIndex - functions[right].StartIndex;
        if (leftWidth != rightWidth)
        {
            return leftWidth < rightWidth ? left : right;
        }

        return Math.Min(left, right);
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
