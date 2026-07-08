using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class JavaScriptLanguageParser : EcmaScriptLanguageParser
{
    public override SourceLanguage Language => SourceLanguage.JavaScript;

    public override IReadOnlyCollection<string> Extensions { get; } =
        [".js", ".jsx", ".mjs", ".cjs"];
}

internal sealed class TypeScriptLanguageParser : EcmaScriptLanguageParser
{
    public override SourceLanguage Language => SourceLanguage.TypeScript;

    public override IReadOnlyCollection<string> Extensions { get; } =
        [".ts", ".tsx", ".mts", ".cts"];
}

internal abstract class EcmaScriptLanguageParser : ILanguageParser
{
    public abstract SourceLanguage Language { get; }

    public abstract IReadOnlyCollection<string> Extensions { get; }

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, BraceLanguageTokenizer.Tokenize(source));
        ExtractImports(parsed);
        ExtractClasses(parsed);
        ExtractFunctionDeclarations(parsed);
        ExtractArrowFunctions(parsed);
        ParserUtilities.AssignFunctionParents(parsed);
        return parsed;
    }

    private static void ExtractImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "import")
            {
                continue;
            }

            int next = ParserUtilities.NextSignificant(tokens, i + 1);
            if (next >= 0 && tokens[next].Text == "(")
            {
                int argument = ParserUtilities.NextSignificant(tokens, next + 1);
                if (argument >= 0 && tokens[argument].Kind == TokenKind.String)
                {
                    ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[argument].Text));
                }

                continue;
            }

            int statementEnd = FindStatementEnd(parsed, i + 1, tokens.Count - 1);
            for (int j = i + 1; j <= statementEnd; j++)
            {
                if (tokens[j].Text == "from")
                {
                    int source = ParserUtilities.NextSignificant(tokens, j + 1);
                    if (source >= 0 && tokens[source].Kind == TokenKind.String)
                    {
                        ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[source].Text));
                    }

                    break;
                }

                if (tokens[j].Kind == TokenKind.String && j == ParserUtilities.NextSignificant(tokens, i + 1))
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
            if (tokens[i].Text != "class")
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            string name = nameIndex >= 0 && ParserUtilities.IsNameToken(tokens[nameIndex]) ? tokens[nameIndex].Text : "anonymous";
            int bodyStart = ParserUtilities.FindNextToken(tokens, "{", nameIndex >= 0 ? nameIndex + 1 : i + 1);
            if (bodyStart < 0)
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd < 0)
            {
                continue;
            }

            int classIndex = parsed.Classes.Count;
            ClassSpan span = new(name, i, bodyStart, bodyEnd);
            parsed.Classes.Add(span);
            ExtractClassMembers(parsed, span, classIndex);
            i = bodyEnd;
        }
    }

    private static void ExtractClassMembers(ParsedSource parsed, ClassSpan classSpan, int classIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int i = classSpan.BodyStartIndex + 1;
        while (i < classSpan.EndIndex)
        {
            if (tokens[i].Text is ";" or ",")
            {
                i++;
                continue;
            }

            if (tokens[i].Text is "{" or "(" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i) + 1;
                continue;
            }

            int memberStart = i;
            while (memberStart < classSpan.EndIndex && ParserUtilities.IsMemberModifier(tokens[memberStart].Text))
            {
                memberStart++;
            }

            if (memberStart >= classSpan.EndIndex || !ParserUtilities.IsNameToken(tokens[memberStart]))
            {
                i++;
                continue;
            }

            int nameIndex = memberStart;
            string memberName = tokens[nameIndex].Text;
            int afterName = ParserUtilities.NextSignificant(tokens, nameIndex + 1);

            if (afterName >= 0 && tokens[afterName].Text == "(")
            {
                int closeParen = parsed.ParenPartner[afterName];
                int bodyStart = closeParen >= 0 ? FindBodyStart(parsed, closeParen + 1) : -1;
                if (bodyStart >= 0)
                {
                    int bodyEnd = parsed.BracePartner[bodyStart];
                    if (bodyEnd >= 0)
                    {
                        FunctionSpan method = new(memberName, nameIndex, bodyStart, bodyEnd, isMethod: true, classIndex);
                        classSpan.MethodFunctionIndexes.Add(parsed.Functions.Count);
                        parsed.Functions.Add(method);
                        i = bodyEnd + 1;
                        continue;
                    }
                }
            }

            if (afterName >= 0 && IsFieldDelimiter(tokens[afterName].Text))
            {
                ParserUtilities.AddUnique(classSpan.FieldNames, memberName);
                i = SkipMemberDeclaration(parsed, afterName, classSpan.EndIndex);
                continue;
            }

            i++;
        }
    }

    private static void ExtractFunctionDeclarations(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "function")
            {
                continue;
            }

            int cursor = ParserUtilities.NextSignificant(tokens, i + 1);
            if (cursor >= 0 && tokens[cursor].Text == "*")
            {
                cursor = ParserUtilities.NextSignificant(tokens, cursor + 1);
            }

            string? explicitName = null;
            if (cursor >= 0 && ParserUtilities.IsNameToken(tokens[cursor]))
            {
                explicitName = tokens[cursor].Text;
                cursor = ParserUtilities.NextSignificant(tokens, cursor + 1);
            }

            int parameterStart = cursor >= 0 && tokens[cursor].Text == "("
                ? cursor
                : ParserUtilities.FindNextToken(tokens, "(", i + 1);
            if (parameterStart < 0)
            {
                continue;
            }

            int parameterEnd = parsed.ParenPartner[parameterStart];
            int bodyStart = parameterEnd >= 0 ? FindBodyStart(parsed, parameterEnd + 1) : -1;
            if (bodyStart < 0)
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[bodyStart];
            if (bodyEnd < 0)
            {
                continue;
            }

            string name = explicitName ?? ResolveContextualName(parsed, i, i) ?? "anonymous";
            parsed.Functions.Add(new FunctionSpan(name, i, bodyStart, bodyEnd, isMethod: false, classIndex: -1));
        }
    }

    private static int FindBodyStart(ParsedSource parsed, int afterParameters)
    {
        // A TypeScript signature may carry a return-type annotation between the parameter
        // list and the body (`f(): Map<string, number> {`). The annotation is skipped by
        // scanning for the body's `{` while ignoring anything nested in parentheses,
        // brackets, or generic angle brackets. Returns -1 when no block body follows
        // (an overload signature, an abstract member, or an expression arrow handled
        // elsewhere).
        List<Token> tokens = parsed.Tokens;
        int cursor = ParserUtilities.NextSignificant(tokens, afterParameters);
        if (cursor < 0)
        {
            return -1;
        }

        if (tokens[cursor].Text == "{")
        {
            return cursor;
        }

        if (tokens[cursor].Text != ":")
        {
            return -1;
        }

        int angleDepth = 0;
        for (int i = cursor + 1; i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (text is "(" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i);
                continue;
            }

            if (text == "<")
            {
                angleDepth++;
                continue;
            }

            if (text is ">" or ">>" or ">>>")
            {
                angleDepth = Math.Max(0, angleDepth - text.Length);
                continue;
            }

            if (angleDepth > 0)
            {
                continue;
            }

            if (text == "{")
            {
                // Either the body or an object-literal type. It is only a type when the
                // annotation continues (a union/intersection) or the real body follows
                // the matching close brace.
                int close = parsed.BracePartner[i];
                if (close < 0)
                {
                    return -1;
                }

                int next = ParserUtilities.NextSignificant(tokens, close + 1);
                if (next >= 0 && tokens[next].Text is "{" or "|" or "&")
                {
                    i = close;
                    continue;
                }

                return i;
            }

            if (text is ";" or "," or ")" or "]" or "}")
            {
                return -1;
            }
        }

        return -1;
    }

    private static void ExtractArrowFunctions(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "=>")
            {
                continue;
            }

            int bodyStart = ParserUtilities.NextSignificant(tokens, i + 1);
            if (bodyStart < 0)
            {
                continue;
            }

            int startIndex = ResolveArrowStart(parsed, i);
            if (LooksLikeTypeOnlyArrow(parsed, startIndex, i))
            {
                continue;
            }

            int endIndex;
            if (tokens[bodyStart].Text == "{")
            {
                endIndex = parsed.BracePartner[bodyStart];
                if (endIndex < 0)
                {
                    continue;
                }
            }
            else
            {
                endIndex = FindExpressionArrowEnd(parsed, bodyStart);
            }

            string name = ResolveContextualName(parsed, startIndex, i) ?? "anonymous";
            parsed.Functions.Add(new FunctionSpan(name, startIndex, bodyStart, endIndex, isMethod: false, classIndex: -1));
        }
    }

    private static string? ResolveContextualName(ParsedSource parsed, int startIndex, int arrowOrFunctionIndex)
    {
        string? assigned = ResolveAssignedName(parsed, startIndex);
        if (assigned is not null)
        {
            return assigned;
        }

        string? callContext = ResolveCallContext(parsed, startIndex, arrowOrFunctionIndex);
        return callContext is null ? null : "anonymous: " + callContext;
    }

    private static string? ResolveAssignedName(ParsedSource parsed, int startIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int limit = Math.Max(0, startIndex - 24);
        for (int i = startIndex - 1; i >= limit; i--)
        {
            string text = tokens[i].Text;
            if (text is ";" or "{" or "}")
            {
                break;
            }

            if (text is "=" or ":")
            {
                for (int j = i - 1; j >= limit; j--)
                {
                    if (ParserUtilities.IsNameToken(tokens[j]) && !IsDeclarationKeyword(tokens[j].Text))
                    {
                        return tokens[j].Text;
                    }

                    if (tokens[j].Text is ";" or "{" or "}" or ",")
                    {
                        break;
                    }
                }
            }
        }

        return null;
    }

    private static string? ResolveCallContext(ParsedSource parsed, int startIndex, int arrowOrFunctionIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int bestOpen = -1;
        for (int i = startIndex - 1; i >= 0; i--)
        {
            if (tokens[i].Text == "(" && parsed.ParenPartner[i] >= arrowOrFunctionIndex)
            {
                bestOpen = i;
                break;
            }

            if (tokens[i].Text is ";" or "{" or "}")
            {
                break;
            }
        }

        if (bestOpen < 1)
        {
            return null;
        }

        int calleeIndex = ParserUtilities.PreviousSignificant(tokens, bestOpen - 1);
        if (calleeIndex < 0)
        {
            return null;
        }

        if (tokens[calleeIndex].Text == ")")
        {
            int calleeOpen = parsed.ParenPartner[calleeIndex];
            calleeIndex = calleeOpen > 0 ? ParserUtilities.PreviousSignificant(tokens, calleeOpen - 1) : -1;
        }

        return calleeIndex >= 0 && ParserUtilities.IsNameToken(tokens[calleeIndex]) ? tokens[calleeIndex].Text : null;
    }

    private static int ResolveArrowStart(ParsedSource parsed, int arrowIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int previous = ParserUtilities.PreviousSignificant(tokens, arrowIndex - 1);
        if (previous >= 0 && tokens[previous].Text == ")")
        {
            int open = parsed.ParenPartner[previous];
            if (open >= 0)
            {
                return open;
            }
        }

        return previous >= 0 ? previous : arrowIndex;
    }

    private static bool LooksLikeTypeOnlyArrow(ParsedSource parsed, int startIndex, int arrowIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int limit = Math.Max(0, startIndex - 16);
        for (int i = startIndex - 1; i >= limit; i--)
        {
            if (tokens[i].Text is ";" or "{" or "}")
            {
                break;
            }

            if (tokens[i].Text == "type")
            {
                return true;
            }
        }

        int next = ParserUtilities.NextSignificant(tokens, arrowIndex + 1);
        return next >= 0 && tokens[next].Text is "void" or "never" or "unknown";
    }

    private static int FindExpressionArrowEnd(ParsedSource parsed, int bodyStart)
    {
        List<Token> tokens = parsed.Tokens;
        int i = bodyStart;
        while (i < tokens.Count)
        {
            string text = tokens[i].Text;
            if (text is "(" or "{" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i) + 1;
                continue;
            }

            if (text is ";" or ",")
            {
                return Math.Max(bodyStart, i - 1);
            }

            if (text is ")" or "}" or "]")
            {
                return Math.Max(bodyStart, i - 1);
            }

            i++;
        }

        return tokens.Count - 1;
    }

    private static int SkipMemberDeclaration(ParsedSource parsed, int startIndex, int classEnd)
    {
        List<Token> tokens = parsed.Tokens;
        int i = startIndex;
        while (i < classEnd)
        {
            string text = tokens[i].Text;
            if (text is "(" or "{" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i) + 1;
                continue;
            }

            if (text == ";")
            {
                return i + 1;
            }

            i++;
        }

        return classEnd;
    }

    private static int FindStatementEnd(ParsedSource parsed, int start, int end)
    {
        int i = Math.Max(0, start);
        while (i <= end && i < parsed.Tokens.Count)
        {
            string text = parsed.Tokens[i].Text;
            if (text is "(" or "{" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i) + 1;
                continue;
            }

            if (text == ";")
            {
                return i;
            }

            if (text is "}" or ")")
            {
                return Math.Max(start, i - 1);
            }

            i++;
        }

        return Math.Min(end, parsed.Tokens.Count - 1);
    }

    private static bool IsFieldDelimiter(string text)
    {
        return text is ":" or "=" or ";" or "!" or "?";
    }

    private static bool IsDeclarationKeyword(string text)
    {
        return text is "const" or "let" or "var" or "export" or "default" or "return";
    }
}
