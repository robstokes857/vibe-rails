using System;
using System.Collections.Generic;

namespace MintLint;

internal sealed class ParsedSource
{
    public ParsedSource(string path, string displayPath, string text, SourceLanguage language, List<Token> tokens)
    {
        Path = path;
        DisplayPath = displayPath;
        Text = text;
        Language = language;
        Tokens = tokens;
        BracePartner = BuildPartnerMap(tokens, "{", "}");
        ParenPartner = BuildPartnerMap(tokens, "(", ")");
        BracketPartner = BuildPartnerMap(tokens, "[", "]");
    }

    public string Path { get; }

    public string DisplayPath { get; }

    public string Text { get; }

    public SourceLanguage Language { get; }

    public List<Token> Tokens { get; }

    public int[] BracePartner { get; }

    public int[] ParenPartner { get; }

    public int[] BracketPartner { get; }

    public List<FunctionSpan> Functions { get; } = [];

    public List<ClassSpan> Classes { get; } = [];

    public List<string> ImportSources { get; } = [];

    // Functions grouped by start token, built on first lookup once parsing has filled Functions.
    // Metric passes ask about every token, so a scan of Functions per token is quadratic.
    private int[]? _firstFunctionAtToken;
    private int[]? _nextFunctionAtSameToken;

    /// <summary>
    /// Returns the lowest-index function starting at <paramref name="tokenIndex"/>, or -1.
    /// Continue with <see cref="NextFunctionAtSameStart"/> to visit the rest in ascending order.
    /// </summary>
    public int FirstFunctionStartingAt(int tokenIndex)
    {
        if (_nextFunctionAtSameToken is null || _nextFunctionAtSameToken.Length != Functions.Count)
        {
            BuildFunctionStartIndex();
        }

        return (uint)tokenIndex < (uint)_firstFunctionAtToken!.Length ? _firstFunctionAtToken[tokenIndex] : -1;
    }

    /// <summary>
    /// Returns the next higher-index function with the same start token as
    /// <paramref name="functionIndex"/>, or -1.
    /// </summary>
    public int NextFunctionAtSameStart(int functionIndex) => _nextFunctionAtSameToken![functionIndex];

    private void BuildFunctionStartIndex()
    {
        int[] first = new int[Tokens.Count];
        Array.Fill(first, -1);
        int[] next = new int[Functions.Count];

        // Prepending in descending order leaves each chain in ascending order.
        for (int i = Functions.Count - 1; i >= 0; i--)
        {
            int start = Functions[i].StartIndex;
            if ((uint)start >= (uint)first.Length)
            {
                next[i] = -1;
                continue;
            }

            next[i] = first[start];
            first[start] = i;
        }

        _firstFunctionAtToken = first;
        _nextFunctionAtSameToken = next;
    }

    private static int[] BuildPartnerMap(List<Token> tokens, string open, string close)
    {
        int[] partners = new int[tokens.Count];
        for (int i = 0; i < partners.Length; i++)
        {
            partners[i] = -1;
        }

        Stack<int> stack = new();
        for (int i = 0; i < tokens.Count; i++)
        {
            // Only real punctuation delimits scopes; a string/number literal whose content
            // happens to be "{" or "(" must never be paired, or it desyncs the whole file.
            if (tokens[i].Kind != TokenKind.Punctuation)
            {
                continue;
            }

            string text = tokens[i].Text;
            if (text == open)
            {
                stack.Push(i);
            }
            else if (text == close && stack.Count > 0)
            {
                int openIndex = stack.Pop();
                partners[openIndex] = i;
                partners[i] = openIndex;
            }
        }

        return partners;
    }
}

internal sealed class FunctionSpan
{
    public FunctionSpan(string name, int startIndex, int bodyStartIndex, int endIndex, bool isMethod, int classIndex)
    {
        Name = name;
        StartIndex = startIndex;
        BodyStartIndex = bodyStartIndex;
        EndIndex = endIndex;
        IsMethod = isMethod;
        ClassIndex = classIndex;
    }

    public string Name { get; }

    public int StartIndex { get; }

    public int BodyStartIndex { get; }

    public int EndIndex { get; }

    public bool IsMethod { get; }

    public int ClassIndex { get; }

    public int ParentIndex { get; set; } = -1;
}

internal sealed class ClassSpan
{
    public ClassSpan(string name, int startIndex, int bodyStartIndex, int endIndex)
    {
        Name = name;
        StartIndex = startIndex;
        BodyStartIndex = bodyStartIndex;
        EndIndex = endIndex;
    }

    public string Name { get; }

    public int StartIndex { get; }

    public int BodyStartIndex { get; }

    public int EndIndex { get; }

    public List<int> MethodFunctionIndexes { get; } = [];

    public List<string> FieldNames { get; } = [];
}
