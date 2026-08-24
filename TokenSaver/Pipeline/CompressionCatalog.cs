using TokenSaver.Minify;
using TokenSaver.Shape;

namespace TokenSaver.Pipeline;

/// <summary>
/// What a stage costs you if it misfires. Since the curated-set decision (2026-07-18) there is no
/// per-stage settings UI; the kind still classifies risk for captures, the preview, and the
/// default-set review below. A Lossy stage may only be on by default when it leaves an explicit
/// marker where the destroyed information used to be, so the model always knows something was
/// removed and can re-run the command if it needs the rest.
/// </summary>
public enum StageKind
{
    /// <summary>Output is a subsequence of the input: only bytes a real terminal would have
    /// discarded are removed. Worst case is lost savings, never lost meaning.</summary>
    Lossless,

    /// <summary>Same facts, different layout (grep matches regrouped under their file). Nothing is
    /// dropped, but the model sees a shape it did not ask for.</summary>
    Reshaping,

    /// <summary>Information is genuinely destroyed and an explicit marker (`[xN]`,
    /// `[... N lines elided ...]`) is left in its place.</summary>
    Lossy,
}

/// <summary>One toggleable transform, in pipeline order.</summary>
/// <param name="Id">Stable id. Persisted in settings.json AND in every capture row, so renaming one
/// silently re-reads history as "stage disabled". Treat these as a wire format.</param>
/// <param name="Order">Execution order. See <see cref="CompressionCatalog"/> remarks — this is
/// declaration order made explicit so the UI, the README, and the runbook can all render the real
/// pipeline instead of three hand-maintained lists that drift.</param>
public sealed record CompressionStageInfo(
    string Id,
    string Name,
    string Summary,
    StageKind Kind,
    bool OnByDefault,
    int Order);

/// <summary>
/// Which tools' output the pipeline is allowed to touch. A separate axis from the stages: "what do
/// we rewrite" and "what do we rewrite it with" fail in completely different ways, and conflating
/// them is how you end up unable to answer "why did my Edit break".
/// </summary>
public sealed record CompressionScopeInfo(
    string Id,
    string Name,
    string Summary,
    IReadOnlyList<string> AnthropicTools,
    IReadOnlyList<string> CodexTools,
    IReadOnlyList<string> OpenCodeTools,
    IReadOnlyList<string> GrokTools,
    bool OnByDefault,
    string? Warning = null);

/// <summary>
/// The single source of truth for what the token saver can do. <see cref="DefaultSelection"/> is
/// the curated set the pipeline runs whenever the saver is on (per-LLM on/off is the only setting
/// exposed since 2026-07-18; <c>TokenSaverStageOverride</c> in settings.json is the hand-edit
/// escape hatch), the what-if preview in Vibe AI resolves ids from here, and
/// TokenSaver/README.md documents this table. Add a stage in exactly one place: this file.
///
/// Pipeline order is fixed and deliberate — it is not a user preference, because the stages are not
/// commutative:
///
///   1. Terminal cleanup (T1..T4)  — normalize first, so everything downstream sees plain \n text.
///   2. Shape filters              — regroup a known command's output while it is still verbatim.
///   3. Condense (D, then T)       — lossless dedupe gets first shot at shrinking the payload
///                                   below the truncation threshold, so truncation fires less often.
///
/// Running shape filters before cleanup would make them parse ANSI; running truncation before
/// dedupe would elide lines that dedupe could have collapsed for free.
///
/// EXECUTION NOTE (this trips people up): stages 1-5 are listed here as five independent toggles,
/// but they do NOT execute as five passes. <see cref="OutputMinifier"/> fuses them into one forward
/// scan, because its idempotency proof is cross-transform — e.g. with CR-collapse off, a bare CR
/// aborts the whole string, precisely because a later pass's trailing-whitespace deletion could
/// make that CR line-final and reclassify it. Splitting them into five real passes would void that
/// proof. They are individually killable (which is what the toggles give you); they are not
/// individually schedulable. Same story for stages 10-11 and <see cref="OutputCondenser"/>.
/// </summary>
public static class CompressionCatalog
{
    // ---- Stage ids. Wire format: persisted in settings.json and in CompressionCaptures rows. ----
    public const string CrCollapse = "cr-collapse";
    public const string CrLfNormalize = "crlf-normalize";
    public const string AnsiStrip = "ansi-strip";
    public const string TrailingWhitespace = "trailing-whitespace";
    public const string BlankEdges = "blank-edges";
    public const string BlankRuns = "blank-runs";
    public const string GitStatusGroup = "git-status-group";
    public const string GrepGroup = "grep-group";
    public const string FindGroup = "find-group";
    public const string ElidePassedTests = "elide-passed-tests";
    public const string DedupeLines = "dedupe-lines";
    public const string TruncateLong = "truncate-long";

