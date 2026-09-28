using System;
using System.Collections.Generic;
using System.Linq;

namespace MintLint;

/// <summary>Token-backed module import evidence, including its original source line and alias.</summary>
/// <param name="Path">The literal module path, preserving Python relative dots and Rust qualifiers.</param>
/// <param name="Kind">The import syntax: python-import, python-from, rust-mod, rust-inline, or rust-use.</param>
/// <param name="Line">The original source line.</param>
/// <param name="ImportedName">A member named by a Python from-import, when present.</param>
/// <param name="Alias">The local alias, when present.</param>
/// <param name="Scope">The containing Rust inline module path, relative to its source file.</param>
public sealed record SourceOutlineImport(string Path, string Kind, int Line,
    string? ImportedName = null, string? Alias = null, string? Scope = null);

// Mapper-only evidence: do not change the analyzer's historical import counts or quality scores.
internal static class SourceModuleImports
{
    internal static IReadOnlyList<SourceOutlineImport> Read(SourceLanguage language, IReadOnlyList<Token> tokens)
    {
        var imports = new List<SourceOutlineImport>();
        if (language == SourceLanguage.Python) ReadPython(tokens, imports);
        if (language == SourceLanguage.Rust) ReadRust(tokens, 0, tokens.Count, "", imports, 0);
        return imports.Distinct().ToArray();
    }

    private static void ReadPython(IReadOnlyList<Token> tokens, List<SourceOutlineImport> imports)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var keyword = tokens[i];
            if (keyword.Text is not ("from" or "import")) continue;
            var cursor = i + 1;
            var from = keyword.Text == "from";
            var module = from ? PythonPath(tokens, ref cursor, allowRelative: true) : "";
            if (from && (module.Length == 0 || cursor >= tokens.Count || tokens[cursor].Text != "import")) continue;
            if (from) cursor++;
            var grouped = cursor < tokens.Count && tokens[cursor].Text == "(";
            if (grouped) cursor++;
            while (cursor < tokens.Count)
            {
                var name = tokens[cursor].Text == "*" ? tokens[cursor++].Text
                    : PythonPath(tokens, ref cursor, allowRelative: false);
                if (name.Length == 0) break;
                string? alias = null;
                if (cursor + 1 < tokens.Count && tokens[cursor].Text == "as"
                    && tokens[cursor + 1].Kind == TokenKind.Identifier)
                {
                    alias = tokens[cursor + 1].Text;
                    cursor += 2;
                }
                imports.Add(new(from ? module : name, from ? "python-from" : "python-import",
                    keyword.Line, from ? name : null, alias));
                if (cursor >= tokens.Count || tokens[cursor].Text != ",") break;
                cursor++;
                if (grouped && cursor < tokens.Count && tokens[cursor].Text == ")") break;
            }
            i = Math.Max(i, cursor - 1);
        }
    }

    private static string PythonPath(IReadOnlyList<Token> tokens, ref int cursor, bool allowRelative)
    {
        var result = "";
        if (allowRelative)
            while (cursor < tokens.Count && tokens[cursor].Text is "." or "...") result += tokens[cursor++].Text;
        if (cursor >= tokens.Count || tokens[cursor].Kind != TokenKind.Identifier || tokens[cursor].Text == "import")
            return result;
        result += tokens[cursor++].Text;
        while (cursor + 1 < tokens.Count && tokens[cursor].Text == "." && tokens[cursor + 1].Kind == TokenKind.Identifier)
        {
            result += "." + tokens[cursor + 1].Text;
            cursor += 2;
        }
        return result;
    }

    private static void ReadRust(IReadOnlyList<Token> tokens, int start, int end, string scope,
        List<SourceOutlineImport> imports, int depth)
    {
        if (depth > 64) return;
        for (var i = start; i < end; i++)
        {
            // Token trees in macros are input to code generation, not literal module declarations.
            if (tokens[i].Text == "!")
            {
                var open = i + 1;
                if (open < end && tokens[open].Kind == TokenKind.Identifier) open++;
                if (open < end && tokens[open].Text is "{" or "(" or "[")
                {
                    i = Close(tokens, open, end);
                    continue;
                }
            }
            if (tokens[i].Text == "mod" && i + 2 < end && tokens[i + 1].Kind == TokenKind.Identifier)
            {
                var name = tokens[i + 1].Text;
                var next = tokens[i + 2].Text;
                if (next == ";" && !HasPathAttribute(tokens, i))
                    imports.Add(new(name, "rust-mod", tokens[i].Line, Scope: scope));
                else if (next == "{")
                {
                    var close = Close(tokens, i + 2, end);
                    imports.Add(new(name, "rust-inline", tokens[i].Line, Scope: scope));
                    ReadRust(tokens, i + 3, close, Join(scope, name), imports, depth + 1);
                    i = close;
                }
                continue;
            }
            if (tokens[i].Text != "use") continue;
            var cursor = i + 1;
            ReadRustUse(tokens, ref cursor, end, "", scope, tokens[i].Line, imports, 0);
            i = Math.Max(i, cursor - 1);
        }
    }

    private static void ReadRustUse(IReadOnlyList<Token> tokens, ref int cursor, int end, string prefix,
        string scope, int line, List<SourceOutlineImport> imports, int depth)
    {
        if (depth > 64) return;
        var path = prefix;
        if (cursor < end && tokens[cursor].Text == "::") { path = "::"; cursor++; }
        while (cursor < end)
        {
            var token = tokens[cursor];
            if (token.Text == "{")
            {
                cursor++;
                while (cursor < end && tokens[cursor].Text != "}")
                {
                    var before = cursor;
                    ReadRustUse(tokens, ref cursor, end, path, scope, line, imports, depth + 1);
                    if (cursor < end && tokens[cursor].Text == ",") cursor++;
                    else if (cursor == before || cursor >= end || tokens[cursor].Text != "}") break;
                }
                if (cursor < end && tokens[cursor].Text == "}") cursor++;
                return;
            }
            if (token.Kind != TokenKind.Identifier && token.Text != "*") return;
            cursor++;
            path = token.Text == "self" && path.Length > 0 ? path.TrimEnd(':') : Join(path, token.Text);
            if (cursor < end && tokens[cursor].Text == "::") { cursor++; continue; }
            string? alias = null;
            if (cursor + 1 < end && tokens[cursor].Text == "as" && tokens[cursor + 1].Kind == TokenKind.Identifier)
            {
                alias = tokens[cursor + 1].Text;
                cursor += 2;
            }
            imports.Add(new(path, "rust-use", line, Alias: alias, Scope: scope));
            return;
        }
    }

    private static bool HasPathAttribute(IReadOnlyList<Token> tokens, int mod)
    {
        // #[path = "..."] and #[cfg_attr(..., path = "...")] invalidate the conventional filename.
        for (var i = mod - 1; i >= 0 && tokens[i].Text is not (";" or "{" or "}"); i--)
            if (tokens[i].Text == "path" && i + 1 < mod && tokens[i + 1].Text == "=") return true;
        return false;
    }

    private static int Close(IReadOnlyList<Token> tokens, int open, int end)
    {
        var opening = tokens[open].Text;
        var closing = opening == "{" ? "}" : opening == "(" ? ")" : "]";
        var depth = 1;
        for (var i = open + 1; i < end; i++)
        {
            if (tokens[i].Text == opening) depth++;
            if (tokens[i].Text == closing && --depth == 0) return i;
        }
        return end;
    }

    private static string Join(string prefix, string part) => prefix.Length == 0 ? part
        : prefix.EndsWith("::", StringComparison.Ordinal) ? prefix + part : prefix + "::" + part;
}
