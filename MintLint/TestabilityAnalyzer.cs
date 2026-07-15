using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Detects the structural seams that make code hard to test in isolation: concrete
/// dependencies constructed inline instead of injected, and calls into ambient/impure
/// APIs (clock, filesystem, network, environment, randomness, console) that cannot be
/// substituted in a test. These are heuristics — indicators of where IoC/DI is missing,
/// not proof — and the catalogs below are intentionally small and meant to be tuned.
/// </summary>
internal static class TestabilityAnalyzer
{
    public static (int HardCodedDependencies, int AmbientDependencies) Analyze(ParsedSource parsed)
    {
        LanguageCatalog catalog = CatalogFor(parsed.Language);
        List<Token> tokens = parsed.Tokens;

        int hardCoded = 0;
        int ambient = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            if (token.Kind != TokenKind.Identifier)
            {
                continue;
            }

            string text = token.Text;

            // `this.history.push()` touches a member that merely shares a global's name;
            // an identifier reached through a member access can never be the ambient
            // root/global itself.
            if (i > 0 && tokens[i - 1].Text is "." or "?." or "::" or "->")
            {
                continue;
            }

            if (catalog.HasNewOperator && text == "new")
            {
                if (IsConcreteConstruction(tokens, i))
                {
                    hardCoded++;
                }

                continue;
            }

            // A free function that reaches outside the program (e.g. `open(...)`, `fetch(...)`).
            if (catalog.AmbientGlobals.Contains(text) && i + 1 < tokens.Count && tokens[i + 1].Text == "(")
            {
                ambient++;
                continue;
            }

            // A variable that is itself the dependency, with no accessor to key off
            // (e.g. PowerShell's `$env:HOME`).
            if (IsAmbientVariable(catalog, text))
            {
                ambient++;
                continue;
            }

            // A member access on an ambient root (e.g. `DateTime.Now`, `os.getenv`,
            // `std::chrono`, `this->dependency`). Indexers count too: PHP superglobals and
            // Ruby's ENV are read as `$_ENV["HOME"]` / `ENV["HOME"]`, never as members.
            if (catalog.AmbientRoots.Contains(text) && i + 1 < tokens.Count &&
                tokens[i + 1].Text is "." or "::" or "->" or "[")
            {
                ambient++;
            }
        }

