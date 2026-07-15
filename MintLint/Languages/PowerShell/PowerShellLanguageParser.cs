using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class PowerShellLanguageParser : ILanguageParser
{
    private static readonly HashSet<string> FunctionKeywords = new(StringComparer.Ordinal)
    {
        "filter", "function", "workflow"
    };

    public SourceLanguage Language => SourceLanguage.PowerShell;

    public IReadOnlyCollection<string> Extensions { get; } = [".ps1", ".psm1"];

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, PowerShellTokenizer.Tokenize(source));
        ExtractImports(parsed);
        ExtractClasses(parsed);
        ExtractFunctions(parsed);
        ExtractClassMethods(parsed);
        ParserUtilities.AssignFunctionParents(parsed);
        AssignClassMembers(parsed);
        return parsed;
    }

    private static void ExtractImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            int valueStart = -1;
            if (tokens[i].Text == "using" && i + 1 < tokens.Count && tokens[i + 1].Text == "module")
            {
                valueStart = i + 2;
            }
            else if (tokens[i].Text == "import-module")
            {
                valueStart = i + 1;
            }

            if (valueStart < 0)
            {
                continue;
            }

            for (int j = valueStart; j < tokens.Count && tokens[j].Line == tokens[i].Line; j++)
            {
                if (tokens[j].Kind == TokenKind.String)
                {
                    ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[j].Text));
                    break;
                }

                if (tokens[j].Kind == TokenKind.Identifier && !tokens[j].Text.StartsWith('$'))
                {
                    ParserUtilities.AddUnique(parsed.ImportSources, tokens[j].Text);
                    break;
                }
            }
        }
    }

    private static void ExtractClasses(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text is not "class" and not "enum")
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            if (nameIndex < 0 || !ParserUtilities.IsNameToken(tokens[nameIndex]))
            {
                continue;
            }

            int bodyStart = FindBodyStart(parsed, nameIndex + 1);
            int bodyEnd = bodyStart >= 0 ? parsed.BracePartner[bodyStart] : -1;
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
            if (!FunctionKeywords.Contains(tokens[i].Text))
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            if (nameIndex < 0 || !ParserUtilities.IsNameToken(tokens[nameIndex]))
            {
                continue;
            }

            int bodyStart = FindBodyStart(parsed, nameIndex + 1);
            int bodyEnd = bodyStart >= 0 ? parsed.BracePartner[bodyStart] : -1;
            if (bodyEnd >= 0)
            {
                parsed.Functions.Add(new FunctionSpan(tokens[nameIndex].Text, i, bodyStart, bodyEnd, isMethod: false, classIndex: -1));
            }
        }
    }

    private static void ExtractClassMethods(ParsedSource parsed)
    {
        foreach (ClassSpan classSpan in parsed.Classes)
        {
            int i = classSpan.BodyStartIndex + 1;
            while (i < classSpan.EndIndex)
            {
                if (parsed.Tokens[i].Text == "{")
                {
                    int bodyEnd = parsed.BracePartner[i];
                    i = bodyEnd > i ? bodyEnd + 1 : i + 1;
                    continue;
                }

                if (parsed.Tokens[i].Text != "(")
                {
                    i++;
                    continue;
                }

                int nameIndex = ParserUtilities.PreviousSignificant(parsed.Tokens, i - 1);
                int parameterEnd = parsed.ParenPartner[i];
                int bodyStart = parameterEnd >= 0 ? ParserUtilities.NextSignificant(parsed.Tokens, parameterEnd + 1) : -1;
                if (nameIndex >= 0 && ParserUtilities.IsNameToken(parsed.Tokens[nameIndex]) &&
                    bodyStart >= 0 && parsed.Tokens[bodyStart].Text == "{")
                {
                    int bodyEnd = parsed.BracePartner[bodyStart];
                    if (bodyEnd > bodyStart && bodyEnd <= classSpan.EndIndex &&
                        !ContainsFunctionAt(parsed, nameIndex, bodyEnd))
                    {
                        parsed.Functions.Add(new FunctionSpan(
                            parsed.Tokens[nameIndex].Text,
                            nameIndex,
                            bodyStart,
                            bodyEnd,
                            isMethod: true,
                            classIndex: -1));
                        i = bodyEnd + 1;
                        continue;
                    }
                }

                i = parameterEnd > i ? parameterEnd + 1 : i + 1;
            }
        }
    }

    private static void AssignClassMembers(ParsedSource parsed)
    {
        foreach (ClassSpan classSpan in parsed.Classes)
        {
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

                if (text == "this" && i + 2 < classSpan.EndIndex && parsed.Tokens[i + 1].Text == "." &&
                    ParserUtilities.IsNameToken(parsed.Tokens[i + 2]))
                {
                    ParserUtilities.AddUnique(classSpan.FieldNames, parsed.Tokens[i + 2].Text);
                }
            }
        }
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

    private static bool ContainsFunctionAt(ParsedSource parsed, int start, int end)
    {
        foreach (FunctionSpan function in parsed.Functions)
        {
            if (function.StartIndex == start && function.EndIndex == end)
            {
                return true;
            }
        }

        return false;
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
}