    // ---- Scope ids. Same wire-format rules. ----
    public const string ScopeShell = "scope-shell";
    public const string ScopeShellBackground = "scope-shell-background";
    public const string ScopeRead = "scope-read";
    public const string ScopeGrep = "scope-grep";

    /// <summary>
    /// The pipeline, in execution order.
    ///
    /// On the two defaults that look surprising: <see cref="CrCollapse"/> and
    /// <see cref="AnsiStrip"/> are the two largest lossless wins available and both are OFF by
    /// deliberate product decision (2026-07-15) — they reshape terminal-looking output and the
    /// owner wants to opt into that consciously, not inherit it. Do not "fix" this by flipping them
    /// on; flip them in the UI if you want them.
    ///
    /// <see cref="CrLfNormalize"/> exists because that decision had a cost nobody intended:
    /// CRLF→LF normalization was bundled into <see cref="CrCollapse"/>, so with it off every stage
    /// from <see cref="GitStatusGroup"/> down fails open on the surviving \r and no Windows payload
    /// (rg, PowerShell, dotnet) was ever compressed by more than trailing whitespace. Splitting the
    /// line-ending rewrite out restores those stages without touching a single redraw frame.
    /// </summary>
    public static readonly IReadOnlyList<CompressionStageInfo> Stages =
    [
        new(CrCollapse, "Collapse progress redraws",
            "Keeps only the final frame of a \\r-redrawn line (progress bars, spinners), and only "
            + "when that frame provably covers every earlier one. Also normalizes CRLF to LF.",
            StageKind.Lossless, OnByDefault: false, Order: 1),

        new(CrLfNormalize, "Normalize CRLF line endings",
            "Rewrites Windows \\r\\n line endings to \\n. Every later stage fails open on a "
            + "surviving \\r, so without this the group, elide, dedupe and truncate stages all "
            + "no-op on output from rg, PowerShell and dotnet.",
            StageKind.Lossless, OnByDefault: true, Order: 2),

        new(AnsiStrip, "Strip ANSI styling",
            "Drops colour codes (SGR), window-title sequences (OSC) and bells. Cursor moves and "
            + "erases are left byte-verbatim.",
            StageKind.Lossless, OnByDefault: false, Order: 3),

        new(TrailingWhitespace, "Strip trailing whitespace",
            "Spaces and tabs at end of line.",
            StageKind.Lossless, OnByDefault: true, Order: 4),

        new(BlankEdges, "Trim blank edges",
            "Blank lines at the very start and end of the output.",
            StageKind.Lossless, OnByDefault: true, Order: 5),

        new(BlankRuns, "Collapse blank runs",
            "Runs of 3 or more consecutive blank lines become 2.",
            StageKind.Lossless, OnByDefault: true, Order: 6),

        new(GitStatusGroup, "Group git status",
            "Regroups porcelain `XY path` status lines under one header per status. Only fires "
            + "when the command was a recognised `git status --short`/`--porcelain=v1`.",
            StageKind.Reshaping, OnByDefault: true, Order: 7),

        new(GrepGroup, "Group grep matches",
            "Regroups `path:line:content` matches under one header per file. Only fires when the "
            + "command was a recognised `grep`/`rg` with an explicit -n.",
            StageKind.Reshaping, OnByDefault: true, Order: 8),

        new(FindGroup, "Group find results",
            "Regroups a flat path list under one header per directory. Only fires when the command "
            + "was a recognised `find`.",
            StageKind.Reshaping, OnByDefault: true, Order: 9),

        // The three lossy stages ship ON (2026-07-18/19, curated-set decision): they are the
        // largest wins on the exact payloads that burn tokens (log/build/test spew), and each
        // leaves an explicit marker where content was removed, so the model can always tell and
        // re-run the command with a narrower filter if it needs what was elided.
        new(ElidePassedTests, "Collapse passing tests",
            "Replaces each run of individual passing-test lines (pytest -v, jest/vitest, dotnet "
            + "test, go test -v, cargo test) with a `[... N passed ...]` marker. Failures, "
            + "errors, skips, logs and summaries are kept verbatim, in place. Only fires when the "
            + "command was a recognised test runner. Running before truncation also keeps failure "
            + "details out of truncate-long's elided middle.",
            StageKind.Lossy, OnByDefault: true, Order: 10),

        new(DedupeLines, "Collapse repeated lines",
            "A run of 3+ identical lines becomes one instance tagged `[xN]`. Lossy: the repeat "
            + "count survives, the repeats do not.",
            StageKind.Lossy, OnByDefault: true, Order: 11),

        new(TruncateLong, "Truncate very long output",
            "Keeps the first 150 and last 50 lines and replaces the middle with a "
            + "`[... N lines elided ...]` marker. Lossy: the middle is gone.",
            StageKind.Lossy, OnByDefault: true, Order: 12),
    ];

