using System;
using System.Collections.Generic;

namespace MintLint;

// Named types belong in the repository map, but are not executable classes/functions in
// the analyzer's quality metrics. Reuse its tokens without changing those measurements.
internal sealed record TypeScriptOutlineDeclaration(SourceOutlineSymbol Symbol, int StartIndex, int EndIndex);

internal static class TypeScriptOutlineDeclarations
{
    public static IReadOnlyList<TypeScriptOutlineDeclaration> Read(ParsedSource source)
    {
        if (source.Language != SourceLanguage.TypeScript) return [];

        var result = new List<TypeScriptOutlineDeclaration>();
        var tokens = source.Tokens;
        var partners = ReadTypeArgumentPartners(source);
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            var keyword = tokens[i];
            if (keyword.Kind != TokenKind.Identifier || keyword.Text is not ("type" or "interface")
                || !ParserUtilities.IsNameToken(tokens[i + 1])) continue;
            // `import type Foo = require(...)` is an import alias, not a declared type.
            if (i > 0 && tokens[i - 1].Text is "import" or "." or "?.") continue;

            var cursor = i + 2;
            var assignmentInClose = false;
            if (tokens[cursor].Text == "<" && !SkipTypeArguments(tokens, partners, ref cursor, out assignmentInClose))
                continue;

            int end;
            if (keyword.Text == "type")
            {
                if (!assignmentInClose)
                {
                    if (cursor >= tokens.Count || tokens[cursor].Text != "=") continue;
                    cursor++;
                }
                if (cursor >= tokens.Count || tokens[cursor].Text is ";" or "}") continue;
                end = FindAliasEnd(source, partners, cursor);
            }
            else
            {
                if (assignmentInClose || cursor >= tokens.Count) continue;
                if (tokens[cursor].Text == "extends")
                {
                    cursor++;
                    while (cursor < tokens.Count && tokens[cursor].Text != "{")
                    {
                        if (tokens[cursor].Text == "<")
                        {
                            if (!SkipTypeArguments(tokens, partners, ref cursor, out var assignment) || assignment) break;
                            continue;
                        }
                        if (tokens[cursor].Kind != TokenKind.Identifier && tokens[cursor].Text is not ("." or ",")) break;
                        cursor++;
                    }
                }
                if (cursor >= tokens.Count || tokens[cursor].Text != "{") continue;
                end = source.BracePartner[cursor];
                if (end < 0) continue;
            }

            result.Add(new(new(tokens[i + 1].Text, keyword.Text == "interface" ? "interface" : "type", keyword.Line), i, end));
            i = end;
        }
        return result;
    }

    // One pass pairs each `<` with the close that returns it to depth zero, scoped to its
    // enclosing paired group. Scanning forward from every `<` on demand repeated the same walk
    // for each unmatched delimiter, which made an in-progress or malformed source quadratic.
    private static int[] ReadTypeArgumentPartners(ParsedSource source)
    {
        var tokens = source.Tokens;
        var partners = new int[tokens.Count];
        Array.Fill(partners, -1);
        var open = new List<int>();
        var frames = new Stack<(int Close, int OpenCount)>();
        var floor = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (frames.Count > 0 && frames.Peek().Close == i)
            {
                // Delimiters left open inside a group never match outside it.
                open.RemoveRange(floor, open.Count - floor);
                frames.Pop();
                floor = frames.Count > 0 ? frames.Peek().OpenCount : 0;
                continue;
            }
            var text = tokens[i].Text;
            if (text is "(" or "[" or "{")
            {
                var close = ParserUtilities.SkipPaired(source, i);
                // An unmatched opener is stepped over, exactly as the forward scan did.
                if (close <= i) continue;
                frames.Push((close, open.Count));
                floor = open.Count;
                continue;
            }
            if (text == "<") { open.Add(i); continue; }
            if (text is ">" or ">>" or ">>>" or ">=" or ">>=" or ">>>=")
            {
                var closeCount = text.EndsWith('=') ? text.Length - 1 : text.Length;
                var available = open.Count - floor;
                if (closeCount <= available)
                {
                    // Only the outermost `<` this token closes lands on depth zero. The inner
                    // ones overshoot, which a scan from them reports as no match.
                    partners[open[open.Count - closeCount]] = i;
                    open.RemoveRange(open.Count - closeCount, closeCount);
                }
                else open.RemoveRange(floor, available);
                continue;
            }
            if (text is ";" or "}") open.RemoveRange(floor, open.Count - floor);
        }
        return partners;
    }

    private static bool SkipTypeArguments(IReadOnlyList<Token> tokens, int[] partners, ref int cursor, out bool assignment)
    {
        var partner = partners[cursor];
        assignment = partner >= 0 && tokens[partner].Text.EndsWith('=');
        if (partner < 0) return false;
        cursor = partner + 1;
        return true;
    }

    private static int FindAliasEnd(ParsedSource source, int[] partners, int start)
    {
        var tokens = source.Tokens;
        for (var i = start; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            if (text is ";" or "}") return i - 1;
            if (i > start && tokens[i].Line > tokens[i - 1].Line && StartsDeclaration(tokens, i))
                return i - 1;
            if (text is "(" or "[" or "{") i = ParserUtilities.SkipPaired(source, i);
            else if (text == "<" && partners[i] >= 0) i = partners[i];
        }
        return tokens.Count - 1;
    }

    private static bool StartsDeclaration(IReadOnlyList<Token> tokens, int index)
    {
        if (tokens[index].Kind != TokenKind.Identifier) return false;
        return tokens[index].Text switch
        {
            "type" or "interface" or "class" or "function" or "const" or "let" or "var" or "enum" or "namespace"
                => index + 1 < tokens.Count && ParserUtilities.IsNameToken(tokens[index + 1]),
            "export" or "declare" => true,
            // `async` can also name a type. Only the function-declaration prefix ends
            // a semicolonless alias; a multiline union may legitimately mention async.
            "async" => index + 1 < tokens.Count && tokens[index + 1].Text == "function"
                && tokens[index + 1].Line == tokens[index].Line,
            "import" => index + 1 < tokens.Count && tokens[index + 1].Text != "(",
            _ => false
        };
    }
}
