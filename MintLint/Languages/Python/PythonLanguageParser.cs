using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class PythonLanguageParser : ILanguageParser
{
    public SourceLanguage Language => SourceLanguage.Python;

    public IReadOnlyCollection<string> Extensions { get; } = [".py"];

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, PythonTokenizer.Tokenize(source));
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
            if (tokens[i].Text == "import")
            {
                if (HasPreviousTokenOnLine(tokens, i, "from"))
                {
                    continue;
                }

                int line = tokens[i].Line;
                for (int j = i + 1; j < tokens.Count && tokens[j].Line == line; j++)
                {
                    if (tokens[j].Text == "as")
                    {
                        j++;
                        continue;
                    }

                    if (tokens[j].Kind == TokenKind.Identifier)
                    {
                        ParserUtilities.AddUnique(parsed.ImportSources, ReadDottedName(tokens, ref j, line));
                    }
                }
            }
            else if (tokens[i].Text == "from")
            {
                int line = tokens[i].Line;
                int cursor = i + 1;
                ParserUtilities.AddUnique(parsed.ImportSources, ReadDottedName(tokens, ref cursor, line));
            }
        }
    }

    private static void ExtractClasses(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "class")
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            string name = nameIndex >= 0 && ParserUtilities.IsNameToken(tokens[nameIndex]) ? tokens[nameIndex].Text : "anonymous";
            int colon = ParserUtilities.FindNextToken(tokens, ":", nameIndex >= 0 ? nameIndex + 1 : i + 1);
            int bodyStart = colon >= 0 ? ParserUtilities.NextSignificant(tokens, colon + 1) : -1;
            if (bodyStart < 0 || tokens[bodyStart].Text != "{")
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd >= 0)
            {
                parsed.Classes.Add(new ClassSpan(name, i, bodyStart, bodyEnd));
                i = bodyEnd;
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

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            if (nameIndex < 0 || !ParserUtilities.IsNameToken(tokens[nameIndex]))
            {
                continue;
            }

            int parameterStart = ParserUtilities.NextSignificant(tokens, nameIndex + 1);
            if (parameterStart < 0 || tokens[parameterStart].Text != "(")
            {
                continue;
            }

            int parameterEnd = parsed.ParenPartner[parameterStart];
            int colon = parameterEnd >= 0 ? ParserUtilities.FindNextToken(tokens, ":", parameterEnd + 1) : -1;
            int bodyStart = colon >= 0 ? ParserUtilities.NextSignificant(tokens, colon + 1) : -1;
            if (bodyStart < 0 || tokens[bodyStart].Text != "{")
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd >= 0)
            {
                parsed.Functions.Add(new FunctionSpan(tokens[nameIndex].Text, i, bodyStart, bodyEnd, isMethod: false, classIndex: -1));
            }
        }
    }

    private static void AssignClassMembers(ParsedSource parsed)
    {
        for (int classIndex = 0; classIndex < parsed.Classes.Count; classIndex++)
        {
            ClassSpan classSpan = parsed.Classes[classIndex];
            HashSet<string> fields = new(StringComparer.Ordinal);

            for (int functionIndex = 0; functionIndex < parsed.Functions.Count; functionIndex++)
            {
                FunctionSpan function = parsed.Functions[functionIndex];
                if (function.ParentIndex != -1 ||
                    function.StartIndex <= classSpan.BodyStartIndex ||
                    function.EndIndex > classSpan.EndIndex)
                {
                    continue;
                }

                classSpan.MethodFunctionIndexes.Add(functionIndex);
                CollectSelfFields(parsed, function, fields);
            }

            foreach (string field in fields)
            {
                classSpan.FieldNames.Add(field);
            }
        }
    }

    private static void CollectSelfFields(ParsedSource parsed, FunctionSpan function, HashSet<string> fields)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = function.BodyStartIndex + 1; i + 2 < function.EndIndex; i++)
        {
            if (tokens[i].Text == "self" && tokens[i + 1].Text == "." && ParserUtilities.IsNameToken(tokens[i + 2]))
            {
                int next = ParserUtilities.NextSignificant(tokens, i + 3);
                if (next < 0 || tokens[next].Text != "(")
                {
                    fields.Add(tokens[i + 2].Text);
                }

                i += 2;
            }
        }
    }

    private static string ReadDottedName(List<Token> tokens, ref int index, int line)
    {
        string result = string.Empty;
        bool expectName = true;

        while (index < tokens.Count && tokens[index].Line == line)
        {
            Token token = tokens[index];
            if (expectName)
            {
                if (token.Kind != TokenKind.Identifier)
                {
                    break;
                }

                result = result.Length == 0 ? token.Text : result + "." + token.Text;
                expectName = false;
                index++;
                continue;
            }

            if (token.Text == ".")
            {
                expectName = true;
                index++;
                continue;
            }

            break;
        }

        return result;
    }

    private static bool HasPreviousTokenOnLine(List<Token> tokens, int index, string text)
    {
        int line = tokens[index].Line;
        for (int i = index - 1; i >= 0 && tokens[i].Line == line; i--)
        {
            if (tokens[i].Text == text)
            {
                return true;
            }
        }

        return false;
    }
}
