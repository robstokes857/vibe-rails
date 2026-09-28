using System;
using System.Collections.Generic;
using System.Linq;

namespace MintLint;

/// <summary>A lexical name with only the qualified type names visible in its source scope.</summary>
public sealed record SourceScopedReference(string Name, int Line, IEnumerable<string> Candidates,
    string Provenance, string? TypeNameHint = null);

/// <summary>Namespace and import evidence for C# and PHP; this is not compiler name binding.</summary>
public sealed record SourceReferenceScope(IReadOnlyList<SourceOutlineSymbol> Declarations,
    IReadOnlyList<SourceScopedReference> References)
{
    private const int MaxScopeTextCharacters = 64 * 1024;
    private const int MaxReferenceTextCharacters = 256 * 1024;
    private const int MaxReferences = 8192;

    /// <summary>True when bounded scope or reference evidence was omitted.</summary>
    public bool Truncated { get; init; }

    internal static SourceReferenceScope? Read(ParsedSource source)
    {
        if (source.Language is not (SourceLanguage.CSharp or SourceLanguage.Php)) return null;
        var php = source.Language == SourceLanguage.Php;
        var separator = php ? "\\" : ".";
        var comparer = php ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var tokens = source.Tokens;
        var root = new Scope("", null, 0, tokens.Count, comparer);
        var scope = root;
        var tokenScopes = new Scope?[tokens.Count];
        var excluded = new bool[tokens.Count];
        var references = new List<SourceScopedReference>();
        var scopeTextLimit = (int)Math.Min(MaxScopeTextCharacters, 2L * source.Text.Length);
        var referenceTextLimit = (int)Math.Min(MaxReferenceTextCharacters, 4L * source.Text.Length);
        var scopeTextCharacters = 0;
        var referenceTextCharacters = 0;
        var truncated = false;
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (truncated) return new([], []) { Truncated = true };
            if (i == scope.End && scope.Parent is not null) scope = scope.Parent;
            tokenScopes[i] = scope;
            if (tokens[i].Text == "namespace" && depth == scope.Depth)
            {
                var end = i + 1;
                var name = ReadName(tokens, ref end, separator);
                if (end < tokens.Count && tokens[end].Text is ";" or "{")
                {
                    Exclude(i, end);
                    var block = tokens[end].Text == "{";
                    if (block && source.BracePartner[end] < 0) continue;
                    var parent = php || !block ? root : scope;
                    var qualified = Qualify(parent.Name, name, separator);
                    if (!KeepScopeText(qualified.Length)) return new([], []) { Truncated = true };
                    scope = new Scope(qualified, parent,
                        block ? depth + 1 : depth, block ? source.BracePartner[end] : tokens.Count, comparer);
                    if (block) depth++;
                    i = end;
                    continue;
                }
            }
            // Top-level imports only. C# using statements and PHP closure/trait uses have different semantics.
            if (depth == scope.Depth && tokens[i].Text == (php ? "use" : "using"))
            {
                var end = i + 1;
                while (end < tokens.Count && tokens[end].Text is not (";" or "(" or "=")) end++;
                if (end < tokens.Count && tokens[end].Text == "=")
                    while (end < tokens.Count && tokens[end].Text != ";") end++;
                if (end < tokens.Count && tokens[end].Text == ";")
                {
                    if (php) ReadPhpUses(i + 1, end, scope, tokens[i].Line);
                    else if (!ReadCSharpUsing(i + 1, end, scope, tokens[i].Line)) continue;
                    Exclude(i, end);
                    i = end;
                    continue;
                }
            }
            if (tokens[i].Text == "{") depth++;
            else if (tokens[i].Text == "}") depth--;
        }

        // A partially read import scope could resolve a name to the wrong namespace.
        if (truncated) return new([], []) { Truncated = true };

        var declarations = new List<SourceOutlineSymbol>();
        foreach (var type in source.Classes)
        {
            var typeScope = tokenScopes[type.StartIndex] ?? root;
            var qualified = Qualify(typeScope.Name, type.Name, separator);
            if (!KeepReferenceText(qualified.Length)) return new([], []) { Truncated = true };
            declarations.Add(new(qualified, "type", tokens[type.StartIndex].Line));
            // A declaration is not a reference to an identically named type in another file.
            for (var i = type.StartIndex + 1; i < tokens.Count && i <= type.BodyStartIndex; i++)
                if (tokens[i].Text == type.Name) { excluded[i] = true; break; }
        }
        var seen = new HashSet<(Scope Scope, string Name, bool Absolute)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var namespaceRelative = php && tokens[i].Text == "namespace" && i + 1 < tokens.Count && tokens[i + 1].Text == "\\";
            if (excluded[i] || (!ParserUtilities.IsNameToken(tokens[i]) && !namespaceRelative) || tokens[i].Text.StartsWith('$')) continue;
            if (i > 0 && (tokens[i - 1].Text is "." or "::" or "->" or "?."
                || (tokens[i - 1].Text == "\\" && i > 1 && tokens[i - 2].Kind == TokenKind.Identifier
                    && tokens[i - 2].End == tokens[i - 1].Start))) continue;
            var referenceScope = tokenScopes[i] ?? root;
            var absolute = php && i > 0 && tokens[i - 1].Text == "\\";
            var start = i;
            if (!php && tokens[i].Text == "global" && i + 2 < tokens.Count && tokens[i + 1].Text == "::")
            {
                absolute = true;
                i += 2;
            }
            var name = "";
            for (var j = i; j < tokens.Count && !excluded[j] && tokens[j].Kind == TokenKind.Identifier; j += 2)
            {
                name = Qualify(name, tokens[j].Text, separator);
                // Charge repeated chains too: deduplicated evidence must not hide repeated
                // quadratic prefix construction work in the source scanner.
                if (!KeepReferenceText(name.Length))
                    return new(declarations, references) { Truncated = true };
                if (seen.Add((referenceScope, php ? name.ToUpperInvariant() : name, absolute)))
                {
                    var hint = TypeNameHint(referenceScope, name, absolute, php);
                    if (references.Count >= MaxReferences || !KeepReferenceText(hint.Length))
                        return new(declarations, references) { Truncated = true };
                    // This static iterator holds only the shared scope, not the parsed source or
                    // tokens. Never materialize the imports × identifiers Cartesian product here.
                    references.Add(new(name, tokens[start].Line, Candidates(referenceScope, name, absolute, php),
                        referenceScope.Provenance, hint));
                }
                if (j + 2 >= tokens.Count || tokens[j + 1].Text != separator
                    || (php && tokens[j].End != tokens[j + 1].Start)) break;
            }
        }
        return new(declarations, references) { Truncated = truncated };

        bool KeepScopeText(int characters)
        {
            scopeTextCharacters += characters;
            return !(truncated = scopeTextCharacters > scopeTextLimit);
        }

        bool KeepReferenceText(int characters)
        {
            referenceTextCharacters += characters;
            return !(truncated = referenceTextCharacters > referenceTextLimit);
        }

        void Exclude(int start, int end)
        {
            for (var i = start; i <= end; i++) excluded[i] = true;
        }

        bool ReadCSharpUsing(int start, int end, Scope current, int line)
        {
            if (start >= end) return false;
            var isStatic = tokens[start].Text == "static";
            if (isStatic) start++;
            string? alias = null;
            if (start + 1 < end && tokens[start + 1].Text == "=")
            {
                alias = tokens[start].Text;
                start += 2;
            }
            if (start + 1 < end && tokens[start].Text == "global" && tokens[start + 1].Text == "::") start += 2;
            var target = ReadName(tokens, ref start, ".");
            if (target.Length == 0 || start != end) return false;
            if (!KeepScopeText(target.Length + (alias?.Length ?? 0))) return true;
            if (alias is not null) current.Aliases[alias] = target;
            else if (!isStatic) current.Imports.Add(target);
            if (alias is not null || isStatic)
            {
                if (references.Count >= MaxReferences) { truncated = true; return true; }
                references.Add(new(alias ?? target, line, [target], $"imports {target}" + (alias is null ? "" : $" as {alias}"), SimpleName(target, '.')));
            }
            return true;
        }

        void ReadPhpUses(int start, int end, Scope current, int line)
        {
            if (start >= end || tokens[start].Text is "function" or "const") return;
            var prefixEnd = start;
            var prefix = ReadName(tokens, ref prefixEnd, "\\");
            // A group import has one shared prefix: use App\Types\{Thing, Contract as Local};
            var grouped = prefixEnd < end && tokens[prefixEnd].Text == "{";
            if (grouped) start = prefixEnd + 1;
            while (start < end && tokens[start].Text != "}")
            {
                var member = ReadName(tokens, ref start, "\\");
                if (member.Length == 0) break;
                var target = grouped ? Qualify(prefix, member, "\\") : member;
                var alias = member.Split('\\')[^1];
                if (start + 1 < end && tokens[start].Text == "as") { alias = tokens[start + 1].Text; start += 2; }
                if (!KeepScopeText(target.Length + alias.Length)) return;
                current.Aliases[alias] = target;
                if (references.Count >= MaxReferences) { truncated = true; return; }
                references.Add(new(alias, line, [target], $"imports {target}" + (alias == member ? "" : $" as {alias}"), SimpleName(target, '\\')));
                if (start >= end || tokens[start].Text != ",") break;
                start++;
            }
        }
    }

    private static IEnumerable<string> Candidates(Scope current, string name, bool absolute, bool php)
    {
        if (absolute) { yield return name; yield break; }
        var separator = php ? "\\" : ".";
        var first = name.Split(separator)[0];
        for (var owner = current; owner is not null; owner = php ? null : owner.Parent)
        {
            if (!owner.Aliases.TryGetValue(first, out var target)) continue;
            yield return target + name[first.Length..];
            yield break; // An external alias never falls back to a same-spelled local type.
        }
        if (php)
        {
            yield return Qualify(current.Name, name.StartsWith("namespace\\", StringComparison.Ordinal)
                ? name[10..] : name, separator);
            yield break;
        }
        var ns = current.Name;
        while (true)
        {
            yield return Qualify(ns, name, separator);
            var last = ns.LastIndexOf('.');
            if (ns.Length == 0) break;
            ns = last < 0 ? "" : ns[..last];
        }
        for (var owner = current; owner is not null; owner = owner.Parent)
            foreach (var imported in owner.Imports) yield return Qualify(imported, name, separator);
    }

    private static string TypeNameHint(Scope current, string name, bool absolute, bool php)
    {
        var separator = php ? '\\' : '.';
        if (absolute) return SimpleName(name, separator);
        var first = name.Split(separator)[0];
        for (var owner = current; owner is not null; owner = php ? null : owner.Parent)
            if (owner.Aliases.TryGetValue(first, out var target))
                return SimpleName(name.Length == first.Length ? target : name, separator);
        return SimpleName(name, separator);
    }

    private static string SimpleName(string name, char separator) => name[(name.LastIndexOf(separator) + 1)..];

    private static string ReadName(IReadOnlyList<Token> tokens, ref int index, string separator)
    {
        var parts = new List<string>();
        if (separator == "\\" && index < tokens.Count && tokens[index].Text == "\\") index++;
        while (index < tokens.Count && tokens[index].Kind == TokenKind.Identifier)
        {
            parts.Add(tokens[index++].Text);
            if (index >= tokens.Count || tokens[index].Text != separator) break;
            index++;
        }
        return string.Join(separator, parts);
    }

    private static string Qualify(string prefix, string name, string separator) => prefix.Length == 0 ? name : prefix + separator + name;

    private sealed class Scope(string name, Scope? parent, int depth, int end, StringComparer comparer)
    {
        internal string Name { get; } = name;
        internal string Provenance { get; } = name.Length == 0 ? "in the global namespace" : $"in namespace {name}";
        internal Scope? Parent { get; } = parent;
        internal int Depth { get; } = depth;
        internal int End { get; } = end;
        internal Dictionary<string, string> Aliases { get; } = new(comparer);
        internal HashSet<string> Imports { get; } = new(comparer);
    }
}
