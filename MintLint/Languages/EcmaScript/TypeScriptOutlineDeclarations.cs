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
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            var keyword = tokens[i];
            if (keyword.Kind != TokenKind.Identifier || keyword.Text is not ("type" or "interface")
                || !ParserUtilities.IsNameToken(tokens[i + 1])) continue;
            // `import type Foo = require(...)` is an import alias, not a declared type.
            if (i > 0 && tokens[i - 1].Text is "import" or "." or "?.") continue;

            var cursor = i + 2;
            var assignmentInClose = false;
            if (tokens[cursor].Text == "<" && !SkipTypeArguments(source, ref cursor, out assignmentInClose))
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
                end = FindAliasEnd(source, cursor);
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
                            if (!SkipTypeArguments(source, ref cursor, out var assignment) || assignment) break;
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

    private static bool SkipTypeArguments(ParsedSource source, ref int cursor, out bool assignment)
    {
        assignment = false;
        var depth = 0;
        var tokens = source.Tokens;
        for (var i = cursor; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            if (text is "(" or "[" or "{")
            {
                i = ParserUtilities.SkipPaired(source, i);
                continue;
            }
            if (text == "<") depth++;
            else if (text is ">" or ">>" or ">>>" or ">=" or ">>=" or ">>>=")
            {
                var closeCount = text.EndsWith('=') ? text.Length - 1 : text.Length;
                depth -= closeCount;
                if (depth < 0) return false;
                if (depth == 0)
                {
                    assignment = text.EndsWith('=');
                    cursor = i + 1;
                    return true;
                }
            }
            else if (text is ";" or "}") return false;
        }
        return false;
    }

    private static int FindAliasEnd(ParsedSource source, int start)
    {
        var tokens = source.Tokens;
        for (var i = start; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            if (text is ";" or "}") return i - 1;
            if (i > start && tokens[i].Line > tokens[i - 1].Line && StartsDeclaration(tokens, i))
                return i - 1;
            if (text is "(" or "[" or "{") i = ParserUtilities.SkipPaired(source, i);
            else if (text == "<")
            {
                var afterTypeArguments = i;
                if (SkipTypeArguments(source, ref afterTypeArguments, out _)) i = afterTypeArguments - 1;
            }
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
            "import" => index + 1 < tokens.Count && tokens[index + 1].Text != "(",
            _ => false
        };
    }
}
