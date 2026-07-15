using System;

namespace MintLint;

internal static class LanguageRegistry
{
    private static readonly ILanguageParser[] Parsers =
    [
        new JavaScriptLanguageParser(),
        new TypeScriptLanguageParser(),
        new PythonLanguageParser(),
        new CSharpLanguageParser(),
        new GoLanguageParser(),
        new RustLanguageParser(),
        new CLanguageParser(),
        new JavaLanguageParser(),
        new CppLanguageParser(),
        new PhpLanguageParser(),
        new RubyLanguageParser(),
        new BashLanguageParser(),
        new PowerShellLanguageParser()
    ];

    public static bool TryGetParser(string extension, out ILanguageParser parser)
    {
        foreach (ILanguageParser candidate in Parsers)
        {
            foreach (string supported in candidate.Extensions)
            {
                if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase))
                {
                    parser = candidate;
                    return true;
                }
            }
        }

        parser = Parsers[0];
        return false;
    }
}