    /// <summary>
    /// Which tools the pipeline may touch.
    ///
    /// <see cref="ScopeRead"/> and <see cref="ScopeGrep"/> are the two biggest theoretical wins and
    /// both ship OFF. The reason is specific, not squeamishness: the model builds Edit
    /// <c>old_string</c> values out of Read output, so a rewritten Read means a FAILED EDIT, not
    /// merely a lost saving. They exist as toggles so the capture log can answer "would this have
    /// been safe" with evidence instead of a guess. Read runbooks/compress_runbook.md before
    /// turning either on.
    /// </summary>
    public static readonly IReadOnlyList<CompressionScopeInfo> Scopes =
    [
        new(ScopeShell, "Shell output",
            "Output of Bash and PowerShell tool calls.",
            AnthropicTools: ["Bash", "PowerShell"],
            // "exec" is Codex code-mode (plan_1A A1, 2026-08-16): 61% of all tool-output wire
            // chars rode on it unrewritten. Its command is JavaScript source, which never
            // classifies a CommandShape, so only minify + condense ever run on exec output.
            CodexTools: ["shell_command", "exec_command", "exec"],
            OpenCodeTools: ["bash"],
            // Native Grok's shell tool wire name (config section [toolset.bash], wire name
            // run_terminal_command — read from a live /responses body, grok 1.0.5).
            GrokTools: ["run_terminal_command"],
            OnByDefault: true),

        new(ScopeShellBackground, "Background shell output",
            "Output of background shell reads (BashOutput). Same content class as Bash.",
            AnthropicTools: ["BashOutput"],
            // "wait" reads background-job output — Codex's moral equivalent of BashOutput
            // (plan_1A A2, 2026-08-16). Its arguments carry no command, so shapes never fire.
            CodexTools: ["write_stdin", "wait"],
            OpenCodeTools: [],
            // Reads background command/subagent output — Grok's BashOutput equivalent.
            GrokTools: ["get_command_or_subagent_output"],
            OnByDefault: true),

        new(ScopeRead, "File reads",
            "Output of the Read tool.",
            AnthropicTools: ["Read"],
            CodexTools: [],
            OpenCodeTools: ["read"],
            GrokTools: ["read_file"],
            OnByDefault: false,
            Warning: "The model builds Edit old_string values from Read output. Rewriting it can "
                   + "cause failed edits, not just smaller savings. Off by default on purpose."),

        new(ScopeGrep, "Grep results",
            "Output of the Grep tool.",
            AnthropicTools: ["Grep"],
            CodexTools: [],
            OpenCodeTools: ["grep"],
            GrokTools: ["grep"],
            OnByDefault: false,
            Warning: "Lower risk than Read, but the model still navigates by these line numbers. "
                   + "Verify against captures before trusting it."),
    ];

