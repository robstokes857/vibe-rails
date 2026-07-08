using System;
using System.Collections.Generic;

namespace MintLint;

internal static class MetricEngine
{
    private const int NPathCap = 1_000_000_000;

    private static readonly HashSet<string> OperandKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "base", "false", "nil", "null", "nullptr", "self",
        "super", "this", "true", "undefined"
    };

    private static readonly HashSet<string> OperatorKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "assert", "async", "await", "break", "case", "catch",
        "chan", "checked", "class", "const", "constexpr", "continue", "crate", "defer",
        "def", "default", "delegate", "delete", "del", "do", "dyn", "elif", "else",
        "enum", "event", "except", "explicit", "export", "extends", "extern",
        "fallthrough", "finally", "fixed", "fn", "for", "foreach", "friend", "from",
        "func", "function", "global", "go", "goto", "if", "impl", "implicit", "import",
        "implements", "in", "include", "inline", "instanceof", "interface", "internal",
        "is", "lambda", "let", "lock", "loop", "map", "match", "mod", "move", "mut",
        "namespace", "new", "noexcept", "nonlocal", "not", "of", "operator", "or", "out",
        "override", "package", "params", "pass", "private", "protected", "public", "pub",
        "raise", "range", "readonly", "record", "ref", "required", "requires", "return",
        "sealed", "select", "sizeof", "stackalloc", "static", "struct", "switch",
        "synchronized", "template", "throw", "throws", "trait", "try", "type", "typedef",
        "typename", "typeof", "unchecked", "union", "unsafe", "use", "using", "var",
        "virtual", "void", "volatile", "where", "while", "with", "yield"
    };

    public static FileMetrics BuildFileMetrics(ParsedSource parsed, double duplicationRatio, MintLintOptions options)
    {
        int loc = CountLoc(parsed.Text);
        List<FunctionMetrics> functions = [];

        int globalCyclomatic = CountDecisionPoints(parsed, 0, parsed.Tokens.Count - 1, currentFunctionIndex: -1, baseComplexity: 0);
        int globalCognitive = ComputeCognitive(parsed, 0, parsed.Tokens.Count - 1, currentFunctionIndex: -1);
        int globalNPath = ComputeNPath(parsed, 0, parsed.Tokens.Count - 1, currentFunctionIndex: -1);
        functions.Add(new FunctionMetrics(
            "global",
            0,
            Math.Max(loc, 1),
            globalCyclomatic,
            globalCognitive,
            globalNPath,
            0.0,
            0.0));

        int maxNestingDepth = ComputeMaxNestingDepth(parsed, 0, parsed.Tokens.Count - 1, currentFunctionIndex: -1);
        int maxParameterCount = 0;

        for (int i = 0; i < parsed.Functions.Count; i++)
        {
            FunctionSpan span = parsed.Functions[i];
            int cyclomatic = CountDecisionPoints(parsed, span.BodyStartIndex, span.EndIndex, i, baseComplexity: 1);
            int cognitive = ComputeCognitive(parsed, span.BodyStartIndex + 1, span.EndIndex - 1, i);
            int npath = ComputeNPath(parsed, span.BodyStartIndex + 1, span.EndIndex - 1, i);
            (double hVolume, double hDifficulty) = ComputeHalstead(parsed, span.StartIndex, span.EndIndex);
            int startLine = parsed.Tokens[span.StartIndex].Line;
            int endLine = parsed.Tokens[span.EndIndex].Line;

            maxNestingDepth = Math.Max(maxNestingDepth, ComputeMaxNestingDepth(parsed, span.BodyStartIndex, span.EndIndex, i));
            maxParameterCount = Math.Max(maxParameterCount, ComputeParameterCount(parsed, span));

            functions.Add(new FunctionMetrics(
                span.Name,
                startLine,
                Math.Max(1, endLine - startLine + 1),
                cyclomatic,
                cognitive,
                npath,
                hVolume,
                hDifficulty));
        }

        functions.Sort(static (left, right) =>
        {
            int cyclo = right.Cyclomatic.CompareTo(left.Cyclomatic);
            if (cyclo != 0)
            {
                return cyclo;
            }

            int global = (left.Name == "global").CompareTo(right.Name == "global");
            if (global != 0)
            {
                return global;
            }

            int line = left.Line.CompareTo(right.Line);
            return line != 0 ? line : string.CompareOrdinal(left.Name, right.Name);
        });

        List<ClassMetrics> classes = BuildClassMetrics(parsed);
        int fanOut = parsed.ImportSources.Count;

        int cyclomaticSum = 0;
        int cognitiveSum = 0;
        int npathMax = 1;
        int highestCyclomatic = 0;
        foreach (FunctionMetrics function in functions)
        {
            cyclomaticSum += function.Cyclomatic;
            cognitiveSum += function.Cognitive;
            npathMax = Math.Max(npathMax, function.NPath);
            highestCyclomatic = Math.Max(highestCyclomatic, function.Cyclomatic);
        }

        (double fileHalsteadVolume, _) = ComputeHalstead(parsed, 0, parsed.Tokens.Count - 1);
        double maintainabilityIndex = ComputeMaintainabilityIndex(fileHalsteadVolume, cyclomaticSum, loc);
        (int hardCodedDependencies, int ambientDependencies) = TestabilityAnalyzer.Analyze(parsed);

        return new FileMetrics(
            parsed.DisplayPath,
            loc,
            functions,
            classes,
            fanOut,
            duplicationRatio,
            cyclomaticSum,
            cognitiveSum,
            npathMax,
            fileHalsteadVolume,
            maintainabilityIndex,
            ClassifyComplexity(highestCyclomatic, options.WarningThreshold, options.ErrorThreshold),
            maxNestingDepth,
            maxParameterCount,
            hardCodedDependencies,
            ambientDependencies);
    }

    public static string ClassifyComplexity(int complexity, int warningThreshold, int errorThreshold)
    {
        if (complexity >= errorThreshold)
        {
            return "error";
        }

        return complexity >= warningThreshold ? "warning" : "ok";
    }

    public static FileMetrics Reclassify(FileMetrics file, int warningThreshold, int errorThreshold)
    {
        int highest = 0;
        foreach (FunctionMetrics function in file.Functions)
        {
            highest = Math.Max(highest, function.Cyclomatic);
        }

        return file with { ComplexityLevel = ClassifyComplexity(highest, warningThreshold, errorThreshold) };
    }

    private static int CountDecisionPoints(ParsedSource parsed, int start, int end, int currentFunctionIndex, int baseComplexity)
    {
        if (parsed.Tokens.Count == 0 || end < start)
        {
            return baseComplexity;
        }

        int total = baseComplexity;
        int i = Math.Max(0, start);
        end = Math.Min(end, parsed.Tokens.Count - 1);

        while (i <= end)
        {
            if (TryGetNestedFunctionSkip(parsed, currentFunctionIndex, i, out int skipEnd))
            {
                i = skipEnd + 1;
                continue;
            }

            string text = parsed.Tokens[i].Text;
            if (text is "if" or "elif" or "for" or "foreach" or "catch" or "except" or "case" or "match" or "loop")
            {
                total++;
            }
            else if (text == "select")
            {
                total++;
            }
            else if (text == "do")
            {
                total++;
            }
            else if (text == "while")
            {
                if (!IsDoWhileCondition(parsed, i))
                {
                    total++;
                }
            }
            else if (text is "&&" or "||" or "??" or "and" or "or")
            {
                total++;
            }
            else if (text == "?" && IsLikelyTernary(parsed, i))
            {
                total++;
            }

            i++;
        }

        return total;
    }

    private static int ComputeCognitive(ParsedSource parsed, int start, int end, int currentFunctionIndex)
    {
        if (parsed.Tokens.Count == 0 || end < start)
        {
            return 0;
        }

        HashSet<int> controlBodyStarts = FindControlBodyStarts(parsed, start, end);
        HashSet<int> elseIfIndexes = FindElseIfIndexes(parsed, start, end);
        Stack<int> activeControlEnds = new();

        int total = 0;
        int i = Math.Max(0, start);
        end = Math.Min(end, parsed.Tokens.Count - 1);

        while (i <= end)
        {
            while (activeControlEnds.Count > 0 && i >= activeControlEnds.Peek())
            {
                activeControlEnds.Pop();
            }

            if (TryGetNestedFunctionSkip(parsed, currentFunctionIndex, i, out int skipEnd))
            {
                if (currentFunctionIndex >= 0)
                {
                    total++;
                }

                i = skipEnd + 1;
                continue;
            }

            string text = parsed.Tokens[i].Text;
            int nesting = activeControlEnds.Count;

            if (text == "if")
            {
                total += elseIfIndexes.Contains(i) ? 1 : 1 + nesting;
            }
            else if (text == "elif")
            {
                total++;
            }
            else if (text is "for" or "foreach" or "catch" or "except" or "switch" or "match" or "loop" or "select")
            {
                total += 1 + nesting;
            }
            else if (text == "do")
            {
                total += 1 + nesting;
            }
            else if (text == "while")
            {
                if (!IsDoWhileCondition(parsed, i))
                {
                    total += 1 + nesting;
                }
            }
            else if (text == "else")
            {
                int next = ParserUtilities.NextSignificant(parsed.Tokens, i + 1);
                if (next < 0 || parsed.Tokens[next].Text != "if")
                {
                    total++;
                }
            }
            else if (text is "&&" or "||" or "??" or "and" or "or")
            {
                total++;
            }
            else if (text == "?" && IsLikelyTernary(parsed, i))
            {
                total += 1 + nesting;
            }

            if (controlBodyStarts.Contains(i))
            {
                int close = parsed.BracePartner[i];
                if (close > i)
                {
                    activeControlEnds.Push(close);
                }
            }

            i++;
        }

        return total;
    }

    private static int ComputeNPath(ParsedSource parsed, int start, int end, int currentFunctionIndex)
    {
        if (parsed.Tokens.Count == 0 || end < start)
        {
            return 1;
        }

        long result = 1;
        int i = Math.Max(0, start);
        end = Math.Min(end, parsed.Tokens.Count - 1);

        while (i <= end)
        {
            if (TryGetNestedFunctionSkip(parsed, currentFunctionIndex, i, out int skipEnd))
            {
                i = skipEnd + 1;
                continue;
            }

            string text = parsed.Tokens[i].Text;
            if (text == "if")
            {
                int logical = CountConditionLogicalOps(parsed, i, currentFunctionIndex);
                result = MultiplyCapped(result, 2 + logical);
            }
            else if (text == "elif")
            {
                int logical = CountConditionLogicalOps(parsed, i, currentFunctionIndex);
                result = MultiplyCapped(result, 2 + logical);
            }
            else if (text is "for" or "foreach" or "catch" or "except" or "loop")
            {
                int logical = CountConditionLogicalOps(parsed, i, currentFunctionIndex);
                result = MultiplyCapped(result, 2 + logical);
            }
            else if (text == "while")
            {
                if (!IsDoWhileCondition(parsed, i))
                {
                    int logical = CountConditionLogicalOps(parsed, i, currentFunctionIndex);
                    result = MultiplyCapped(result, 2 + logical);
                }
            }
            else if (text == "do")
            {
                result = MultiplyCapped(result, 2);
            }
            else if (text is "switch" or "match" or "select")
            {
                int bodyStart = FindControlBodyStart(parsed, i, end);
                int caseCount = 0;
                if (bodyStart >= 0)
                {
                    int bodyEnd = parsed.BracePartner[bodyStart];
                    for (int j = bodyStart + 1; j < bodyEnd; j++)
                    {
                        if (parsed.Tokens[j].Text is "case" or "default" ||
                            (text == "match" && parsed.Tokens[j].Text == "=>"))
                        {
                            caseCount++;
                        }
                    }

                    i = bodyEnd;
                }

                result = MultiplyCapped(result, Math.Max(1, caseCount));
            }
            else if (text == "?" && IsLikelyTernary(parsed, i))
            {
                result = MultiplyCapped(result, 2);
            }
            else if (text == "return")
            {
                int statementEnd = FindStatementEnd(parsed, i + 1, end);
                int logical = CountLogicalOps(parsed, i + 1, statementEnd, currentFunctionIndex);
                result = MultiplyCapped(result, 1 + logical);
                i = statementEnd;
            }

            if (result >= NPathCap)
            {
                return NPathCap;
            }

            i++;
        }

        return (int)Math.Max(1, result);
    }

    private static int ComputeMaxNestingDepth(ParsedSource parsed, int start, int end, int currentFunctionIndex)
    {
        if (parsed.Tokens.Count == 0 || end < start)
        {
            return 0;
        }

        HashSet<int> controlBodyStarts = FindControlBodyStarts(parsed, start, end);
        Stack<int> activeControlEnds = new();
        int maxDepth = 0;
        int i = Math.Max(0, start);
        end = Math.Min(end, parsed.Tokens.Count - 1);

        while (i <= end)
        {
            while (activeControlEnds.Count > 0 && i >= activeControlEnds.Peek())
            {
                activeControlEnds.Pop();
            }

            if (TryGetNestedFunctionSkip(parsed, currentFunctionIndex, i, out int skipEnd))
            {
                i = skipEnd + 1;
                continue;
            }

            if (controlBodyStarts.Contains(i))
            {
                int close = parsed.BracePartner[i];
                if (close > i)
                {
                    activeControlEnds.Push(close);
                    maxDepth = Math.Max(maxDepth, activeControlEnds.Count);
                }
            }

            i++;
        }

        return maxDepth;
    }

    private static int ComputeParameterCount(ParsedSource parsed, FunctionSpan span)
    {
        List<Token> tokens = parsed.Tokens;
        int open = -1;
        for (int i = span.StartIndex; i < span.BodyStartIndex && i < tokens.Count; i++)
        {
            if (tokens[i].Text == "(")
            {
                open = i;
                break;
            }
        }

        if (open < 0)
        {
            // A single-identifier arrow parameter, e.g. `x => x + 1`.
            return span.StartIndex < tokens.Count && ParserUtilities.IsNameToken(tokens[span.StartIndex]) ? 1 : 0;
        }

        int close = parsed.ParenPartner[open];
        if (close <= open + 1)
        {
            return 0;
        }

        int count = 1;
        int depth = 0;
        for (int i = open + 1; i < close; i++)
        {
            string text = tokens[i].Text;
            if (text is "(" or "[" or "{")
            {
                depth++;
            }
            else if (text is ")" or "]" or "}")
            {
                depth--;
            }
            else if (text == "," && depth == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static List<ClassMetrics> BuildClassMetrics(ParsedSource parsed)
    {
        List<ClassMetrics> results = [];
        for (int i = 0; i < parsed.Classes.Count; i++)
        {
            ClassSpan classSpan = parsed.Classes[i];
            int wmc = 0;
            foreach (int functionIndex in classSpan.MethodFunctionIndexes)
            {
                FunctionSpan method = parsed.Functions[functionIndex];
                wmc += CountDecisionPoints(parsed, method.BodyStartIndex, method.EndIndex, functionIndex, baseComplexity: 1);
            }

            int lcom4 = ComputeLcom4(parsed, classSpan);
            results.Add(new ClassMetrics(
                classSpan.Name,
                parsed.Tokens[classSpan.StartIndex].Line,
                classSpan.MethodFunctionIndexes.Count,
                classSpan.FieldNames.Count,
                lcom4,
                wmc));
        }

        results.Sort(static (left, right) =>
        {
            int line = left.Line.CompareTo(right.Line);
            return line != 0 ? line : string.CompareOrdinal(left.Name, right.Name);
        });

        return results;
    }

    private static int ComputeLcom4(ParsedSource parsed, ClassSpan classSpan)
    {
        int methodCount = classSpan.MethodFunctionIndexes.Count;
        if (methodCount == 0)
        {
            return 0;
        }

        Dictionary<string, int> methodIndexes = new(StringComparer.Ordinal);
        for (int i = 0; i < methodCount; i++)
        {
            methodIndexes[parsed.Functions[classSpan.MethodFunctionIndexes[i]].Name] = i;
        }

        HashSet<string> fields = new(classSpan.FieldNames, StringComparer.Ordinal);
        List<HashSet<string>> fieldAccess = [];
        List<HashSet<int>> methodCalls = [];
        for (int i = 0; i < methodCount; i++)
        {
            fieldAccess.Add(new HashSet<string>(StringComparer.Ordinal));
            methodCalls.Add([]);
        }

        for (int methodOrdinal = 0; methodOrdinal < methodCount; methodOrdinal++)
        {
            int functionIndex = classSpan.MethodFunctionIndexes[methodOrdinal];
            FunctionSpan method = parsed.Functions[functionIndex];
            int i = method.BodyStartIndex + 1;
            while (i < method.EndIndex)
            {
                if (TryGetNestedFunctionSkip(parsed, functionIndex, i, out int skipEnd))
                {
                    i = skipEnd + 1;
                    continue;
                }

                if (parsed.Tokens[i].Text is "this" or "self" &&
                    i + 2 < method.EndIndex &&
                    parsed.Tokens[i + 1].Text is "." or "->" &&
                    ParserUtilities.IsNameToken(parsed.Tokens[i + 2]))
                {
                    string member = parsed.Tokens[i + 2].Text;
                    if (fields.Contains(member))
                    {
                        fieldAccess[methodOrdinal].Add(member);
                    }
                    else if (methodIndexes.TryGetValue(member, out int calledIndex) && calledIndex != methodOrdinal)
                    {
                        methodCalls[methodOrdinal].Add(calledIndex);
                    }

                    i += 3;
                    continue;
                }

                i++;
            }
        }

        int[] parent = new int[methodCount];
        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        for (int i = 0; i < methodCount; i++)
        {
            foreach (int called in methodCalls[i])
            {
                Union(parent, i, called);
            }

            for (int j = i + 1; j < methodCount; j++)
            {
                if (Overlaps(fieldAccess[i], fieldAccess[j]))
                {
                    Union(parent, i, j);
                }
            }
        }

        HashSet<int> components = [];
        for (int i = 0; i < methodCount; i++)
        {
            components.Add(Find(parent, i));
        }

        return components.Count;
    }

    private static (double Volume, double Difficulty) ComputeHalstead(ParsedSource parsed, int start, int end)
    {
        Dictionary<string, int> operators = new(StringComparer.Ordinal);
        Dictionary<string, int> operands = new(StringComparer.Ordinal);

        if (parsed.Tokens.Count == 0 || end < start)
        {
            return (0.0, 0.0);
        }

        start = Math.Max(0, start);
        end = Math.Min(end, parsed.Tokens.Count - 1);

        for (int i = start; i <= end; i++)
        {
            Token token = parsed.Tokens[i];
            if (IsOperand(token))
            {
                AddCount(operands, token.Text);
            }
            else if (IsOperator(token))
            {
                AddCount(operators, token.Text);
            }
        }

        int n1 = operators.Count;
        int n2 = operands.Count;
        if (n1 == 0 || n2 == 0)
        {
            return (0.0, 0.0);
        }

        int N1 = SumValues(operators);
        int N2 = SumValues(operands);
        int vocabulary = n1 + n2;
        int length = N1 + N2;
        double volume = length * Math.Log2(vocabulary);
        double difficulty = (n1 / 2.0) * (N2 / (double)n2);
        return (Math.Round(volume, 2), Math.Round(difficulty, 2));
    }

    private static double ComputeMaintainabilityIndex(double volume, int cyclomatic, int loc)
    {
        if (volume <= 0.0 || loc <= 0)
        {
            return 100.0;
        }

        double raw = 171.0 - (5.2 * Math.Log(volume)) - (0.23 * cyclomatic) - (16.2 * Math.Log(loc));
        double normalized = Math.Max(0.0, Math.Min(100.0, raw * 100.0 / 171.0));
        return Math.Round(normalized, 1);
    }

    private static HashSet<int> FindControlBodyStarts(ParsedSource parsed, int start, int end)
    {
        HashSet<int> starts = [];
        for (int i = Math.Max(0, start); i <= end && i < parsed.Tokens.Count; i++)
        {
            string text = parsed.Tokens[i].Text;
            if (text is "if" or "elif" or "for" or "foreach" or "while" or "switch" or "match" or "select" or "catch" or "except" or "else" or "do" or "loop")
            {
                if (text == "while" && IsDoWhileCondition(parsed, i))
                {
                    continue;
                }

                int bodyStart = FindControlBodyStart(parsed, i, end);
                if (bodyStart >= 0)
                {
                    starts.Add(bodyStart);
                }
            }
        }

        return starts;
    }

    private static HashSet<int> FindElseIfIndexes(ParsedSource parsed, int start, int end)
    {
        HashSet<int> indexes = [];
        for (int i = Math.Max(0, start); i <= end && i < parsed.Tokens.Count; i++)
        {
            if (parsed.Tokens[i].Text != "else")
            {
                continue;
            }

            int next = ParserUtilities.NextSignificant(parsed.Tokens, i + 1);
            if (next >= 0 && parsed.Tokens[next].Text == "if")
            {
                indexes.Add(next);
            }
        }

        return indexes;
    }

    private static int FindControlBodyStart(ParsedSource parsed, int controlIndex, int end)
    {
        List<Token> tokens = parsed.Tokens;
        string text = tokens[controlIndex].Text;

        if (text == "do")
        {
            int next = ParserUtilities.NextSignificant(tokens, controlIndex + 1);
            return next >= 0 && tokens[next].Text == "{" ? next : -1;
        }

        if (text == "else")
        {
            int next = ParserUtilities.NextSignificant(tokens, controlIndex + 1);
            if (next >= 0 && tokens[next].Text == "if")
            {
                return FindControlBodyStart(parsed, next, end);
            }

            return next >= 0 && tokens[next].Text == "{" ? next : -1;
        }

        int cursor = ParserUtilities.NextSignificant(tokens, controlIndex + 1);
        if (cursor >= 0 && tokens[cursor].Text == "(")
        {
            int closeParen = parsed.ParenPartner[cursor];
            cursor = closeParen >= 0 ? ParserUtilities.NextSignificant(tokens, closeParen + 1) : -1;
        }
        else
        {
            while (cursor >= 0 && cursor <= end && tokens[cursor].Text != ":" && tokens[cursor].Text != "{")
            {
                cursor++;
            }

            if (cursor >= 0 && cursor <= end && tokens[cursor].Text == ":")
            {
                cursor = ParserUtilities.NextSignificant(tokens, cursor + 1);
            }
        }

        return cursor >= 0 && cursor <= end && tokens[cursor].Text == "{" ? cursor : -1;
    }

    private static int CountConditionLogicalOps(ParsedSource parsed, int controlIndex, int currentFunctionIndex)
    {
        int next = ParserUtilities.NextSignificant(parsed.Tokens, controlIndex + 1);
        if (next < 0)
        {
            return 0;
        }

        if (parsed.Tokens[next].Text == "(")
        {
            int conditionEnd = parsed.ParenPartner[next];
            return conditionEnd > next ? CountLogicalOps(parsed, next, conditionEnd, currentFunctionIndex) : 0;
        }

        int end = next;
        while (end < parsed.Tokens.Count && parsed.Tokens[end].Text is not ":" and not "{")
        {
            end++;
        }

        return end > next ? CountLogicalOps(parsed, next, end - 1, currentFunctionIndex) : 0;
    }

    private static int CountLogicalOps(ParsedSource parsed, int start, int end, int currentFunctionIndex)
    {
        int total = 0;
        for (int i = Math.Max(0, start); i <= end && i < parsed.Tokens.Count; i++)
        {
            if (TryGetNestedFunctionSkip(parsed, currentFunctionIndex, i, out int skipEnd))
            {
                i = skipEnd;
                continue;
            }

            if (parsed.Tokens[i].Text is "&&" or "||" or "??" or "and" or "or")
            {
                total++;
            }
        }

        return total;
    }

    private static int FindStatementEnd(ParsedSource parsed, int start, int end)
    {
        int i = Math.Max(0, start);
        int startLine = i < parsed.Tokens.Count ? parsed.Tokens[i].Line : -1;
        while (i <= end && i < parsed.Tokens.Count)
        {
            if (startLine >= 0 && i > start && parsed.Tokens[i].Line != startLine)
            {
                return i - 1;
            }

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

    private static bool TryGetNestedFunctionSkip(ParsedSource parsed, int currentFunctionIndex, int tokenIndex, out int endIndex)
    {
        for (int i = 0; i < parsed.Functions.Count; i++)
        {
            if (i == currentFunctionIndex)
            {
                continue;
            }

            FunctionSpan candidate = parsed.Functions[i];
            if (candidate.StartIndex != tokenIndex)
            {
                continue;
            }

            if (currentFunctionIndex < 0 || IsDescendantOf(parsed, i, currentFunctionIndex))
            {
                endIndex = candidate.EndIndex;
                return true;
            }
        }

        endIndex = -1;
        return false;
    }

    private static bool IsDescendantOf(ParsedSource parsed, int childIndex, int ancestorIndex)
    {
        int cursor = parsed.Functions[childIndex].ParentIndex;
        while (cursor >= 0)
        {
            if (cursor == ancestorIndex)
            {
                return true;
            }

            cursor = parsed.Functions[cursor].ParentIndex;
        }

        return false;
    }

    private static bool IsDoWhileCondition(ParsedSource parsed, int whileIndex)
    {
        int previous = ParserUtilities.PreviousSignificant(parsed.Tokens, whileIndex - 1);
        if (previous < 0 || parsed.Tokens[previous].Text != "}")
        {
            return false;
        }

        int open = parsed.BracePartner[previous];
        int beforeOpen = open > 0 ? ParserUtilities.PreviousSignificant(parsed.Tokens, open - 1) : -1;
        return beforeOpen >= 0 && parsed.Tokens[beforeOpen].Text == "do";
    }

    private static bool IsLikelyTernary(ParsedSource parsed, int questionIndex)
    {
        // Distinguish a conditional operator (`cond ? a : b`) from a nullable type marker
        // (`int?`, `string?`, `List<int?>`) or a TypeScript optional marker. A real ternary
        // resolves a matching `:` at the same bracket-nesting depth before the surrounding
        // expression ends; `(` `[` `{` are tracked as nesting so literals in a branch and
        // method bodies after a nullable return type are skipped over.
        List<Token> tokens = parsed.Tokens;

        // `?:` with nothing between is a TypeScript optional marker (`name?: T`), not a ternary.
        if (questionIndex + 1 < tokens.Count && tokens[questionIndex + 1].Text == ":")
        {
            return false;
        }

        // In C# a lone `?` is most often a nullable type marker on a declaration, so a `{`
        // (block/initializer) or `=>` (arrow body) at expression top level rules a ternary
        // out. Other languages have no `T?` types but do use arrow functions as ternary
        // branches, so those terminators are not applied there.
        bool strict = parsed.Language == SourceLanguage.CSharp;

        int depth = 0;
        for (int i = questionIndex + 1; i < tokens.Count; i++)
        {
            switch (tokens[i].Text)
            {
                case "(" or "[":
                    depth++;
                    break;
                case ")" or "]":
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;
                case "{":
                    if (depth == 0 && strict)
                    {
                        return false;
                    }

                    depth++;
                    break;
                case "}":
                    if (depth == 0)
                    {
                        return false;
                    }

                    depth--;
                    break;
                case ":":
                    if (depth == 0)
                    {
                        return true;
                    }

                    break;
                case ";" or ",":
                    if (depth == 0)
                    {
                        return false;
                    }

                    break;
                case "=>":
                    if (depth == 0 && strict)
                    {
                        return false;
                    }

                    break;
            }
        }

        return false;
    }

    private static long MultiplyCapped(long left, int right)
    {
        long value = left * Math.Max(1, right);
        return value > NPathCap ? NPathCap : value;
    }

    private static bool IsOperand(Token token)
    {
        if (token.Kind is TokenKind.Number or TokenKind.String)
        {
            return !string.IsNullOrEmpty(token.Text);
        }

        if (token.Kind != TokenKind.Identifier)
        {
            return false;
        }

        return !OperatorKeywords.Contains(token.Text) || OperandKeywords.Contains(token.Text);
    }

    private static bool IsOperator(Token token)
    {
        if (token.Kind is TokenKind.Operator or TokenKind.Punctuation)
        {
            return true;
        }

        return token.Kind == TokenKind.Identifier && OperatorKeywords.Contains(token.Text) && !OperandKeywords.Contains(token.Text);
    }

    private static void AddCount(Dictionary<string, int> counts, string key)
    {
        if (counts.TryGetValue(key, out int existing))
        {
            counts[key] = existing + 1;
        }
        else
        {
            counts[key] = 1;
        }
    }

    private static int SumValues(Dictionary<string, int> counts)
    {
        int total = 0;
        foreach (int value in counts.Values)
        {
            total += value;
        }

        return total;
    }

    private static bool Overlaps(HashSet<string> left, HashSet<string> right)
    {
        foreach (string value in left)
        {
            if (right.Contains(value))
            {
                return true;
            }
        }

        return false;
    }

    private static int Find(int[] parent, int value)
    {
        while (parent[value] != value)
        {
            parent[value] = parent[parent[value]];
            value = parent[value];
        }

        return value;
    }

    private static void Union(int[] parent, int left, int right)
    {
        int leftRoot = Find(parent, left);
        int rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[leftRoot] = rightRoot;
        }
    }

    private static int CountLoc(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && i < text.Length - 1)
            {
                lines++;
            }
        }

        return lines;
    }
}
