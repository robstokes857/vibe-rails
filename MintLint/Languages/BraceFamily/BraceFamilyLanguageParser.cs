using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class GoLanguageParser : BraceFamilyLanguageParser
{
    public override SourceLanguage Language => SourceLanguage.Go;

    public override IReadOnlyCollection<string> Extensions { get; } = [".go"];

    protected override BraceFamilyKind Kind => BraceFamilyKind.Go;
}

internal sealed class RustLanguageParser : BraceFamilyLanguageParser
{
    public override SourceLanguage Language => SourceLanguage.Rust;

    public override IReadOnlyCollection<string> Extensions { get; } = [".rs"];

    protected override BraceFamilyKind Kind => BraceFamilyKind.Rust;
}

internal sealed class CLanguageParser : BraceFamilyLanguageParser
{
    public override SourceLanguage Language => SourceLanguage.C;

    public override IReadOnlyCollection<string> Extensions { get; } = [".c", ".h"];

    protected override BraceFamilyKind Kind => BraceFamilyKind.C;
}

internal sealed class JavaLanguageParser : BraceFamilyLanguageParser
{
    public override SourceLanguage Language => SourceLanguage.Java;

    public override IReadOnlyCollection<string> Extensions { get; } = [".java"];

    protected override BraceFamilyKind Kind => BraceFamilyKind.Java;
}

internal sealed class CppLanguageParser : BraceFamilyLanguageParser
{
    public override SourceLanguage Language => SourceLanguage.Cpp;

    public override IReadOnlyCollection<string> Extensions { get; } =
        [".cc", ".cpp", ".cxx", ".c++", ".hh", ".hpp", ".hxx", ".h++", ".ixx", ".cppm"];

    protected override BraceFamilyKind Kind => BraceFamilyKind.Cpp;
}

internal enum BraceFamilyKind
{
    Go,
    Rust,
    C,
    Java,
    Cpp
}

internal abstract class BraceFamilyLanguageParser : ILanguageParser
{
    private static readonly HashSet<string> BodyIntroducers = new(StringComparer.Ordinal)
    {
        "catch", "do", "else", "finally", "for", "foreach", "if", "loop", "match",
        "select", "switch", "try", "while"
    };

    private static readonly HashSet<string> InvalidFunctionNames = new(StringComparer.Ordinal)
    {
        "case", "catch", "class", "default", "do", "else", "enum", "finally", "for",
        "foreach", "if", "impl", "interface", "match", "namespace", "record", "return",
        "select", "struct", "switch", "trait", "try", "union", "while"
    };

    private static readonly HashSet<string> FieldNameRejects = new(StringComparer.Ordinal)
    {
        "as", "case", "class", "const", "default", "delete", "else", "enum", "extends",
        "final", "for", "friend", "if", "implements", "import", "interface", "mut",
        "mutable", "namespace", "new", "noexcept", "operator", "package", "private",
        "protected", "public", "pub", "readonly", "record", "requires", "return",
        "static", "struct", "template", "throws", "type", "typedef", "typename", "union",
        "using", "virtual", "volatile"
    };

    public abstract SourceLanguage Language { get; }

    public abstract IReadOnlyCollection<string> Extensions { get; }

    protected abstract BraceFamilyKind Kind { get; }

    public ParsedSource Parse(string path, string displayPath, string source)
    {
        ParsedSource parsed = new(path, displayPath, source, Language, BraceLanguageTokenizer.Tokenize(source));
        Dictionary<int, string> methodOwners = new();
        List<ImplSpan> rustImpls = [];

        ExtractImports(parsed);
        ExtractTypes(parsed, rustImpls);
        ExtractFunctionsWithBodies(parsed, methodOwners, rustImpls);
        ExtractTypeMembers(parsed, methodOwners);
        ParserUtilities.AssignFunctionParents(parsed);
        AssignMethodsToTypes(parsed, methodOwners);

        return parsed;
    }

    private void ExtractImports(ParsedSource parsed)
    {
        switch (Kind)
        {
            case BraceFamilyKind.Go:
                ExtractGoImports(parsed);
                break;
            case BraceFamilyKind.Rust:
                ExtractRustImports(parsed);
                break;
            case BraceFamilyKind.C:
            case BraceFamilyKind.Cpp:
                ExtractCIncludes(parsed);
                break;
            case BraceFamilyKind.Java:
                ExtractJavaImports(parsed);
                break;
        }
    }

