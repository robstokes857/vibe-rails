using MintLint;

namespace VibeRails.Services.CodeReports;

/// <summary>Matches qualified lexical type evidence within one language, never global bare names.</summary>
internal sealed class RepositoryReferenceResolver
{
    private readonly Dictionary<string, Dictionary<string, string[]>> declarations = new(StringComparer.Ordinal);

    internal RepositoryReferenceResolver(IEnumerable<(string Path, SourceOutline? Outline)> files)
    {
        foreach (var group in files.Where(file => file.Outline?.ReferenceScope is not null)
            .GroupBy(file => file.Outline!.Language!))
        {
            var comparer = group.Key == "Php" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            declarations[group.Key] = group.SelectMany(file => file.Outline!.ReferenceScope!.Declarations
                    .Select(declaration => (declaration.Name, file.Path)))
                .GroupBy(declaration => declaration.Name, comparer)
                .ToDictionary(names => names.Key, names => names.Select(item => item.Path).Distinct(StringComparer.Ordinal).ToArray(), comparer);
        }
    }

    internal IEnumerable<(string Path, string Evidence)> Resolve(string path, SourceOutline outline)
    {
        if (outline.ReferenceScope is null || outline.Language is null
            || !declarations.TryGetValue(outline.Language, out var names)) yield break;
        foreach (var reference in outline.ReferenceScope.References)
        {
            var matches = reference.Candidates.Where(names.ContainsKey).SelectMany(candidate => names[candidate])
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            // Ambiguous scope evidence stays omitted. Matching another declaration in the source itself
            // is also enough to prevent us from inventing a dependency on an unrelated same-named type.
            if (matches.Length != 1 || matches[0] == path) continue;
            yield return (matches[0], $"{path}:{reference.Line} {reference.Provenance} mentions {reference.Name}");
        }
    }
}
