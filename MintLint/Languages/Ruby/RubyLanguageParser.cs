using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class RubyLanguageParser : ILanguageParser
{
    public SourceLanguage Language => SourceLanguage.Ruby;

    public IReadOnlyCollection<string> Extensions { get; } = [".rb", ".rake", ".gemspec"];

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, RubyTokenizer.Tokenize(source));
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
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Text is not "require" and not "require_relative" and not "load" and not "autoload")
            {
                continue;
            }

            int line = tokens[i].Line;
            for (int j = i + 1; j < tokens.Count && tokens[j].Line == line; j++)
            {
                if (tokens[j].Kind == TokenKind.String)
                {
                    ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[j].Text));
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
            if (tokens[i].Text is not "class" and not "module")
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            string name = nameIndex >= 0 && ParserUtilities.IsNameToken(tokens[nameIndex])
                ? tokens[nameIndex].Text
                : "anonymous";
            int bodyStart = FindSyntheticBody(tokens, nameIndex >= 0 ? nameIndex + 1 : i + 1);
            if (bodyStart < 0)
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd >= 0)
            {
                parsed.Classes.Add(new ClassSpan(name, i, bodyStart, bodyEnd));
            }
        }
    }

    private static void ExtractFunctions(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "def")
            {
                continue;
            }

            int bodyStart = FindSyntheticBody(tokens, i + 1);
            if (bodyStart < 0)
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd < 0)
            {
                continue;
            }

            string name = "anonymous";
            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            if (nameIndex >= 0 && tokens[nameIndex].Text == "self")
            {
                int separator = ParserUtilities.NextSignificant(tokens, nameIndex + 1);
                nameIndex = separator >= 0 && tokens[separator].Text is "." or "::"
                    ? ParserUtilities.NextSignificant(tokens, separator + 1)
                    : nameIndex;
            }

            if (nameIndex >= 0 && ParserUtilities.IsNameToken(tokens[nameIndex]) && tokens[nameIndex].Text != "self")
            {
                name = tokens[nameIndex].Text;
            }

            parsed.Functions.Add(new FunctionSpan(name, i, bodyStart, bodyEnd, isMethod: false, classIndex: -1));
        }
    }

    private static void AssignClassMembers(ParsedSource parsed)
    {
        for (int functionIndex = 0; functionIndex < parsed.Functions.Count; functionIndex++)
        {
            FunctionSpan function = parsed.Functions[functionIndex];
            int owner = -1;
            int ownerWidth = int.MaxValue;
            for (int classIndex = 0; classIndex < parsed.Classes.Count; classIndex++)
            {
                ClassSpan candidate = parsed.Classes[classIndex];
                if (function.StartIndex > candidate.BodyStartIndex && function.EndIndex <= candidate.EndIndex)
                {
                    int width = candidate.EndIndex - candidate.BodyStartIndex;
                    if (width < ownerWidth)
                    {
                        owner = classIndex;
                        ownerWidth = width;
                    }
                }
            }

            if (owner >= 0)
            {
                parsed.Classes[owner].MethodFunctionIndexes.Add(functionIndex);
            }
        }

        foreach (ClassSpan classSpan in parsed.Classes)
        {
            for (int i = classSpan.BodyStartIndex + 1; i < classSpan.EndIndex; i++)
            {
                string text = parsed.Tokens[i].Text;
                if (text.Length > 1 && text[0] == '@' && text[1] != '@')
                {
                    ParserUtilities.AddUnique(classSpan.FieldNames, text);
                }
            }
        }
    }

    private static int FindSyntheticBody(List<Token> tokens, int start)
    {
        int declarationLine = start > 0 && start - 1 < tokens.Count ? tokens[start - 1].Line : -1;
        for (int i = Math.Max(0, start); i < tokens.Count; i++)
        {
            if (tokens[i].Text == "{" && tokens[i].Line == declarationLine)
            {
                return i;
            }

            if (declarationLine >= 0 && tokens[i].Line != declarationLine)
            {
                break;
            }
        }

        return -1;
    }
}
