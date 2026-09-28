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
    /// <summary>Language-specific module import evidence for repository-local resolution.</summary>
    public IReadOnlyList<SourceOutlineImport> ImportEvidence { get; init; } = [];

    /// <summary>True when import evidence stopped at the per-file path-text budget.</summary>
    public bool ImportEvidenceTruncated { get; init; }

    /// <summary>Namespace and alias evidence for scoped lexical C# and PHP references.</summary>
    public SourceReferenceScope? ReferenceScope { get; init; }

    /// <summary>Reads the existing language parser's declarations, imports and identifier tokens.</summary>
    public static SourceOutline Read(string path, string content)
    {
        if (!LanguageRegistry.TryGetParser(Path.GetExtension(path), out var parser))
            return new SourceOutline(null, [], [], []);

        var source = parser.Parse(path, path, content);
        var declarations = new List<SourceOutlineSymbol>();
        var namedTypes = TypeScriptOutlineDeclarations.Read(source);
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
            if (InsideNamedType(namedTypes, function.StartIndex)) continue;
            declarations.Add(new(function.Name, "function", source.Tokens[function.StartIndex].Line));
        }
        declarations.AddRange(namedTypes.Select(type => type.Symbol));

        // The graph labels these as lexical references, never resolved calls. Comments and
        // strings are not identifier tokens, and ambiguous declarations are resolved by the host.
        var references = source.Tokens
            .Where(token => token.Kind == TokenKind.Identifier && token.Text.Length >= 3)
            .DistinctBy(token => token.Text)
            .Select(token => new SourceOutlineSymbol(token.Text, "identifier", token.Line))
            .ToArray();
        var imports = source.ImportSources.AsEnumerable();
        if (source.Language is SourceLanguage.JavaScript or SourceLanguage.TypeScript)
            imports = imports.Concat(ReadReExports(source.Tokens));
        var importEvidence = SourceModuleImports.Read(source.Language, source.Tokens, out var importEvidenceTruncated);
        return new(source.Language.ToString(), namedTypes.Count > 0 ? declarations.OrderBy(symbol => symbol.Line).ToArray() : declarations, references,
            imports.Distinct(StringComparer.Ordinal).ToArray())
        {
            ImportEvidence = importEvidence,
            ImportEvidenceTruncated = importEvidenceTruncated,
            ReferenceScope = SourceReferenceScope.Read(source)
        };
    }

    // Named types are emitted in source order without overlap, so one binary search on the
    // start index replaces scanning every type for every function.
    private static bool InsideNamedType(IReadOnlyList<TypeScriptOutlineDeclaration> types, int index)
    {
        int low = 0, high = types.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (types[middle].StartIndex > index) high = middle - 1;
            else if (types[middle].EndIndex < index) low = middle + 1;
            else return true;
        }
        return false;
    }

    // Re-exports are dependency evidence for the mapper. Keep this separate from the
    // analyzer's import list so this addition does not change saved quality measurements.
    private static IEnumerable<string> ReadReExports(IReadOnlyList<Token> tokens)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Text != "export") continue;
            var start = i + 1;
            if (tokens[start].Text == "type" && start + 1 < tokens.Count) start++;
            if (tokens[start].Text is not ("{" or "*")) continue;
            for (var j = start; j < tokens.Count - 1; j++)
            {
                if (tokens[j].Text is ";" or "export" or "import") break;
                if (tokens[j].Text == "from" && tokens[j + 1].Kind == TokenKind.String)
                {
                    yield return ParserUtilities.StringLiteralValue(tokens[j + 1].Text);
                    break;
                }
                // `export { local }` has no source. Do not scan the next statement.
                if (tokens[j].Text == "}" && tokens[j + 1].Text != "from") break;
            }
        }
    }
}
