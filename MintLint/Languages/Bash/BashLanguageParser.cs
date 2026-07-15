using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class BashLanguageParser : ILanguageParser
{
    public SourceLanguage Language => SourceLanguage.Bash;

    public IReadOnlyCollection<string> Extensions { get; } = [".sh", ".bash"];

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, BashTokenizer.Tokenize(source));
        ExtractImports(parsed);
        ExtractFunctions(parsed);
        ParserUtilities.AssignFunctionParents(parsed);
        return parsed;
    }

    private static void ExtractImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            bool firstOnLine = i == 0 || tokens[i - 1].Line != tokens[i].Line || tokens[i - 1].Text == ";";
            if (!firstOnLine || tokens[i].Text is not "source" and not ".")
            {
                continue;
            }

            int next = ParserUtilities.NextSignificant(tokens, i + 1);
            if (next < 0 || tokens[next].Line != tokens[i].Line)
            {
                continue;
            }

            if (tokens[next].Kind == TokenKind.String)
            {
                ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[next].Text));
                continue;
            }

            string path = string.Empty;
            for (int j = next; j < tokens.Count && tokens[j].Line == tokens[i].Line && tokens[j].Text != ";"; j++)
            {
                path += tokens[j].Text;
            }

            ParserUtilities.AddUnique(parsed.ImportSources, path);
        }
    }

    private static void ExtractFunctions(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            int nameIndex = -1;
            int cursor = -1;
            int startIndex = i;

            if (tokens[i].Text == "function")
            {
                nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
                cursor = nameIndex >= 0 ? ParserUtilities.NextSignificant(tokens, nameIndex + 1) : -1;
                if (cursor >= 0 && tokens[cursor].Text == "(")
                {
                    int close = parsed.ParenPartner[cursor];
                    cursor = close >= 0 ? ParserUtilities.NextSignificant(tokens, close + 1) : -1;
                }
            }
            else if (ParserUtilities.IsNameToken(tokens[i]) && i + 3 < tokens.Count &&
                tokens[i + 1].Text == "(" && tokens[i + 2].Text == ")")
            {
                nameIndex = i;
                cursor = i + 3;
            }

            if (nameIndex < 0 || cursor < 0 || cursor >= tokens.Count || tokens[cursor].Text != "{" ||
                !ParserUtilities.IsNameToken(tokens[nameIndex]))
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[cursor];
            if (bodyEnd >= 0)
            {
                parsed.Functions.Add(new FunctionSpan(tokens[nameIndex].Text, startIndex, cursor, bodyEnd, isMethod: false, classIndex: -1));
                i = cursor;
            }
        }
    }
}
