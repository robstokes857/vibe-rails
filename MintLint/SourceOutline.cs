using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MintLint;

/// <summary>A declaration or identifier at its original source line.</summary>
public sealed record SourceOutlineSymbol(string Name, string Kind, int Line);

/// <summary>Parser evidence for a repository map, without running or grading the code.</summary>
/// <remarks><paramref name="Language"/> is null when no parser claims the extension, so hosts omit
/// the field instead of publishing an empty language name.</remarks>
public sealed record SourceOutline(
    string? Language,
    IReadOnlyList<SourceOutlineSymbol> Declarations,
    IReadOnlyList<SourceOutlineSymbol> References,
    IReadOnlyList<string> Imports)
{
    /// <summary>Reads the existing language parser's declarations, imports and identifier tokens.</summary>
    public static SourceOutline Read(string path, string content)
    {
        if (!LanguageRegistry.TryGetParser(Path.GetExtension(path), out var parser))
            return new SourceOutline(null, [], [], []);

        var source = parser.Parse(path, path, content);
        var declarations = new List<SourceOutlineSymbol>();
        foreach (var type in source.Classes)
        {
            var token = source.Tokens[type.StartIndex];
            // The parser groups every named type together, so "class" here means "named type":
            // structs, enums, records, traits and Go type declarations all land in this bucket.
            var kind = token.Text == "interface" ? "interface" : "class";
            declarations.Add(new(type.Name, kind, token.Line));
        }
        foreach (var function in source.Functions)
        {
            if (function.Name is "global" or "anonymous" || function.Name.Length == 0) continue;
            declarations.Add(new(function.Name, "function", source.Tokens[function.StartIndex].Line));
        }

        // The graph labels these as lexical references, never resolved calls. Comments and
        // strings are not identifier tokens, and ambiguous declarations are resolved by the host.
        var references = source.Tokens
            .Where(token => token.Kind == TokenKind.Identifier && token.Text.Length >= 3)
            .DistinctBy(token => token.Text)
            .Select(token => new SourceOutlineSymbol(token.Text, "identifier", token.Line))
            .ToArray();
        return new(source.Language.ToString(), declarations, references, source.ImportSources.ToArray());
    }
}
