using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
    /// <summary>
    /// Path text emitted per file. A Rust use-group repeats its shared prefix for every leaf,
    /// so a source under the graph's per-file read limit could otherwise expand to many times
    /// its size; reading stops at this budget and the outline reports the omission.
    /// </summary>
    internal const int MaxImportTextCharacters = 256 * 1024;

    internal static IReadOnlyList<SourceOutlineImport> Read(SourceLanguage language, IReadOnlyList<Token> tokens)
        => Read(language, tokens, out _);

    internal static IReadOnlyList<SourceOutlineImport> Read(SourceLanguage language, IReadOnlyList<Token> tokens,
        out bool truncated)
    {
        var reader = new ImportReader();
        if (language == SourceLanguage.Python) ReadPython(tokens, reader);
        if (language == SourceLanguage.Rust) ReadRust(tokens, 0, tokens.Count, "", reader, 0);
        truncated = reader.Exhausted;
        return reader.Imports.Distinct().ToArray();
    }

    private static void ReadPython(IReadOnlyList<Token> tokens, ImportReader reader)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var keyword = tokens[i];
            if (keyword.Text is not ("from" or "import")) continue;
            var cursor = i + 1;
            var from = keyword.Text == "from";
            var module = from ? PythonPath(tokens, ref cursor, allowRelative: true, reader.Builder) : "";
            if (from && (module.Length == 0 || cursor >= tokens.Count || tokens[cursor].Text != "import")) continue;
            // The module string is shared by every name the statement imports; charge it once.
            if (from && !reader.Keep(module.Length)) return;
            if (from) cursor++;
            var grouped = cursor < tokens.Count && tokens[cursor].Text == "(";
            if (grouped) cursor++;
            while (cursor < tokens.Count)
            {
                var name = tokens[cursor].Text == "*" ? tokens[cursor++].Text
                    : PythonPath(tokens, ref cursor, allowRelative: false, reader.Builder);
                if (name.Length == 0) break;
                string? alias = null;
                if (cursor + 1 < tokens.Count && tokens[cursor].Text == "as"
                    && tokens[cursor + 1].Kind == TokenKind.Identifier)
                {
                    alias = tokens[cursor + 1].Text;
                    cursor += 2;
                }
                if (!reader.Keep(name.Length)) return;
                reader.Imports.Add(new(from ? module : name, from ? "python-from" : "python-import",
                    keyword.Line, from ? name : null, alias));
                if (cursor >= tokens.Count || tokens[cursor].Text != ",") break;
                cursor++;
                if (grouped && cursor < tokens.Count && tokens[cursor].Text == ")") break;
            }
            i = Math.Max(i, cursor - 1);
        }
    }

    // Append every segment to one builder: the path is built once, in linear time, however
    // many dotted segments the source contains.
    private static string PythonPath(IReadOnlyList<Token> tokens, ref int cursor, bool allowRelative, StringBuilder builder)
    {
        builder.Clear();
        if (allowRelative)
            while (cursor < tokens.Count && tokens[cursor].Text is "." or "...") builder.Append(tokens[cursor++].Text);
        if (cursor >= tokens.Count || tokens[cursor].Kind != TokenKind.Identifier || tokens[cursor].Text == "import")
            return builder.ToString();
        builder.Append(tokens[cursor++].Text);
        while (cursor + 1 < tokens.Count && tokens[cursor].Text == "." && tokens[cursor + 1].Kind == TokenKind.Identifier)
        {
            builder.Append('.').Append(tokens[cursor + 1].Text);
            cursor += 2;
        }
        return builder.ToString();
    }

    private static void ReadRust(IReadOnlyList<Token> tokens, int start, int end, string scope,
        ImportReader reader, int depth)
    {
        if (depth > 64) return;
        for (var i = start; i < end && !reader.Exhausted; i++)
        {
            // Token trees in macros are input to code generation, not literal module declarations.
            if (tokens[i].Text == "!" && i > start && tokens[i - 1].Kind == TokenKind.Identifier)
            {
                var open = i + 1;
                var name = tokens[i - 1].Text;
                // Only macro_rules! takes an identifier between ! and its token tree. In
                // `if !condition { ... }`, the following block is ordinary executable code.
                if (name == "macro_rules" && open < end && tokens[open].Kind == TokenKind.Identifier) open++;
                // These expression keywords can precede unary negation of a block. A macro
                // invocation instead has a macro path immediately before ! and a delimiter after it.
                if (name is not ("if" or "while" or "match" or "return" or "break" or "yield" or "in")
                    && open < end && tokens[open].Text is "{" or "(" or "[")
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
                    reader.Imports.Add(new(name, "rust-mod", tokens[i].Line, Scope: scope));
                else if (next == "{")
                {
                    var close = Close(tokens, i + 2, end);
                    reader.Imports.Add(new(name, "rust-inline", tokens[i].Line, Scope: scope));
                    ReadRust(tokens, i + 3, close, Join(scope, name), reader, depth + 1);
                    i = close;
                }
                continue;
            }
            if (tokens[i].Text != "use") continue;
            var cursor = i + 1;
            reader.Segments.Clear();
            ReadRustUse(tokens, ref cursor, end, scope, tokens[i].Line, reader, 0);
            i = Math.Max(i, cursor - 1);
        }
    }

    // The path under construction is a segment stack shared by the whole use statement. Each
    // leaf joins it once, so a group's shared prefix is never re-copied per segment; an empty
    // first segment stands for a leading `::`.
    private static void ReadRustUse(IReadOnlyList<Token> tokens, ref int cursor, int end, string scope, int line,
        ImportReader reader, int depth)
    {
        if (depth > 64 || reader.Exhausted) return;
        var segments = reader.Segments;
        var entry = segments.Count;
        if (entry == 0 && cursor < end && tokens[cursor].Text == "::") { segments.Add(""); cursor++; }
        try
        {
            while (cursor < end)
            {
                var token = tokens[cursor];
                if (token.Text == "{")
                {
                    cursor++;
                    while (cursor < end && tokens[cursor].Text != "}")
                    {
                        var before = cursor;
                        ReadRustUse(tokens, ref cursor, end, scope, line, reader, depth + 1);
                        if (reader.Exhausted) return;
                        if (cursor < end && tokens[cursor].Text == ",") cursor++;
                        else if (cursor == before || cursor >= end || tokens[cursor].Text != "}") break;
                    }
                    if (cursor < end && tokens[cursor].Text == "}") cursor++;
                    return;
                }
                if (token.Kind != TokenKind.Identifier && token.Text != "*") return;
                cursor++;
                // `self` inside a group names the prefix itself: `use a::b::{self}` imports a::b.
                if (token.Text != "self" || segments.Count == 0) segments.Add(token.Text);
                if (cursor < end && tokens[cursor].Text == "::") { cursor++; continue; }
                string? alias = null;
                if (cursor + 1 < end && tokens[cursor].Text == "as" && tokens[cursor + 1].Kind == TokenKind.Identifier)
                {
                    alias = tokens[cursor + 1].Text;
                    cursor += 2;
                }
                var path = string.Join("::", segments);
                if (!reader.Keep(path.Length)) return;
                reader.Imports.Add(new(path, "rust-use", line, Alias: alias, Scope: scope));
                return;
            }
        }
        finally
        {
            segments.RemoveRange(entry, segments.Count - entry);
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

    private sealed class ImportReader
    {
        private int remaining = MaxImportTextCharacters;

        public List<SourceOutlineImport> Imports { get; } = [];
        public List<string> Segments { get; } = [];
        public StringBuilder Builder { get; } = new();
        public bool Exhausted { get; private set; }

        /// <summary>Charges emitted path text; false once the file's budget is spent.</summary>
        public bool Keep(int characters)
        {
            remaining -= characters;
            if (remaining < 0) Exhausted = true;
            return !Exhausted;
        }
    }
}