    private static void ExtractGoImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "import")
            {
                continue;
            }

            int next = ParserUtilities.NextSignificant(tokens, i + 1);
            if (next < 0)
            {
                continue;
            }

            if (tokens[next].Kind == TokenKind.String)
            {
                ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[next].Text));
            }
            else if (tokens[next].Text == "(")
            {
                int close = parsed.ParenPartner[next];
                for (int j = next + 1; j < close; j++)
                {
                    if (tokens[j].Kind == TokenKind.String)
                    {
                        ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[j].Text));
                    }
                }
            }
        }
    }

    private static void ExtractRustImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "use")
            {
                int end = FindStatementEnd(tokens, i + 1, tokens.Count - 1);
                ParserUtilities.AddUnique(parsed.ImportSources, ReadPath(tokens, i + 1, end));
            }
            else if (tokens[i].Text == "extern")
            {
                int crateIndex = ParserUtilities.NextSignificant(tokens, i + 1);
                int nameIndex = crateIndex >= 0 && tokens[crateIndex].Text == "crate"
                    ? ParserUtilities.NextSignificant(tokens, crateIndex + 1)
                    : -1;
                if (nameIndex >= 0 && ParserUtilities.IsNameToken(tokens[nameIndex]))
                {
                    ParserUtilities.AddUnique(parsed.ImportSources, tokens[nameIndex].Text);
                }
            }
            else if (tokens[i].Text == "mod")
            {
                int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
                if (nameIndex >= 0 && ParserUtilities.IsNameToken(tokens[nameIndex]))
                {
                    ParserUtilities.AddUnique(parsed.ImportSources, tokens[nameIndex].Text);
                }
            }
        }
    }

    private static void ExtractCIncludes(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Text == "#" && tokens[i + 1].Text == "include")
            {
                int next = ParserUtilities.NextSignificant(tokens, i + 2);
                if (next < 0)
                {
                    continue;
                }

                if (tokens[next].Kind == TokenKind.String)
                {
                    ParserUtilities.AddUnique(parsed.ImportSources, ParserUtilities.StringLiteralValue(tokens[next].Text));
                }
                else if (tokens[next].Text == "<")
                {
                    int end = next + 1;
                    while (end < tokens.Count && tokens[end].Text != ">")
                    {
                        end++;
                    }

                    ParserUtilities.AddUnique(parsed.ImportSources, JoinTokenText(tokens, next + 1, end - 1));
                }
            }
            else if (tokens[i].Text == "import")
            {
                int end = FindStatementEnd(tokens, i + 1, tokens.Count - 1);
                ParserUtilities.AddUnique(parsed.ImportSources, ReadPath(tokens, i + 1, end));
            }
        }
    }

    private static void ExtractJavaImports(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "import")
            {
                continue;
            }

            int start = ParserUtilities.NextSignificant(tokens, i + 1);
            if (start >= 0 && tokens[start].Text == "static")
            {
                start = ParserUtilities.NextSignificant(tokens, start + 1);
            }

            int end = FindStatementEnd(tokens, start, tokens.Count - 1);
            ParserUtilities.AddUnique(parsed.ImportSources, ReadPath(tokens, start, end));
        }
    }

    private void ExtractTypes(ParsedSource parsed, List<ImplSpan> rustImpls)
    {
        switch (Kind)
        {
            case BraceFamilyKind.Go:
                ExtractGoTypes(parsed);
                break;
            case BraceFamilyKind.Rust:
                ExtractRustTypesAndImpls(parsed, rustImpls);
                break;
            case BraceFamilyKind.C:
            case BraceFamilyKind.Java:
            case BraceFamilyKind.Cpp:
                ExtractCStyleTypes(parsed);
                break;
        }
    }

    private static void ExtractGoTypes(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "type")
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            if (nameIndex < 0 || !ParserUtilities.IsNameToken(tokens[nameIndex]))
            {
                continue;
            }

            int cursor = ParserUtilities.NextSignificant(tokens, nameIndex + 1);
            if (cursor >= 0 && tokens[cursor].Text == "[")
            {
                cursor = ParserUtilities.NextSignificant(tokens, ParserUtilities.SkipPaired(parsed, cursor) + 1);
            }

            if (cursor < 0 || tokens[cursor].Text is not "struct" and not "interface")
            {
                continue;
            }

            int bodyStart = ParserUtilities.NextSignificant(tokens, cursor + 1);
            if (bodyStart >= 0 && tokens[bodyStart].Text == "{")
            {
                int bodyEnd = parsed.BracePartner[bodyStart];
                if (bodyEnd >= 0)
                {
                    parsed.Classes.Add(new ClassSpan(tokens[nameIndex].Text, i, bodyStart, bodyEnd));
                    i = bodyEnd;
                }
            }
        }
    }

    private static void ExtractRustTypesAndImpls(ParsedSource parsed, List<ImplSpan> rustImpls)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text is "struct" or "enum" or "trait")
            {
                int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
                if (nameIndex < 0 || !ParserUtilities.IsNameToken(tokens[nameIndex]))
                {
                    continue;
                }

                (int bodyStart, int terminator) = FindDeclarationBody(parsed, nameIndex + 1);
                if (bodyStart >= 0)
                {
                    int bodyEnd = parsed.BracePartner[bodyStart];
                    if (bodyEnd >= 0)
                    {
                        parsed.Classes.Add(new ClassSpan(tokens[nameIndex].Text, i, bodyStart, bodyEnd));
                        i = bodyEnd;
                    }
                }
                else if (terminator >= 0)
                {
                    parsed.Classes.Add(new ClassSpan(tokens[nameIndex].Text, i, terminator, terminator));
                    i = terminator;
                }
            }
            else if (tokens[i].Text == "impl")
            {
                (int bodyStart, _) = FindDeclarationBody(parsed, i + 1);
                if (bodyStart >= 0)
                {
                    int bodyEnd = parsed.BracePartner[bodyStart];
                    string owner = ResolveRustImplOwner(tokens, i, bodyStart);
                    if (bodyEnd >= 0 && owner.Length > 0)
                    {
                        rustImpls.Add(new ImplSpan(owner, bodyStart, bodyEnd));
                        i = bodyEnd;
                    }
                }
            }
        }
    }

    private void ExtractCStyleTypes(ParsedSource parsed)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!IsCStyleTypeKeyword(tokens, i))
            {
                continue;
            }

            int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
            if (tokens[i].Text == "enum" && nameIndex >= 0 && tokens[nameIndex].Text is "class" or "struct")
            {
                nameIndex = ParserUtilities.NextSignificant(tokens, nameIndex + 1);
            }

            string name = nameIndex >= 0 && ParserUtilities.IsNameToken(tokens[nameIndex])
                ? tokens[nameIndex].Text
                : "anonymous";

            (int bodyStart, int terminator) = FindDeclarationBody(parsed, nameIndex >= 0 ? nameIndex + 1 : i + 1);
            if (bodyStart >= 0)
            {
                int bodyEnd = parsed.BracePartner[bodyStart];
                if (bodyEnd < 0)
                {
                    continue;
                }

                if (!IsLikelyCStyleTypeBody(tokens, i, nameIndex, bodyStart))
                {
                    continue;
                }

                if (name == "anonymous")
                {
                    int afterBody = ParserUtilities.NextSignificant(tokens, bodyEnd + 1);
                    if (afterBody >= 0 && ParserUtilities.IsNameToken(tokens[afterBody]))
                    {
                        name = tokens[afterBody].Text;
                    }
                }

                parsed.Classes.Add(new ClassSpan(name, i, bodyStart, bodyEnd));
                i = bodyEnd;
            }
            else if (terminator >= 0 &&
                name != "anonymous" &&
                IsLikelyCStyleForwardTypeDeclaration(tokens, nameIndex, terminator))
            {
                parsed.Classes.Add(new ClassSpan(name, i, terminator, terminator));
                i = terminator;
            }
        }
    }

    private bool IsCStyleTypeKeyword(List<Token> tokens, int index)
    {
        string text = tokens[index].Text;
        return Kind switch
        {
            BraceFamilyKind.C => text is "struct" or "union" or "enum",
            BraceFamilyKind.Java => text is "class" or "interface" or "enum" or "record",
            BraceFamilyKind.Cpp => text is "class" or "struct" or "union" or "enum",
            _ => false
        };
    }

    private bool IsLikelyCStyleTypeBody(List<Token> tokens, int keywordIndex, int nameIndex, int bodyStart)
    {
        if (nameIndex < 0 || tokens[keywordIndex].Text == "record")
        {
            return true;
        }

        if (Kind == BraceFamilyKind.C || Kind == BraceFamilyKind.Cpp)
        {
            for (int i = nameIndex + 1; i < bodyStart; i++)
            {
                if (tokens[i].Text == "(")
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsLikelyCStyleForwardTypeDeclaration(List<Token> tokens, int nameIndex, int terminator)
    {
        for (int i = nameIndex + 1; i < terminator; i++)
        {
            if (ParserUtilities.IsNameToken(tokens[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void ExtractFunctionsWithBodies(
        ParsedSource parsed,
        Dictionary<int, string> methodOwners,
        List<ImplSpan> rustImpls)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "{" || tokens[i].Kind != TokenKind.Punctuation)
            {
                continue;
            }

            int bodyEnd = parsed.BracePartner[i];
            if (bodyEnd < 0 ||
                !TryReadFunctionSignature(parsed, i, rustImpls, out FunctionSignature signature))
            {
                continue;
            }

            string name = tokens[signature.NameIndex].Text;
            int functionIndex = parsed.Functions.Count;
            parsed.Functions.Add(new FunctionSpan(
                name,
                signature.NameIndex,
                i,
                bodyEnd,
                signature.Owner is not null,
                classIndex: -1));

            if (signature.Owner is not null)
            {
                methodOwners[functionIndex] = signature.Owner;
            }
        }
    }

    private bool TryReadFunctionSignature(
        ParsedSource parsed,
        int bodyStart,
        List<ImplSpan> rustImpls,
        out FunctionSignature signature)
    {
        signature = default;
        if (LooksLikeNonFunctionBody(parsed, bodyStart))
        {
            return false;
        }

        List<Token> tokens = parsed.Tokens;
        int cursor = ParserUtilities.PreviousSignificant(tokens, bodyStart - 1);
        while (cursor >= 0)
        {
            string text = tokens[cursor].Text;
            if (text == ")" && parsed.ParenPartner[cursor] >= 0)
            {
                int parameterStart = parsed.ParenPartner[cursor];
                int nameIndex = ResolveNameBeforeParameters(parsed, parameterStart);
                if (nameIndex >= 0 && IsPotentialFunctionName(tokens[nameIndex]))
                {
                    if (Kind == BraceFamilyKind.Go && !HasGoFuncKeyword(parsed, nameIndex))
                    {
                        return false;
                    }

                    if (Kind == BraceFamilyKind.Rust && !HasRustFnKeyword(tokens, nameIndex))
                    {
                        return false;
                    }

                    if (!IsPlausibleFunctionNameContext(tokens, nameIndex))
                    {
                        return false;
                    }

                    string? owner = ResolveMethodOwner(parsed, rustImpls, nameIndex, bodyStart);
                    signature = new FunctionSignature(nameIndex, bodyStart, parsed.BracePartner[bodyStart], owner);
                    return true;
                }

                cursor = parameterStart - 1;
                continue;
            }

            if (text is ";" or "{" or "}")
            {
                break;
            }

            cursor--;
        }

        return false;
    }

    private string? ResolveMethodOwner(
        ParsedSource parsed,
        List<ImplSpan> rustImpls,
        int nameIndex,
        int bodyStart)
    {
        return Kind switch
        {
            BraceFamilyKind.Go => ResolveGoReceiverOwner(parsed, nameIndex),
            BraceFamilyKind.Rust => ResolveRustMethodOwner(rustImpls, bodyStart),
            BraceFamilyKind.Cpp => ResolveCppQualifierOwner(parsed.Tokens, nameIndex),
            _ => null
        };
    }

    private static string? ResolveGoReceiverOwner(ParsedSource parsed, int nameIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int receiverClose = ParserUtilities.PreviousSignificant(tokens, nameIndex - 1);
        if (receiverClose < 0 || tokens[receiverClose].Text != ")")
        {
            return null;
        }

        int receiverOpen = parsed.ParenPartner[receiverClose];
        int beforeReceiver = receiverOpen > 0 ? ParserUtilities.PreviousSignificant(tokens, receiverOpen - 1) : -1;
        if (beforeReceiver < 0 || tokens[beforeReceiver].Text != "func")
        {
            return null;
        }

        string owner = string.Empty;
        for (int i = receiverOpen + 1; i < receiverClose; i++)
        {
            if (tokens[i].Text == "[" && parsed.BracketPartner[i] > i)
            {
                i = parsed.BracketPartner[i];
                continue;
            }

            if (ParserUtilities.IsNameToken(tokens[i]) && !ParserUtilities.IsBuiltInType(tokens[i].Text))
            {
                owner = tokens[i].Text;
            }
        }

        return owner.Length == 0 ? null : owner;
    }

    private static string? ResolveRustMethodOwner(List<ImplSpan> rustImpls, int bodyStart)
    {
        string? owner = null;
        int width = int.MaxValue;
        foreach (ImplSpan impl in rustImpls)
        {
            if (bodyStart > impl.BodyStart && bodyStart < impl.End)
            {
                int candidateWidth = impl.End - impl.BodyStart;
                if (candidateWidth < width)
                {
                    owner = impl.Owner;
                    width = candidateWidth;
                }
            }
        }

        return owner;
    }

    private static string? ResolveCppQualifierOwner(List<Token> tokens, int nameIndex)
    {
        int previous = ParserUtilities.PreviousSignificant(tokens, nameIndex - 1);
        if (previous < 0 || tokens[previous].Text != "::")
        {
            return null;
        }

        int ownerIndex = ParserUtilities.PreviousSignificant(tokens, previous - 1);
        if (ownerIndex >= 0 && tokens[ownerIndex].Text == ">")
        {
            int openAngle = FindMatchingOpenAngleBack(tokens, ownerIndex);
            ownerIndex = openAngle > 0 ? ParserUtilities.PreviousSignificant(tokens, openAngle - 1) : -1;
        }

        return ownerIndex >= 0 && ParserUtilities.IsNameToken(tokens[ownerIndex])
            ? tokens[ownerIndex].Text
            : null;
    }

    private static bool HasGoFuncKeyword(ParsedSource parsed, int nameIndex)
    {
        List<Token> tokens = parsed.Tokens;
        int previous = ParserUtilities.PreviousSignificant(tokens, nameIndex - 1);
        if (previous >= 0 && tokens[previous].Text == "func")
        {
            return true;
        }

        if (previous >= 0 && tokens[previous].Text == ")")
        {
            int receiverOpen = parsed.ParenPartner[previous];
            int beforeReceiver = receiverOpen > 0 ? ParserUtilities.PreviousSignificant(tokens, receiverOpen - 1) : -1;
            return beforeReceiver >= 0 && tokens[beforeReceiver].Text == "func";
        }

        return false;
    }

    private static bool HasRustFnKeyword(List<Token> tokens, int nameIndex)
    {
        int previous = ParserUtilities.PreviousSignificant(tokens, nameIndex - 1);
        return previous >= 0 && tokens[previous].Text == "fn";
    }

    private static bool IsPlausibleFunctionNameContext(List<Token> tokens, int nameIndex)
    {
        int previous = ParserUtilities.PreviousSignificant(tokens, nameIndex - 1);
        if (previous < 0)
        {
            return true;
        }

        string text = tokens[previous].Text;
        if (text is "." or "->" or "new" or ":" or ",")
        {
            return false;
        }

        if (text is "class" or "enum" or "interface" or "record" or "struct" or "trait" or "union")
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeNonFunctionBody(ParsedSource parsed, int bodyStart)
    {
        List<Token> tokens = parsed.Tokens;
        int cursor = ParserUtilities.PreviousSignificant(tokens, bodyStart - 1);
        while (cursor >= 0)
        {
            string text = tokens[cursor].Text;
            if (text == ")" || text == "]")
            {
                int paired = text == ")" ? parsed.ParenPartner[cursor] : parsed.BracketPartner[cursor];
                cursor = paired > 0 ? ParserUtilities.PreviousSignificant(tokens, paired - 1) : cursor - 1;
                continue;
            }

            if (BodyIntroducers.Contains(text))
            {
                return true;
            }

            if (text is ";" or "{" or "}")
            {
                return false;
            }

            cursor--;
        }

        return false;
    }

    private static int ResolveNameBeforeParameters(ParsedSource parsed, int parameterStart)
    {
        List<Token> tokens = parsed.Tokens;
        int nameIndex = ParserUtilities.PreviousSignificant(tokens, parameterStart - 1);
        if (nameIndex >= 0 && tokens[nameIndex].Text == ">")
        {
            int openAngle = FindMatchingOpenAngleBack(tokens, nameIndex);
            if (openAngle >= 0)
            {
                nameIndex = ParserUtilities.PreviousSignificant(tokens, openAngle - 1);
            }
        }

        return nameIndex;
    }

    private static bool IsPotentialFunctionName(Token token)
    {
        return token.Kind == TokenKind.Identifier && !InvalidFunctionNames.Contains(token.Text);
    }

    private void ExtractTypeMembers(ParsedSource parsed, Dictionary<int, string> methodOwners)
    {
        foreach (ClassSpan classSpan in parsed.Classes)
        {
            switch (Kind)
            {
                case BraceFamilyKind.Go:
                    ExtractGoTypeMembers(parsed, classSpan, methodOwners);
                    break;
                case BraceFamilyKind.Rust:
                    ExtractRustTypeMembers(parsed, classSpan, methodOwners);
                    break;
                case BraceFamilyKind.C:
                case BraceFamilyKind.Java:
                case BraceFamilyKind.Cpp:
                    ExtractCStyleTypeMembers(parsed, classSpan, methodOwners);
                    break;
            }
        }
    }

    private static void ExtractGoTypeMembers(
        ParsedSource parsed,
        ClassSpan classSpan,
        Dictionary<int, string> methodOwners)
    {
        List<Token> tokens = parsed.Tokens;
        int i = classSpan.BodyStartIndex + 1;
        while (i < classSpan.EndIndex)
        {
            int line = tokens[i].Line;
            int lineEnd = i;
            while (lineEnd + 1 < classSpan.EndIndex && tokens[lineEnd + 1].Line == line)
            {
                lineEnd++;
            }

            int firstName = FindFirstNameToken(tokens, i, lineEnd);
            int firstParen = FindFirstToken(tokens, "(", i, lineEnd);
            if (firstName >= 0 && firstParen > firstName)
            {
                int functionIndex = parsed.Functions.Count;
                parsed.Functions.Add(new FunctionSpan(
                    tokens[firstName].Text,
                    firstName,
                    lineEnd,
                    lineEnd,
                    isMethod: true,
                    classIndex: -1));
                methodOwners[functionIndex] = classSpan.Name;
            }
            else if (firstName >= 0)
            {
                ParserUtilities.AddUnique(classSpan.FieldNames, tokens[firstName].Text);
            }

            i = lineEnd + 1;
        }
    }

    private static void ExtractRustTypeMembers(
        ParsedSource parsed,
        ClassSpan classSpan,
        Dictionary<int, string> methodOwners)
    {
        List<Token> tokens = parsed.Tokens;
        string declaration = tokens[classSpan.StartIndex].Text;
        if (declaration == "trait")
        {
            for (int i = classSpan.BodyStartIndex + 1; i < classSpan.EndIndex; i++)
            {
                if (tokens[i].Text != "fn")
                {
                    continue;
                }

                int nameIndex = ParserUtilities.NextSignificant(tokens, i + 1);
                if (nameIndex < 0 || !IsPotentialFunctionName(tokens[nameIndex]))
                {
                    continue;
                }

                int terminator = FindStatementEnd(tokens, nameIndex + 1, classSpan.EndIndex - 1);
                if (terminator < 0 || tokens[terminator].Text != ";")
                {
                    continue;
                }

                int functionIndex = parsed.Functions.Count;
                parsed.Functions.Add(new FunctionSpan(
                    tokens[nameIndex].Text,
                    nameIndex,
                    terminator,
                    terminator,
                    isMethod: true,
                    classIndex: -1));
                methodOwners[functionIndex] = classSpan.Name;
            }

            return;
        }

        if (declaration != "struct")
        {
            return;
        }

        int segmentStart = classSpan.BodyStartIndex + 1;
        for (int i = segmentStart; i <= classSpan.EndIndex; i++)
        {
            if (i == classSpan.EndIndex || tokens[i].Text == ",")
            {
                int colon = FindFirstToken(tokens, ":", segmentStart, i - 1);
                if (colon > segmentStart)
                {
                    int nameIndex = ParserUtilities.PreviousSignificant(tokens, colon - 1);
                    if (nameIndex >= segmentStart && ParserUtilities.IsNameToken(tokens[nameIndex]))
                    {
                        ParserUtilities.AddUnique(classSpan.FieldNames, tokens[nameIndex].Text);
                    }
                }

                segmentStart = i + 1;
            }
        }
    }

    private static void ExtractCStyleTypeMembers(
        ParsedSource parsed,
        ClassSpan classSpan,
        Dictionary<int, string> methodOwners)
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

            if (i + 1 < classSpan.EndIndex &&
                (tokens[i].Text is "public" or "private" or "protected") &&
                tokens[i + 1].Text == ":")
            {
                i += 2;
                continue;
            }

            int boundary = FindMemberBoundary(parsed, i, classSpan.EndIndex);
            if (boundary < 0)
            {
                break;
            }

            if (tokens[boundary].Text == "{")
            {
                int bodyEnd = parsed.BracePartner[boundary];
                i = bodyEnd >= 0 ? bodyEnd + 1 : boundary + 1;
                continue;
            }

            if (TryAddMethodDeclaration(parsed, classSpan, i, boundary, methodOwners))
            {
                i = boundary + 1;
                continue;
            }

            AddFieldNamesFromDeclaration(tokens, classSpan, i, boundary - 1);
            i = boundary + 1;
        }
    }

    private static bool TryAddMethodDeclaration(
        ParsedSource parsed,
        ClassSpan classSpan,
        int start,
        int semicolon,
        Dictionary<int, string> methodOwners)
    {
        List<Token> tokens = parsed.Tokens;
        int openParen = FindLastTopLevelToken(parsed, "(", start, semicolon - 1);
        if (openParen < 0)
        {
            return false;
        }

        int closeParen = parsed.ParenPartner[openParen];
        if (closeParen < openParen || closeParen > semicolon)
        {
            return false;
        }

        int nameIndex = ResolveNameBeforeParameters(parsed, openParen);
        if (nameIndex < start || !IsPotentialFunctionName(tokens[nameIndex]))
        {
            return false;
        }

        int functionIndex = parsed.Functions.Count;
        parsed.Functions.Add(new FunctionSpan(
            tokens[nameIndex].Text,
            nameIndex,
            semicolon,
            semicolon,
            isMethod: true,
            classIndex: -1));
        methodOwners[functionIndex] = classSpan.Name;
        return true;
    }

    private static void AddFieldNamesFromDeclaration(List<Token> tokens, ClassSpan classSpan, int start, int end)
    {
        int segmentStart = start;
        for (int i = start; i <= end + 1; i++)
        {
            if (i == end + 1 || tokens[i].Text == ",")
            {
                int limit = i - 1;
                for (int j = segmentStart; j <= limit; j++)
                {
                    if (tokens[j].Text == "=")
                    {
                        limit = j - 1;
                        break;
                    }
                }

                int nameIndex = FindLastFieldName(tokens, segmentStart, limit);
                if (nameIndex >= 0)
                {
                    ParserUtilities.AddUnique(classSpan.FieldNames, tokens[nameIndex].Text);
                }

                segmentStart = i + 1;
            }
        }
    }

    private static void AssignMethodsToTypes(ParsedSource parsed, Dictionary<int, string> methodOwners)
    {
        Dictionary<string, int> typeIndexes = new(StringComparer.Ordinal);
        for (int i = 0; i < parsed.Classes.Count; i++)
        {
            if (!typeIndexes.ContainsKey(parsed.Classes[i].Name))
            {
                typeIndexes.Add(parsed.Classes[i].Name, i);
            }
        }

        for (int functionIndex = 0; functionIndex < parsed.Functions.Count; functionIndex++)
        {
            if (methodOwners.TryGetValue(functionIndex, out string? owner) &&
                typeIndexes.TryGetValue(owner, out int ownerIndex))
            {
                AddMethodIndex(parsed.Classes[ownerIndex], functionIndex);
                continue;
            }

            int containingType = FindContainingType(parsed, parsed.Functions[functionIndex]);
            if (containingType >= 0)
            {
                AddMethodIndex(parsed.Classes[containingType], functionIndex);
            }
        }
    }

    private static int FindContainingType(ParsedSource parsed, FunctionSpan function)
    {
        int bestIndex = -1;
        int bestWidth = int.MaxValue;
        for (int i = 0; i < parsed.Classes.Count; i++)
        {
            ClassSpan candidate = parsed.Classes[i];
            if (function.StartIndex > candidate.BodyStartIndex && function.EndIndex <= candidate.EndIndex)
            {
                int width = candidate.EndIndex - candidate.BodyStartIndex;
                if (width < bestWidth)
                {
                    bestIndex = i;
                    bestWidth = width;
                }
            }
        }

        return bestIndex;
    }

    private static void AddMethodIndex(ClassSpan classSpan, int functionIndex)
    {
        foreach (int existing in classSpan.MethodFunctionIndexes)
        {
            if (existing == functionIndex)
            {
                return;
            }
        }

        classSpan.MethodFunctionIndexes.Add(functionIndex);
    }

    private static (int BodyStart, int Terminator) FindDeclarationBody(ParsedSource parsed, int start)
    {
        List<Token> tokens = parsed.Tokens;
        for (int i = Math.Max(0, start); i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (text is "(" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i);
                continue;
            }

            if (text == "<")
            {
                int closeAngle = FindMatchingCloseAngle(tokens, i, tokens.Count - 1);
                if (closeAngle > i)
                {
                    i = closeAngle;
                    continue;
                }
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

    private static int FindMemberBoundary(ParsedSource parsed, int start, int classEnd)
    {
        for (int i = start; i < classEnd; i++)
        {
            string text = parsed.Tokens[i].Text;
            if (text is "(" or "[")
            {
                i = ParserUtilities.SkipPaired(parsed, i);
                continue;
            }

            if (text == "<")
            {
                int closeAngle = FindMatchingCloseAngle(parsed.Tokens, i, classEnd - 1);
                if (closeAngle > i)
                {
                    i = closeAngle;
                    continue;
                }
            }

            if (text is "{" or ";")
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLastTopLevelToken(ParsedSource parsed, string text, int start, int end)
    {
        int found = -1;
        for (int i = Math.Max(0, start); i <= end && i < parsed.Tokens.Count; i++)
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

    private static int FindStatementEnd(List<Token> tokens, int start, int end)
    {
        for (int i = Math.Max(0, start); i <= end && i < tokens.Count; i++)
        {
            if (tokens[i].Text == ";")
            {
                return i;
            }

            if (i > start && tokens[i].Line != tokens[start].Line)
            {
                return i - 1;
            }
        }

        return Math.Min(end, tokens.Count - 1);
    }

    private static int FindFirstNameToken(List<Token> tokens, int start, int end)
    {
        for (int i = Math.Max(0, start); i <= end && i < tokens.Count; i++)
        {
            if (ParserUtilities.IsNameToken(tokens[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindFirstToken(List<Token> tokens, string text, int start, int end)
    {
        for (int i = Math.Max(0, start); i <= end && i < tokens.Count; i++)
        {
            if (tokens[i].Text == text)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLastFieldName(List<Token> tokens, int start, int end)
    {
        for (int i = Math.Min(end, tokens.Count - 1); i >= start && i >= 0; i--)
        {
            if (ParserUtilities.IsNameToken(tokens[i]) &&
                !ParserUtilities.IsMemberModifier(tokens[i].Text) &&
                !ParserUtilities.IsBuiltInType(tokens[i].Text) &&
                !FieldNameRejects.Contains(tokens[i].Text))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindMatchingCloseAngle(List<Token> tokens, int openAngle, int end)
    {
        int depth = 0;
        for (int i = openAngle; i <= end && i < tokens.Count; i++)
        {
            if (tokens[i].Text == "<")
            {
                depth++;
            }
            else if (tokens[i].Text == ">")
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

    private static int FindMatchingOpenAngleBack(List<Token> tokens, int closeAngle)
    {
        int depth = 0;
        for (int i = closeAngle; i >= 0; i--)
        {
            if (tokens[i].Text == ">")
            {
                depth++;
            }
            else if (tokens[i].Text == "<")
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

    private static string ResolveRustImplOwner(List<Token> tokens, int implIndex, int bodyStart)
    {
        int forIndex = -1;
        for (int i = implIndex + 1; i < bodyStart; i++)
        {
            if (tokens[i].Text == "for")
            {
                forIndex = i;
            }
        }

        int start = forIndex >= 0 ? forIndex + 1 : implIndex + 1;
        if (start < bodyStart && tokens[start].Text == "<")
        {
            int genericEnd = FindMatchingCloseAngle(tokens, start, bodyStart - 1);
            if (genericEnd > start)
            {
                start = genericEnd + 1;
            }
        }

        for (int i = start; i < bodyStart; i++)
        {
            if (ParserUtilities.IsNameToken(tokens[i]) && !ParserUtilities.IsBuiltInType(tokens[i].Text))
            {
                return tokens[i].Text;
            }

            if (tokens[i].Text == "<")
            {
                int genericEnd = FindMatchingCloseAngle(tokens, i, bodyStart - 1);
                if (genericEnd > i)
                {
                    i = genericEnd;
                }
            }
        }

        return string.Empty;
    }

    private static string ReadPath(List<Token> tokens, int start, int end)
    {
        return JoinTokenText(tokens, start, end).TrimEnd(';');
    }

    private static string JoinTokenText(List<Token> tokens, int start, int end)
    {
        if (start < 0 || end < start || start >= tokens.Count)
        {
            return string.Empty;
        }

        end = Math.Min(end, tokens.Count - 1);
        string result = string.Empty;
        for (int i = start; i <= end; i++)
        {
            string text = tokens[i].Text;
            if (text == ";")
            {
                break;
            }

            result += text;
        }

        return result;
    }

    private readonly record struct FunctionSignature(int NameIndex, int BodyStart, int End, string? Owner);

    private readonly record struct ImplSpan(string Owner, int BodyStart, int End);
}
