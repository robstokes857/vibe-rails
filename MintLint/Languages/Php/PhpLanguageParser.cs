using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class PhpLanguageParser : ILanguageParser
{
    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "class", "enum", "interface", "trait"
    };

    private static readonly HashSet<string> IncludeKeywords = new(StringComparer.Ordinal)
    {
        "include", "include_once", "require", "require_once"
    };

    public SourceLanguage Language => SourceLanguage.Php;

    public IReadOnlyCollection<string> Extensions { get; } = [".php", ".phtml"];

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, PhpTokenizer.Tokenize(source));
        ExtractImports(parsed);
        ExtractClasses(parsed);
        ExtractFunctions(parsed);
        ParserUtilities.AssignFunctionParents(parsed);
        AssignClassMembers(parsed);
        return parsed;
    }

    private static void ExtractImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (IncludeKeywords.Contains(tokens[i].Text))
            {
                int end = FindStatementEnd(tokens, i + 1);
                for (int j = i + 1; j <= end; j++)
                {
                    if (tokens[j].Kind == TokenKind.String)
                    {
                        ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[j].Text));
                        break;
                    }
                }
            }
            else if (tokens[i].Text == "use")
            {
                int next = ParserUtilities.NextSignificant(tokens, i + 1);
                if (next < 0 || tokens[next].Text == "(")
                {
                    continue; // closure capture: `function () use ($value)`
                }

                int end = FindStatementEnd(tokens, next);
                int segmentStart = next;
                for (int j = next; j <= end + 1; j++)
                {
                    if (j == end + 1 || tokens[j].Text is "," or ";")
                    {
                        ParserUtilities.AddUnique(parsed.ImportSources, JoinNamespace(tokens, segmentStart, j - 1));
                        segmentStart = j + 1;
                    }
                }
            }
        }
    }

    private static void ExtractClasses(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!TypeKeywords.Contains(tokens[i].Text) ||
                (i > 0 && tokens[i - 1].Text == "::"))
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            if (nameIndex < 0 || !ParserUtilities.IsNameToken(tokens[nameIndex]))
            {
                continue; // anonymous class
            }

            int bodyStart = FindBodyStart(parsed, nameIndex + 1);
            if (bodyStart < 0)
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd >= 0)
            {
                parsed.Classes.Add(new ClassSpan(tokens[nameIndex].Text, i, bodyStart, bodyEnd));
            }
        }
    }

    private static void ExtractFunctions(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "function")
            {
                continue;
            }

            int cursor = ParserUtilities.NextSignificant(tokens, i + 1);
            if (cursor >= 0 && tokens[cursor].Text == "&")
            {
                cursor = ParserUtilities.NextSignificant(tokens, cursor + 1);
            }

            string name = "anonymous";
            if (cursor >= 0 && ParserUtilities.IsNameToken(tokens[cursor]))
            {
                name = tokens[cursor].Text;
                cursor = ParserUtilities.NextSignificant(tokens, cursor + 1);
            }

            if (cursor < 0 || tokens[cursor].Text != "(")
            {
                continue;
            }

            int parameterEnd = parsed.ParenPartner[cursor];
            int bodyStart = parameterEnd >= 0 ? FindBodyStart(parsed, parameterEnd + 1) : -1;
            if (bodyStart < 0)
            {
                continue; // abstract/interface declaration
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd >= 0)
            {
                parsed.Functions.Add(new FunctionSpan(name, i, bodyStart, bodyEnd, isMethod: false, classIndex: -1));
            }
        }
    }

    private static void AssignClassMembers(ParsedSource parsed)
    {
        for (int classIndex = 0; classIndex < parsed.Classes.Count; classIndex++)
        {
            ClassSpan classSpan = parsed.Classes[classIndex];
            for (int functionIndex = 0; functionIndex < parsed.Functions.Count; functionIndex++)
            {
                FunctionSpan function = parsed.Functions[functionIndex];
                if (function.ParentIndex == -1 && function.StartIndex > classSpan.BodyStartIndex &&
                    function.EndIndex <= classSpan.EndIndex)
                {
                    classSpan.MethodFunctionIndexes.Add(functionIndex);
                }
            }

            for (int i = classSpan.BodyStartIndex + 1; i < classSpan.EndIndex; i++)
            {
                string text = parsed.Tokens[i].Text;
                if (text.StartsWith('$') && !IsInsideFunction(parsed, i))
                {
                    ParserUtilities.AddUnique(classSpan.FieldNames, text[1..]);
                }

                if (text == "this" && i + 2 < classSpan.EndIndex && parsed.Tokens[i + 1].Text == "->" &&
                    ParserUtilities.IsNameToken(parsed.Tokens[i + 2]))
                {
                    int after = ParserUtilities.NextSignificant(parsed.Tokens, i + 3);
                    if (after < 0 || parsed.Tokens[after].Text != "(")
                    {
                        ParserUtilities.AddUnique(classSpan.FieldNames, parsed.Tokens[i + 2].Text);
                    }
                }
            }
        }
    }

    private static bool IsInsideFunction(ParsedSource parsed, int tokenIndex)
    {
        foreach (FunctionSpan function in parsed.Functions)
        {
            if (tokenIndex >= function.StartIndex && tokenIndex <= function.EndIndex)
            {
                return true;
            }
        }

        return false;
    }

    private static int FindBodyStart(ParsedSource parsed, int start)
    {
        for (int i = Math.Max(0, start); i < parsed.Tokens.Count; i++)
        {
            string text = parsed.Tokens[i].Text;
            if (text is "(" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i);
                continue;
            }

            if (text == "{")
            {
                return i;
            }

            if (text == ";")
            {
                return -1;
            }
        }

        return -1;
    }

    private static int FindStatementEnd(List<Token> tokens, int start)
    {
        for (int i = Math.Max(0, start); i < tokens.Count; i++)
        {
            if (tokens[i].Text == ";")
            {
                return i;
            }
        }

        return tokens.Count - 1;
    }

    private static string JoinNamespace(List<Token> tokens, int start, int end)
    {
        string result = string.Empty;
        for (int i = start; i <= end && i < tokens.Count; i++)
        {
            if (tokens[i].Text == "as")
            {
                break;
            }

            result += tokens[i].Text;
        }

        return result.Trim();
    }
}
