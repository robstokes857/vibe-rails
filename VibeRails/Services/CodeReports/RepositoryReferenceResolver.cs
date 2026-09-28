using MintLint;

namespace VibeRails.Services.CodeReports;

/// <summary>Matches qualified lexical type evidence within one language, never global bare names.</summary>
internal sealed class RepositoryReferenceResolver
{
    internal const int MaxCandidatesPerFile = 65_536;
    private const int MaxCandidatesPerGraph = 1_048_576;
    private const int MaxCandidateCharactersPerFile = 4 * 1024 * 1024;
    private const int MaxCandidateCharactersPerGraph = 32 * 1024 * 1024;
    private readonly Dictionary<string, Dictionary<string, string[]>> declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> typeNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> limitedFiles = new(StringComparer.Ordinal);
    private int candidatesExamined;
    private long candidateCharacters;

    internal int LimitedFileCount => limitedFiles.Count;

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
            typeNames[group.Key] = declarations[group.Key].Keys
                .Select(name => name[(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('\\')) + 1)..]).ToHashSet(comparer);
        }
    }

    internal IEnumerable<(string Path, string Evidence)> Resolve(string path, SourceOutline outline,
        CancellationToken cancellationToken = default)
    {
        if (outline.ReferenceScope is null || outline.Language is null
            || !declarations.TryGetValue(outline.Language, out var names)) yield break;
        var fileCandidates = 0;
        long fileCharacters = 0;
        foreach (var reference in outline.ReferenceScope.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Most identifiers are fields, parameters or locals. Do not expand imports at all
            // unless some mapped declaration has the final type name (after any import alias).
            if (reference.TypeNameHint is { } hint && !typeNames[outline.Language].Contains(hint)) continue;
            if (candidatesExamined >= MaxCandidatesPerGraph || candidateCharacters >= MaxCandidateCharactersPerGraph)
            {
                limitedFiles.Add(path);
                yield break;
            }
            string? match = null;
            var ambiguous = false;
            foreach (var candidate in reference.Candidates)
            {
                if (++fileCandidates > MaxCandidatesPerFile || ++candidatesExamined > MaxCandidatesPerGraph
                    || (fileCharacters += candidate.Length) > MaxCandidateCharactersPerFile
                    || (candidateCharacters += candidate.Length) > MaxCandidateCharactersPerGraph)
                {
                    limitedFiles.Add(path);
                    // Do not emit a partial match: an unexamined candidate could make it ambiguous.
                    yield break;
                }
                if ((fileCandidates & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!names.TryGetValue(candidate, out var paths)) continue;
                foreach (var target in paths)
                {
                    if (match is not null && match != target) { ambiguous = true; break; }
                    match = target;
                }
                if (ambiguous) break;
            }
            // Ambiguous scope evidence stays omitted. Matching another declaration in the source itself
            // is also enough to prevent us from inventing a dependency on an unrelated same-named type.
            if (ambiguous || match is null || match == path) continue;
            yield return (match, $"{path}:{reference.Line} {reference.Provenance} mentions {reference.Name}");
        }
    }
}
