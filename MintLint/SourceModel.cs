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
