using System.Collections.Generic;

namespace MintLint;

internal interface ILanguageParser
{
    SourceLanguage Language { get; }

    IReadOnlyCollection<string> Extensions { get; }

    ParsedSource Parse(string path, string displayPath, string source);
}
