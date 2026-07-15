using System;
using System.Collections.Generic;

namespace MintLint;

/// <summary>
/// Keyword lookups scoped to one language.
///
/// A single metric engine scores every language MintLint parses, so matching on bare token
/// text lets one language's keywords score another language's identifiers: <c>when</c> opens
/// a Ruby case arm but is a filter clause in C# and an ordinary identifier in Java;
/// <c>then</c> is a Bash keyword but a JavaScript promise method; <c>require</c> and
/// <c>module</c> are Ruby/PHP keywords but the backbone of CommonJS. Every lookup below is
/// therefore gated on <see cref="SourceLanguage"/>.
///
/// The common sets are the engine's original shared vocabulary — tokens that are reserved,
/// or absent, in all of C#, JS/TS, Python, Go, Rust, C, Java, and C++. Each language then
/// contributes only its own extras, so adding a language cannot move a score in any other.
/// </summary>
internal static class LanguageKeywords
{
    /// <summary>Adds a decision point to cyclomatic complexity.</summary>
    private static readonly string[] CommonDecisions =
    [
        "if", "elif", "for", "foreach", "catch", "except", "case", "match", "loop",
        "select", "do"
    ];

    /// <summary>Costs <c>1 + nesting</c> in cognitive complexity.</summary>
    private static readonly string[] CommonNestingDecisions =
    [
        "for", "foreach", "catch", "except", "switch", "match", "loop", "select", "do"
    ];

    /// <summary>An <c>else if</c> chain link: a flat +1, never a nesting penalty.</summary>
    private static readonly string[] CommonElseIf = ["elif"];

    /// <summary>Loop headers, excluded when they trail a do-while body.</summary>
    private static readonly string[] CommonLoops = ["while"];

    private static readonly string[] CommonLogicalOperators = ["&&", "||", "??", "and", "or"];

    /// <summary>Opens a branch whose condition multiplies NPath by <c>2 + logical operators</c>.</summary>
    private static readonly string[] CommonBranches =
    [
        "if", "elif", "for", "foreach", "catch", "except", "loop"
    ];

    /// <summary>Arms counted inside a <c>switch</c>/<c>match</c>/<c>select</c> body for NPath.</summary>
    private static readonly string[] CommonCaseArms = ["case", "default"];

    private static readonly string[] CommonOperatorKeywords =
    [
        "abstract", "and", "as", "assert", "async", "await", "break", "case", "catch",
        "chan", "checked", "class", "const", "constexpr", "continue", "crate", "defer",
        "def", "default", "delegate", "delete", "del", "do", "dyn", "elif", "else",
        "enum", "event", "except", "explicit", "export", "extends", "extern",
        "fallthrough", "finally", "fixed", "fn", "for", "foreach", "friend", "from",
        "func", "function", "global", "go", "goto", "if", "impl", "implicit", "import",
        "implements", "in", "include", "inline", "instanceof", "interface", "internal",
        "is", "lambda", "let", "lock", "loop", "map", "match", "mod", "move", "mut",
        "namespace", "new", "noexcept", "nonlocal", "not", "of", "operator", "or", "out",
        "override", "package", "params", "pass", "private", "protected", "public", "pub",
        "raise", "range", "readonly", "record", "ref", "required", "requires", "return",
        "sealed", "select", "sizeof", "stackalloc", "static", "struct", "switch",
        "synchronized", "template", "throw", "throws", "trait", "try", "type", "typedef",
        "typename", "typeof", "unchecked", "union", "unsafe", "use", "using", "var",
        "virtual", "void", "volatile", "where", "while", "with", "yield"
    ];

    /// <summary>The shared vocabulary, used by every language that adds no extras of its own.</summary>
    private static readonly KeywordProfile Default = new(
        Decisions: CommonDecisions,
        NestingDecisions: CommonNestingDecisions,
        ElseIfKeywords: CommonElseIf,
        Loops: CommonLoops,
        LogicalOperators: CommonLogicalOperators,
        Branches: CommonBranches,
        CaseArms: CommonCaseArms,
        OperatorKeywords: CommonOperatorKeywords);

    private static readonly Dictionary<SourceLanguage, KeywordProfile> Profiles = BuildProfiles();

    public static KeywordProfile For(SourceLanguage language) =>
        Profiles.TryGetValue(language, out KeywordProfile? profile) ? profile : Default;

    private static Dictionary<SourceLanguage, KeywordProfile> BuildProfiles()
    {
        Dictionary<SourceLanguage, KeywordProfile> profiles = [];
        foreach (SourceLanguage language in Enum.GetValues<SourceLanguage>())
        {
            profiles[language] = BuildProfile(language);
        }

        return profiles;
    }