        return (hardCoded, ambient);
    }

    private static bool IsAmbientVariable(LanguageCatalog catalog, string text)
    {
        foreach (string prefix in catalog.AmbientVariablePrefixes ?? [])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsConcreteConstruction(List<Token> tokens, int newIndex)
    {
        int typeIndex = newIndex + 1;
        if (typeIndex >= tokens.Count || tokens[typeIndex].Kind != TokenKind.Identifier)
        {
            return false;
        }

        string typeName = tokens[typeIndex].Text;

        // `new[] {...}` / `new int[...]` are arrays, and primitives/collections/exceptions
        // are values rather than collaborators, so none of them count as a dependency.
        if (typeIndex + 1 < tokens.Count && tokens[typeIndex + 1].Text == "[")
        {
            return false;
        }

        return !ValueTypeNames.Contains(typeName)
            && !ParserUtilities.IsBuiltInType(typeName)
            && !typeName.EndsWith("Exception", StringComparison.Ordinal);
    }

    private static LanguageCatalog CatalogFor(SourceLanguage language)
    {
        return language switch
        {
            SourceLanguage.CSharp => new LanguageCatalog(
                AmbientRoots:
                [
                    "Console", "File", "Directory", "Path", "Environment", "DateTime", "DateTimeOffset",
                    "Guid", "Random", "Process", "Thread", "Debug", "Trace", "Stopwatch", "AppContext"
                ],
                AmbientGlobals: [],
                HasNewOperator: true),

            SourceLanguage.JavaScript or SourceLanguage.TypeScript => new LanguageCatalog(
                AmbientRoots:
                [
                    "console", "document", "window", "navigator", "history", "location",
                    "localStorage", "sessionStorage", "process", "Date"
                ],
                AmbientGlobals: ["fetch", "require", "setTimeout", "setInterval", "alert", "prompt", "confirm"],
                HasNewOperator: true),

            SourceLanguage.Python => new LanguageCatalog(
                AmbientRoots:
                [
                    "os", "sys", "datetime", "random", "time", "requests", "subprocess",
                    "socket", "shutil", "pathlib", "logging"
                ],
                AmbientGlobals: ["open", "print", "input"],
                HasNewOperator: false),

            SourceLanguage.Go => new LanguageCatalog(
                AmbientRoots:
                [
                    "fmt", "os", "time", "rand", "http", "exec", "net", "log", "slog",
                    "filepath", "runtime"
                ],
                AmbientGlobals: ["panic", "recover"],
                HasNewOperator: false),

            SourceLanguage.Rust => new LanguageCatalog(
                AmbientRoots:
                [
                    "std", "fs", "env", "thread", "time", "Instant", "SystemTime",
                    "File", "Command", "TcpStream"
                ],
                AmbientGlobals: ["println", "eprintln", "panic"],
                HasNewOperator: false),

            SourceLanguage.C => new LanguageCatalog(
                AmbientRoots: [],
                AmbientGlobals:
                [
                    "printf", "fprintf", "scanf", "fopen", "freopen", "remove", "rename",
                    "getenv", "system", "time", "rand", "srand"
                ],
                HasNewOperator: false),

            SourceLanguage.Java => new LanguageCatalog(
                AmbientRoots:
                [
                    "System", "Files", "Paths", "Path", "LocalDateTime", "Instant",
                    "Clock", "Random", "Thread", "Runtime", "ProcessBuilder"
                ],
                AmbientGlobals: [],
                HasNewOperator: true),

            SourceLanguage.Cpp => new LanguageCatalog(
                AmbientRoots:
                [
                    "std", "filesystem", "chrono", "random", "thread", "cout", "cin",
                    "cerr", "clog"
                ],
                AmbientGlobals: ["printf", "fprintf", "scanf", "fopen", "getenv", "system", "time", "rand"],
                HasNewOperator: true),

            SourceLanguage.Php => new LanguageCatalog(
                AmbientRoots: ["$_ENV", "$_SERVER", "$_SESSION", "$_COOKIE", "$_FILES"],
                AmbientGlobals:
                [
                    "curl_exec", "exec", "file_get_contents", "file_put_contents", "fopen",
                    "getenv", "header", "mail", "mt_rand", "passthru", "rand", "shell_exec",
                    "sleep", "system", "time"
                ],
                HasNewOperator: true),

            SourceLanguage.Ruby => new LanguageCatalog(
                AmbientRoots:
                [
                    "ENV", "File", "Dir", "IO", "Kernel", "Net", "Open3", "Process",
                    "Random", "Socket", "Time"
                ],
                AmbientGlobals: ["gets", "open", "print", "printf", "puts", "rand", "sleep", "system"],
                HasNewOperator: false),

            SourceLanguage.Bash => new LanguageCatalog(
                AmbientRoots: [],
                AmbientGlobals: [],
                HasNewOperator: false),

            SourceLanguage.PowerShell => new LanguageCatalog(
                AmbientRoots: ["Environment", "File", "Directory", "Path", "DateTime", "Random"],
                AmbientGlobals: [],
                HasNewOperator: false,
                // `$env:HOME` is a single token, so it is matched by prefix rather than as a
                // root with an accessor.
                AmbientVariablePrefixes: ["$env:"]),

            _ => new LanguageCatalog([], [], HasNewOperator: false)
        };
    }

    private static readonly HashSet<string> ValueTypeNames = new(StringComparer.Ordinal)
    {
        "List", "Dictionary", "HashSet", "SortedSet", "SortedDictionary", "Stack", "Queue",
        "LinkedList", "StringBuilder", "Tuple", "ValueTuple", "KeyValuePair", "Array",
        "ArrayList", "Span", "ReadOnlySpan", "Memory", "Object", "String", "Guid"
    };

    /// <param name="AmbientRoots">Reached through an accessor, e.g. <c>DateTime.Now</c>.</param>
    /// <param name="AmbientGlobals">Called directly, e.g. <c>getenv(...)</c>.</param>
    /// <param name="AmbientVariablePrefixes">
    /// Variables that are ambient on their own, with no accessor to key off: PowerShell's
    /// <c>$env:HOME</c> is one token naming one environment variable.
    /// </param>
    private readonly record struct LanguageCatalog(
        HashSet<string> AmbientRoots,
        HashSet<string> AmbientGlobals,
        bool HasNewOperator,
        string[]? AmbientVariablePrefixes = null);
}