    /// <summary>
    /// The curated set: every stage and scope marked OnByDefault. This is what the pipeline runs
    /// whenever a provider's saver is on and no <c>TokenSaverStageOverride</c> is present — the
    /// per-stage picker UI was removed 2026-07-18.
    /// </summary>
    public static IReadOnlyList<string> DefaultSelection { get; } =
    [
        .. Stages.Where(s => s.OnByDefault).Select(s => s.Id),
        .. Scopes.Where(s => s.OnByDefault).Select(s => s.Id),
    ];

    private static readonly HashSet<string> KnownIds =
        [.. Stages.Select(s => s.Id), .. Scopes.Select(s => s.Id)];

    /// <summary>True when <paramref name="id"/> is a stage or scope this build understands.</summary>
    public static bool IsKnownId(string id) => KnownIds.Contains(id);

    /// <summary>
    /// Resolves a persisted id set into the concrete knobs the rewriters take. Unknown ids are
    /// ignored rather than rejected: a settings.json written by a newer build must degrade to
    /// "that stage is off here", never to a hard failure on an LLM request.
    ///
    /// A null selection means "never configured" and resolves to <see cref="DefaultSelection"/>. An
    /// EMPTY selection is a real, honoured choice — everything off — and must not be confused with
    /// null, which is why this takes a nullable collection rather than defaulting the parameter.
    /// </summary>
    public static CompressionPlan Resolve(IReadOnlyCollection<string>? selection)
    {
        var enabled = new HashSet<string>(selection ?? DefaultSelection, StringComparer.Ordinal);

        var flags = new MinifyFlags(
            CollapseCrRedraws: enabled.Contains(CrCollapse),
            StripAnsiStyling: enabled.Contains(AnsiStrip),
            StripTrailingWhitespace: enabled.Contains(TrailingWhitespace),
            TrimBlankLineEdges: enabled.Contains(BlankEdges),
            CollapseBlankLineRuns: enabled.Contains(BlankRuns),
            NormalizeCrLf: enabled.Contains(CrLfNormalize));

        var condense = new CondenseOptions(
            DedupeConsecutiveLines: enabled.Contains(DedupeLines),
            TruncateLongOutput: enabled.Contains(TruncateLong));

        var shapes = new HashSet<CommandShape>();
        if (enabled.Contains(GitStatusGroup))
            shapes.Add(CommandShape.GitStatus);
        if (enabled.Contains(GrepGroup))
            shapes.Add(CommandShape.GrepMatches);
        if (enabled.Contains(FindGroup))
            shapes.Add(CommandShape.PathList);
        if (enabled.Contains(ElidePassedTests))
            shapes.Add(CommandShape.TestRun);

        return new CompressionPlan(
            flags,
            condense,
            shapes,
            AnthropicAllowlist: ToolsFor(enabled, s => s.AnthropicTools),
            CodexAllowlist: ToolsFor(enabled, s => s.CodexTools),
            ZaiAllowlist: ToolsFor(enabled, s => s.OpenCodeTools),
            GrokAllowlist: ToolsFor(enabled, s => s.GrokTools),
            EnabledIds: enabled);
    }

    private static IReadOnlyList<string> ToolsFor(
        HashSet<string> enabled, Func<CompressionScopeInfo, IReadOnlyList<string>> pick) =>
        [.. Scopes.Where(s => enabled.Contains(s.Id)).SelectMany(pick)];
}