    private static KeywordProfile BuildProfile(SourceLanguage language) => language switch
    {
        // `unless`/`until` are inverted `if`/`while`, `elsif` chains, `rescue` catches, and
        // `when` opens a case arm. The tokenizer rewrites Ruby's `case` to `switch`, so arms
        // are counted through `when` exactly as C# counts them through `case`.
        SourceLanguage.Ruby => new KeywordProfile(
            Decisions: [.. CommonDecisions, "elsif", "rescue", "unless", "until", "when"],
            NestingDecisions: [.. CommonNestingDecisions, "rescue", "unless"],
            ElseIfKeywords: [.. CommonElseIf, "elsif"],
            Loops: [.. CommonLoops, "until"],
            LogicalOperators: CommonLogicalOperators,
            Branches: [.. CommonBranches, "elsif", "rescue", "unless"],
            CaseArms: [.. CommonCaseArms, "when"],
            OperatorKeywords:
            [
                .. CommonOperatorKeywords, "begin", "elsif", "end", "ensure", "module",
                "require", "require_relative", "rescue", "then", "unless", "until", "when"
            ]),

        SourceLanguage.Php => new KeywordProfile(
            Decisions: [.. CommonDecisions, "elseif"],
            NestingDecisions: CommonNestingDecisions,
            ElseIfKeywords: [.. CommonElseIf, "elseif"],
            Loops: CommonLoops,
            LogicalOperators: CommonLogicalOperators,
            Branches: [.. CommonBranches, "elseif"],
            CaseArms: CommonCaseArms,
            OperatorKeywords:
            [
                .. CommonOperatorKeywords, "elseif", "endforeach", "endfor", "endif",
                "endswitch", "endwhile", "include_once", "require", "require_once"
            ]),

        // The tokenizer rewrites `then`/`do` into synthetic braces and `case` into `switch`,
        // so those never reach the engine as keywords; `until` heads a loop.
        SourceLanguage.Bash => new KeywordProfile(
            Decisions: [.. CommonDecisions, "until"],
            NestingDecisions: CommonNestingDecisions,
            ElseIfKeywords: CommonElseIf,
            Loops: [.. CommonLoops, "until"],
            LogicalOperators: CommonLogicalOperators,
            Branches: CommonBranches,
            CaseArms: CommonCaseArms,
            OperatorKeywords:
            [
                .. CommonOperatorKeywords, "done", "elif", "esac", "fi", "local", "then",
                "trap", "until"
            ]),

        // `-and`/`-or` are PowerShell's logical operators, and `do { } until (...)` mirrors
        // do-while, so `until` is excluded when it trails a do body.
        SourceLanguage.PowerShell => new KeywordProfile(
            Decisions: [.. CommonDecisions, "elseif"],
            NestingDecisions: CommonNestingDecisions,
            ElseIfKeywords: [.. CommonElseIf, "elseif"],
            Loops: [.. CommonLoops, "until"],
            LogicalOperators: [.. CommonLogicalOperators, "-and", "-or"],
            Branches: [.. CommonBranches, "elseif"],
            CaseArms: CommonCaseArms,
            OperatorKeywords:
            [
                .. CommonOperatorKeywords, "begin", "elseif", "end", "filter", "param",
                "process", "trap", "until", "workflow"
            ]),

        _ => Default
    };
}

/// <summary>
/// The keyword vocabulary of a single language, resolved once per metric pass. Every set is
/// closed over that language alone — see <see cref="LanguageKeywords"/> for why.
/// </summary>
internal sealed class KeywordProfile(
    IEnumerable<string> Decisions,
    IEnumerable<string> NestingDecisions,
    IEnumerable<string> ElseIfKeywords,
    IEnumerable<string> Loops,
    IEnumerable<string> LogicalOperators,
    IEnumerable<string> Branches,
    IEnumerable<string> CaseArms,
    IEnumerable<string> OperatorKeywords)
{
    public HashSet<string> Decisions { get; } = new(Decisions, StringComparer.Ordinal);
    public HashSet<string> NestingDecisions { get; } = new(NestingDecisions, StringComparer.Ordinal);
    public HashSet<string> ElseIfKeywords { get; } = new(ElseIfKeywords, StringComparer.Ordinal);
    public HashSet<string> Loops { get; } = new(Loops, StringComparer.Ordinal);
    public HashSet<string> LogicalOperators { get; } = new(LogicalOperators, StringComparer.Ordinal);
    public HashSet<string> Branches { get; } = new(Branches, StringComparer.Ordinal);
    public HashSet<string> CaseArms { get; } = new(CaseArms, StringComparer.Ordinal);
    public HashSet<string> OperatorKeywords { get; } = new(OperatorKeywords, StringComparer.Ordinal);
}
