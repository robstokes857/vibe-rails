using System.Collections.Generic;

namespace MintLint;

internal sealed class CSharpLanguageParser : ILanguageParser
{
    public SourceLanguage Language => SourceLanguage.CSharp;

    public IReadOnlyCollection<string> Extensions { get; } = [".cs"];

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, BraceLanguageTokenizer.Tokenize(source));
        ExtractImports(parsed);
        ExtractTypes(parsed);
        ParserUtilities.AssignFunctionParents(parsed);
        return parsed;
    }

    private static void ExtractImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "using")
            {
                continue;
            }

            int next = ParserUtilities.NextSignificant(tokens, i + 1);
            if (next < 0 || tokens[next].Text is "(" or "static")
            {
                continue;
            }

            int cursor = next;
            ParserUtilities.AddUnique(parsed.ImportSources, ReadDottedName(tokens, ref cursor, tokens[i].Line));
        }
    }

    private static void ExtractTypes(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!IsTypeDeclaration(tokens, i))
            {
                continue;
            }

            // `record class`/`record struct` carry two keywords before the name.
            int nameIndex = i + 1;
            if (tokens[i].Text == "record" && nameIndex < tokens.Count && tokens[nameIndex].Text is "class" or "struct")
            {
                nameIndex++;
            }

            if (nameIndex >= tokens.Count || !ParserUtilities.IsNameToken(tokens[nameIndex]))
            {
                continue;
            }

            string name = tokens[nameIndex].Text;
            (int bodyStart, int terminator) = FindTypeBody(parsed, nameIndex + 1);

            if (bodyStart >= 0)
            {
                int bodyEnd = parsed.BracePartner[bodyStart];
                if (bodyEnd < 0)
                {
                    continue;
                }

                int classIndex = parsed.Classes.Count;
                ClassSpan span = new(name, i, bodyStart, bodyEnd);
                parsed.Classes.Add(span);
                CapturePositionalParameters(parsed, nameIndex + 1, bodyStart, span);
                ExtractClassMembers(parsed, span, classIndex);
                i = bodyEnd;
            }
            else if (terminator >= 0)
            {
                // A positional record without a body, e.g. `record Point(int X, int Y);`.
                ClassSpan span = new(name, i, terminator, terminator);
                parsed.Classes.Add(span);
                CapturePositionalParameters(parsed, nameIndex + 1, terminator, span);
                i = terminator;
            }
        }
    }

    private static bool IsTypeDeclaration(List<Token> tokens, int index)
    {
        string text = tokens[index].Text;
        if (text is "class" or "struct" or "interface")
        {
            return true;
        }

        if (text != "record")
        {
            return false;
        }

        // `record` is a contextual keyword, so confirm it introduces a declaration
        // rather than being used as an identifier (`var record = ...`).
        int next = index + 1;
        if (next >= tokens.Count)
        {
            return false;
        }

        if (tokens[next].Text is "class" or "struct")
        {
            return true;
        }

        if (!ParserUtilities.IsNameToken(tokens[next]))
        {
            return false;
        }

        int after = next + 1;
        return after < tokens.Count && tokens[after].Text is "(" or "{" or ":" or "<" or ";";
    }

    private static (int BodyStart, int Terminator) FindTypeBody(ParsedSource parsed, int start)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = start; i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (text is "(" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i);
                continue;
            }

            if (text == "{")
            {
                return (i, -1);
            }

            if (text == ";")
            {
                return (-1, i);
            }
        }

        return (-1, -1);
    }

    private static void CapturePositionalParameters(ParsedSource parsed, int start, int limit, ClassSpan classSpan)
    {
        List<Token> tokens = parsed.Tokens;
        int open = -1;
        for (int i = start; i < limit; i++)
        {
            if (tokens[i].Text == "(")
            {
                open = i;
                break;
            }
        }

        if (open < 0)
        {
            return;
        }

        int close = parsed.ParenPartner[open];
        if (close < 0)
        {
            return;
        }

        int segmentStart = open + 1;
        for (int i = open + 1; i <= close; i++)
        {
            string text = tokens[i].Text;
            if (text is "(" or "[" or "{")
            {
                i = ParserUtilities.SkipPaired(parsed, i);
                continue;
            }

            if (i == close || (text == "," && i < close))
            {
                int nameIndex = FindLastNameToken(tokens, segmentStart, i - 1);
                if (nameIndex >= 0)
                {
                    ParserUtilities.AddUnique(classSpan.FieldNames, tokens[nameIndex].Text);
                }

                segmentStart = i + 1;
            }
        }
    }

    private static void ExtractClassMembers(ParsedSource parsed, ClassSpan classSpan, int classIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int i = classSpan.BodyStartIndex + 1;
        while (i < classSpan.EndIndex)
        {
            if (tokens[i].Text is ";" or "," or "}")
            {
                i++;
                continue;
            }

            if (tokens[i].Text is "(" or "[" or "{")
            {
                i = ParserUtilities.SkipPaired(parsed, i) + 1;
                continue;
            }

            int declarationStart = i;
            int boundary = FindMemberBoundary(parsed, declarationStart, classSpan.EndIndex);
            if (boundary < 0)
            {
                break;
            }

            if (tokens[boundary].Text == "{")
            {
                int bodyEnd = parsed.BracePartner[boundary];
                int openParen = FindLastTopLevelToken(parsed, "(", declarationStart, boundary - 1);
                if (openParen >= 0)
                {
                    int nameIndex = ParserUtilities.PreviousSignificant(tokens, openParen - 1);
                    if (nameIndex >= declarationStart && ParserUtilities.IsNameToken(tokens[nameIndex]))
                    {
                        FunctionSpan method = new(tokens[nameIndex].Text, nameIndex, boundary, bodyEnd, isMethod: true, classIndex);
                        classSpan.MethodFunctionIndexes.Add(parsed.Functions.Count);
                        parsed.Functions.Add(method);
                        i = bodyEnd + 1;
                        continue;
                    }
                }

                int propertyName = FindLastNameToken(tokens, declarationStart, boundary - 1);
                if (propertyName >= 0)
                {
                    ParserUtilities.AddUnique(classSpan.FieldNames, tokens[propertyName].Text);
                }

                i = bodyEnd + 1;
                continue;
            }

            // An expression-bodied member: `ReturnType Name(params) => expression;`.
            int arrow = FindFirstTopLevelToken(parsed, "=>", declarationStart, boundary - 1);
            if (arrow >= 0)
            {
                int arrowOpenParen = FindLastTopLevelToken(parsed, "(", declarationStart, arrow - 1);
                if (arrowOpenParen >= 0 && parsed.ParenPartner[arrowOpenParen] == arrow - 1)
                {
                    int methodName = ParserUtilities.PreviousSignificant(tokens, arrowOpenParen - 1);
                    if (methodName >= declarationStart && ParserUtilities.IsNameToken(tokens[methodName]))
                    {
                        FunctionSpan method = new(tokens[methodName].Text, methodName, arrow, boundary, isMethod: true, classIndex);
                        classSpan.MethodFunctionIndexes.Add(parsed.Functions.Count);
                        parsed.Functions.Add(method);
                        i = boundary + 1;
                        continue;
                    }
                }
            }

            // A `;`-terminated member shaped like `ReturnType Name(params);` is an
            // abstract or interface method declaration with no body.
            int abstractOpenParen = FindLastTopLevelToken(parsed, "(", declarationStart, boundary - 1);
            if (abstractOpenParen >= 0 && parsed.ParenPartner[abstractOpenParen] == boundary - 1)
            {
                int methodName = ParserUtilities.PreviousSignificant(tokens, abstractOpenParen - 1);
                if (methodName >= declarationStart && ParserUtilities.IsNameToken(tokens[methodName]))
                {
                    FunctionSpan declaration = new(tokens[methodName].Text, methodName, boundary, boundary, isMethod: true, classIndex);
                    classSpan.MethodFunctionIndexes.Add(parsed.Functions.Count);
                    parsed.Functions.Add(declaration);
                    i = boundary + 1;
                    continue;
                }
            }

            int fieldName = FindFieldName(tokens, declarationStart, boundary - 1);
            if (fieldName >= 0)
            {
                ParserUtilities.AddUnique(classSpan.FieldNames, tokens[fieldName].Text);
            }

            i = boundary + 1;
        }
    }

    private static int FindMemberBoundary(ParsedSource parsed, int startIndex, int classEnd)
    {
        for (int i = startIndex; i < classEnd; i++)
        {
            string text = parsed.Tokens[i].Text;
            if (text is "(" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i);
                continue;
            }

            if (text is "{" or ";")
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindFirstTopLevelToken(ParsedSource parsed, string text, int startIndex, int endIndex)
    {
        for (int i = startIndex; i <= endIndex; i++)
        {
            if (parsed.Tokens[i].Text == text)
            {
                return i;
            }

            if (parsed.Tokens[i].Text is "(" or "[")
            {
                int paired = ParserUtilities.SkipPaired(parsed, i);
                if (paired > i)
                {
                    i = paired;
                }
            }
        }

        return -1;
    }

    private static int FindLastTopLevelToken(ParsedSource parsed, string text, int startIndex, int endIndex)
    {
        int found = -1;
        for (int i = startIndex; i <= endIndex; i++)
        {
            if (parsed.Tokens[i].Text == text)
            {
                found = i;
            }

            if (parsed.Tokens[i].Text is "(" or "[")
            {
                int paired = ParserUtilities.SkipPaired(parsed, i);
                if (paired > i)
                {
                    i = paired;
                }
            }
        }

        return found;
    }

    private static int FindFieldName(List<Token> tokens, int startIndex, int endIndex)
    {
        int limit = endIndex;
        for (int i = startIndex; i <= endIndex; i++)
        {
            if (tokens[i].Text == "=")
            {
                limit = i - 1;
                break;
            }
        }

        return FindLastNameToken(tokens, startIndex, limit);
    }

    private static int FindLastNameToken(List<Token> tokens, int startIndex, int endIndex)
    {
        for (int i = endIndex; i >= startIndex; i--)
        {
            if (ParserUtilities.IsNameToken(tokens[i]) &&
                !ParserUtilities.IsMemberModifier(tokens[i].Text) &&
                !ParserUtilities.IsBuiltInType(tokens[i].Text))
            {
                return i;
            }
        }

        return -1;
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
}
