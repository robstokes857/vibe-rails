using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Serilog;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// VCA rule-validation MCP tool. Exposed over the in-process HTTP transport at /mcp.
/// MCP normalizes the method name to snake_case (<c>validate_vca</c>).
/// </summary>
[McpServerToolType]
public class RulesTool
{
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly IFileClassifier FileClassifier = new FileClassifier();
    private static readonly StringComparer GitPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison GitPathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record StagedFileSnapshot(
        string RelativePath,
        string FullPath,
        int? ChangedLineCount,
        bool ExistsInIndex,
        string? Content = null);

    private sealed record AgentFileSnapshot(
        string RelativePath,
        string FullPath,
        string Content);

    private sealed record VcaRuleSnapshot(
        string RuleText,
        string Enforcement,
        AgentFileSnapshot Source);

    private enum RuleValidationState
    {
        Passed,
        Violated,
        Deferred
    }

    private sealed record RuleValidationResult(RuleValidationState State, string Message)
    {
        public static RuleValidationResult Pass(string message) =>
            new(RuleValidationState.Passed, message);

        public static RuleValidationResult Violation(string message) =>
            new(RuleValidationState.Violated, message);

        public static RuleValidationResult Deferred(string message) =>
            new(RuleValidationState.Deferred, message);
    }

    private sealed record GitCommandResult(
        int ExitCode,
        string StdOut,
        string StdErr,
        bool TimedOut);

    // Enforcement vocabulary understood in AGENTS.md rule lines. This is a deliberate SUPERSET of
    // Services.EnforcementParser (WARN/COMMIT/STOP): the MCP tool additionally honors the bracket
    // form and the SKIP/DISABLED opt-outs, which the UI-side parser has no concept of. Kept as one
    // constant so the two patterns below can't drift apart from each other.
    private const string EnforcementAlternation = "WARN|COMMIT|STOP|SKIP|DISABLED";

    // Patterns to extract rules from AGENTS.md. The bracket form is the MCP-native
    // format; the suffix form matches agent files produced by the current UI.
    private static readonly Regex BracketRulePattern = new Regex(
        $@"^-\s*\[({EnforcementAlternation})\]\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SuffixRulePattern = new Regex(
        $@"^-\s*(.+?)\s*\(({EnforcementAlternation})\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    [McpServerTool]
    [Description("Validates staged files against VCA rules defined in AGENTS.md files. Supports '- [WARN] Rule' and '- Rule (WARN)' formats. Call this BEFORE attempting to commit changes. Returns validation results with any COMMIT-level violations that require acknowledgment.")]
    public static async Task<string> ValidateVca(
        [Description("Optional working directory. If not provided, uses current directory.")] string? workingDirectory = null)
    {
        var report = await ValidateVcaReportAsync(workingDirectory);
        return report.Output;
    }

    internal static async Task<VcaToolValidationReport> ValidateVcaReportAsync(
        string? workingDirectory = null,
        string? commitMessage = null,
        bool validateCommitMessage = false,
        CancellationToken cancellationToken = default,
        GitStagedSnapshot? stagedSnapshot = null,
        bool workingTreeScope = false)
    {
        // "changed" when validating a working-tree snapshot, "staged" when validating the
        // index — the transcript must name what was actually looked at.
        var scopeNoun = workingTreeScope ? "changed" : "staged";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workDir = stagedSnapshot?.RepositoryPath
                ?? workingDirectory
                ?? Directory.GetCurrentDirectory();

            // Find git root
            var gitRoot = stagedSnapshot?.RepositoryPath ?? FindGitRoot(workDir);
            if (gitRoot == null)
            {
                return VcaToolValidationReport.Pass("SKIP: Not in a git repository.");
            }

            // Get staged files
            var indexPaths = stagedSnapshot == null
                ? await GetIndexPathsAsync(gitRoot, cancellationToken)
                : stagedSnapshot.AgentFiles.Select(file => file.RelativePath).ToHashSet(GitPathComparer);
            var stagedFiles = stagedSnapshot == null
                ? await GetStagedFilesAsync(gitRoot, indexPaths, cancellationToken)
                : stagedSnapshot.Files.Select(file => new StagedFileSnapshot(
                    file.RelativePath,
                    file.FullPath,
                    file.ChangedLineCount,
                    file.ExistsInIndex,
                    file.Content)).ToList();
            if (stagedFiles.Count == 0 && !validateCommitMessage)
            {
                return VcaToolValidationReport.Pass(
                    $"PASS: No {scopeNoun} files to validate.",
                    stagedFileCount: 0);
            }

            // Find AGENTS.md files
            var agentFiles = stagedSnapshot == null
                ? await GetAgentFilesFromIndexAsync(gitRoot, indexPaths, cancellationToken)
                : stagedSnapshot.AgentFiles.Select(file => new AgentFileSnapshot(
                    file.RelativePath,
                    ToFullPath(gitRoot, file.RelativePath),
                    file.Content)).ToList();
            if (agentFiles.Count == 0)
            {
                return VcaToolValidationReport.Pass(
                    "PASS: No AGENTS.md files found. No VCA rules to check.",
                    stagedFiles.Count);
            }

            // Parse rules from AGENTS.md files
            var allRules = new List<VcaRuleSnapshot>();
            foreach (var agentFile in agentFiles)
            {
                allRules.AddRange(ParseRules(agentFile.Content, agentFile.FullPath)
                    .Select(rule => new VcaRuleSnapshot(rule.RuleText, rule.Enforcement, agentFile)));
            }

            if (allRules.Count == 0)
            {
                return VcaToolValidationReport.Pass(
                    "PASS: No VCA rules defined in AGENTS.md files.",
                    stagedFiles.Count);
            }

            // Validate against rules
            var violations = new List<string>();
            var warnings = new List<string>();
            var deferredChecks = new List<string>();
            var commitViolations = new List<(string RuleText, string SourceFile, string Slug)>();
            var requiredAcknowledgments = new List<string>();
            var findings = new List<VcaRuleFinding>();
            var hasStopViolation = false;
            var evaluatedRuleCount = 0;

            foreach (var rule in allRules)
            {
                var ruleText = rule.RuleText;
                var enforcement = rule.Enforcement;
                var sourceFile = rule.Source.FullPath;

                if (enforcement == "SKIP" || enforcement == "DISABLED")
                {
                    continue;
                }

                var scopedFiles = GetScopedFiles(stagedFiles, sourceFile, gitRoot);
                var isCommitMessageRule = IsCommitMessageRule(ruleText);
                if (scopedFiles.Count == 0
                    && !(validateCommitMessage && stagedFiles.Count == 0 && isCommitMessageRule))
                {
                    continue;
                }

                evaluatedRuleCount++;
                // Pass sourceFile for rules that need to check against Files section
                var validation = await ValidateRuleAsync(
                    ruleText,
                    scopedFiles,
                    gitRoot,
                    rule.Source,
                    commitMessage,
                    validateCommitMessage,
                    cancellationToken);

                if (validation.State == RuleValidationState.Deferred)
                {
                    deferredChecks.Add($"[DEFERRED] {ruleText}: {validation.Message}");
                    var sourcePath = GetRuleSourcePath(sourceFile, gitRoot);
                    findings.Add(new VcaRuleFinding(
                        VcaRuleFindingKind.Deferred,
                        enforcement,
                        ruleText,
                        validation.Message,
                        sourcePath,
                        BuildFindingGuidance(
                            VcaRuleFindingKind.Deferred,
                            validation.Message,
                            sourcePath)));
                    continue;
                }

                if (validation.State == RuleValidationState.Violated)
                {
                    var message = validation.Message;
                    var sourceId = GetRuleSourceId(sourceFile, gitRoot);
                    var sourcePath = GetRuleSourcePath(sourceFile, gitRoot);
                    var slug = GenerateRuleSlug(ruleText);

                    if (enforcement == "WARN")
                    {
                        warnings.Add($"[WARN] {ruleText}: {message}");
                        findings.Add(new VcaRuleFinding(
                            VcaRuleFindingKind.Warning,
                            enforcement,
                            ruleText,
                            message,
                            sourcePath,
                            BuildFindingGuidance(
                                VcaRuleFindingKind.Warning,
                                message,
                                sourcePath)));
                    }
                    else if (enforcement == "COMMIT")
                    {
                        commitViolations.Add((ruleText, sourceFile, slug));
                        var token = $"[VCA:{sourceId}:{slug}]";
                        requiredAcknowledgments.Add(token);
                        violations.Add($"[COMMIT] {ruleText}: {message}\n  Acknowledgment needed: {token} Reason: <your explanation>");
                        findings.Add(new VcaRuleFinding(
                            VcaRuleFindingKind.AcknowledgmentRequired,
                            enforcement,
                            ruleText,
                            message,
                            sourcePath,
                            BuildFindingGuidance(
                                VcaRuleFindingKind.AcknowledgmentRequired,
                                message,
                                sourcePath),
                            token));
                    }
                    else if (enforcement == "STOP")
                    {
                        hasStopViolation = true;
                        violations.Add($"[STOP] {ruleText}: {message}\n  This violation CANNOT be overridden. Fix it before committing.");
                        findings.Add(new VcaRuleFinding(
                            VcaRuleFindingKind.Blocked,
                            enforcement,
                            ruleText,
                            message,
                            sourcePath,
                            BuildFindingGuidance(
                                VcaRuleFindingKind.Blocked,
                                message,
                                sourcePath)));
                    }
                }
            }

            // Build response
            var result = new System.Text.StringBuilder();
            result.AppendLine($"Validated {stagedFiles.Count} {scopeNoun} file(s) against {evaluatedRuleCount} applicable rule(s).");
            result.AppendLine();

            if (violations.Count == 0 && warnings.Count == 0 && deferredChecks.Count == 0)
            {
                result.AppendLine("PASS: All VCA rules satisfied.");
                return new VcaToolValidationReport(
                    result.ToString(),
                    HasError: false,
                    HasStopViolation: false,
                    RequiredAcknowledgments: [],
                    StagedFileCount: stagedFiles.Count,
                    ApplicableRuleCount: evaluatedRuleCount,
                    Findings: []);
            }

            if (warnings.Count > 0)
            {
                result.AppendLine("WARNINGS (these won't block commit):");
                foreach (var warning in warnings)
                {
                    result.AppendLine($"  {warning}");
                }
                result.AppendLine();
            }

            if (deferredChecks.Count > 0)
            {
                result.AppendLine("DEFERRED CHECKS (evaluated by a later Git hook):");
                foreach (var deferredCheck in deferredChecks)
                {
                    result.AppendLine($"  {deferredCheck}");
                }
                result.AppendLine();
            }

            if (violations.Count > 0)
            {
                if (hasStopViolation)
                {
                    result.AppendLine("FAIL: STOP-level violations detected. Cannot commit.");
                }
                else
                {
                    result.AppendLine("COMMIT-LEVEL VIOLATIONS (require acknowledgment in commit message):");
                }

                foreach (var violation in violations)
                {
                    result.AppendLine($"  {violation}");
                }

                if (commitViolations.Count > 0 && !hasStopViolation)
                {
                    result.AppendLine();
                    result.AppendLine("To commit, include acknowledgments like:");
                    foreach (var (ruleText, sourceFile, slug) in commitViolations.Take(3))
                    {
                        var sourceId = GetRuleSourceId(sourceFile, gitRoot);
                        result.AppendLine($"  [VCA:{sourceId}:{slug}] Reason: <explain why this is acceptable>");
                    }
                }
            }
            else
            {
                result.AppendLine("PASS: No blocking VCA violations detected.");
            }

            return new VcaToolValidationReport(
                result.ToString(),
                HasError: false,
                HasStopViolation: hasStopViolation,
                RequiredAcknowledgments: requiredAcknowledgments.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StagedFileCount: stagedFiles.Count,
                ApplicableRuleCount: evaluatedRuleCount,
                Findings: findings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to validate VCA rules for working directory {WorkDir}", workingDirectory ?? "current");
            return new VcaToolValidationReport(
                $"ERROR: Failed to validate VCA rules: {ex.Message}",
                HasError: true,
                HasStopViolation: false,
                RequiredAcknowledgments: [],
                StagedFileCount: 0,
                ApplicableRuleCount: 0,
                Findings: []);
        }
    }

    internal static List<(string RuleText, string Enforcement, string SourceFile)> ParseRules(
        string content,
        string sourceFile)
    {
        var rules = new List<(string RuleText, string Enforcement, string SourceFile)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var bracketMatch = BracketRulePattern.Match(line);
            if (bracketMatch.Success)
            {
                var enforcement = bracketMatch.Groups[1].Value.ToUpperInvariant();
                var ruleText = bracketMatch.Groups[2].Value.Trim();
                AddRuleIfNew(rules, seen, ruleText, enforcement, sourceFile);
                continue;
            }

            var suffixMatch = SuffixRulePattern.Match(line);
            if (suffixMatch.Success)
            {
                var ruleText = suffixMatch.Groups[1].Value.Trim();
                var enforcement = suffixMatch.Groups[2].Value.ToUpperInvariant();
                AddRuleIfNew(rules, seen, ruleText, enforcement, sourceFile);
            }
        }

        return rules;
    }

    private static void AddRuleIfNew(
        List<(string RuleText, string Enforcement, string SourceFile)> rules,
        HashSet<string> seen,
        string ruleText,
        string enforcement,
        string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(ruleText))
        {
            return;
        }

        var key = $"{sourceFile}\n{enforcement}\n{ruleText}";
        if (seen.Add(key))
        {
            rules.Add((ruleText, enforcement, sourceFile));
        }
    }

    private static string? FindGitRoot(string startPath)
    {
        var current = startPath;
        while (!string.IsNullOrEmpty(current))
        {
            // In a linked worktree or submodule, `.git` is a FILE (a gitdir pointer), not a
            // directory — checking only Directory.Exists would walk past the real root and
            // wrongly report "not a git repository", silently skipping all VCA validation.
            var dotGit = Path.Combine(current, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return null;
    }

    private static async Task<HashSet<string>> GetIndexPathsAsync(
        string gitRoot,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            gitRoot,
            ["--no-pager", "ls-files", "--cached", "-z"],
            cancellationToken);
        EnsureGitSucceeded(result, "git ls-files --cached", gitRoot);

        return SplitNullDelimited(result.StdOut)
            .Select(NormalizeGitPath)
            .ToHashSet(GitPathComparer);
    }

    private static async Task<List<StagedFileSnapshot>> GetStagedFilesAsync(
        string gitRoot,
        IReadOnlySet<string> indexPaths,
        CancellationToken cancellationToken)
    {
        // `-z` is essential: newline, tab, quote, and backslash are all legal in Git paths.
        // `--no-renames` also keeps numstat records to one unambiguous path apiece.
        var namesResult = await RunGitAsync(
            gitRoot,
            ["--no-pager", "diff", "--cached", "--name-only", "--no-renames", "-z"],
            cancellationToken);
        EnsureGitSucceeded(namesResult, "git diff --cached --name-only", gitRoot);

        var lineCounts = await GetStagedChangedLineCountsAsync(gitRoot, cancellationToken);
        return SplitNullDelimited(namesResult.StdOut)
            .Select(NormalizeGitPath)
            .Where(path => path.Length > 0)
            .Distinct(GitPathComparer)
            .Select(path => new StagedFileSnapshot(
                path,
                ToFullPath(gitRoot, path),
                lineCounts.GetValueOrDefault(path),
                indexPaths.Contains(path)))
            .ToList();
    }

    private static async Task<Dictionary<string, int?>> GetStagedChangedLineCountsAsync(
        string gitRoot,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            gitRoot,
            ["--no-pager", "diff", "--cached", "--numstat", "--no-renames", "-z"],
            cancellationToken);
        EnsureGitSucceeded(result, "git diff --cached --numstat", gitRoot);

        var counts = new Dictionary<string, int?>(GitPathComparer);
        foreach (var record in SplitNullDelimited(result.StdOut))
        {
            var firstTab = record.IndexOf('\t');
            var secondTab = firstTab < 0 ? -1 : record.IndexOf('\t', firstTab + 1);
            if (firstTab < 0 || secondTab < 0)
            {
                throw new InvalidOperationException("git diff --cached --numstat returned a malformed record.");
            }

            var addedText = record[..firstTab];
            var deletedText = record[(firstTab + 1)..secondTab];
            var path = NormalizeGitPath(record[(secondTab + 1)..]);
            if (path.Length == 0)
            {
                continue;
            }

            counts[path] = int.TryParse(addedText, out var added)
                && int.TryParse(deletedText, out var deleted)
                    ? checked(added + deleted)
                    : null;
        }

        return counts;
    }

    private static async Task<List<AgentFileSnapshot>> GetAgentFilesFromIndexAsync(
        string gitRoot,
        IEnumerable<string> indexPaths,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<AgentFileSnapshot>();
        foreach (var relativePath in indexPaths.Where(IsAgentFilePath).OrderBy(path => path, StringComparer.Ordinal))
        {
            var content = await ReadIndexFileAsync(gitRoot, relativePath, cancellationToken);
            snapshots.Add(new AgentFileSnapshot(
                relativePath,
                ToFullPath(gitRoot, relativePath),
                content));
        }

        return snapshots;
    }

    private static bool IsAgentFilePath(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AGENT.md", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadIndexFileAsync(
        string gitRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            gitRoot,
            ["--no-pager", "show", "--no-textconv", $":./{relativePath}"],
            cancellationToken);
        EnsureGitSucceeded(result, $"git show index file '{relativePath}'", gitRoot);
        return result.StdOut;
    }

    private static async Task<RuleValidationResult> ValidateRuleAsync(
        string ruleText,
        List<StagedFileSnapshot> stagedFiles,
        string gitRoot,
        AgentFileSnapshot sourceAgent,
        string? commitMessage,
        bool validateCommitMessage,
        CancellationToken cancellationToken)
    {
        var ruleLower = ruleText.ToLowerInvariant();

        if (ruleLower.Contains("log all file changes", StringComparison.Ordinal))
        {
            var documentedFiles = ParseDocumentedFiles(sourceAgent, gitRoot);
            var undocumentedFiles = stagedFiles
                .Select(file => file.RelativePath)
                .Where(path => !documentedFiles.Contains(path))
                .ToList();

            if (undocumentedFiles.Count > 0)
            {
                var fileList = string.Join(", ", undocumentedFiles.Take(3));
                var suffix = undocumentedFiles.Count > 3
                    ? $" and {undocumentedFiles.Count - 3} more"
                    : string.Empty;
                return RuleValidationResult.Violation(
                    $"{undocumentedFiles.Count} file(s) not documented in AGENTS.md Files section: {fileList}{suffix}");
            }

            return RuleValidationResult.Pass($"All {stagedFiles.Count} changed file(s) are documented");
        }

        if (ruleLower.Contains("file changes", StringComparison.Ordinal)
            && ruleLower.Contains("lines", StringComparison.Ordinal))
        {
            var match = Regex.Match(ruleLower, @">\s*(\d+)");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var threshold))
            {
                return RuleValidationResult.Violation(
                    "UNSUPPORTED: changed-lines rule is missing a numeric '> N lines' threshold.");
            }

            var uncountableFiles = stagedFiles
                .Where(file => file.ChangedLineCount is null)
                .Select(file => file.RelativePath)
                .ToList();
            if (uncountableFiles.Count > 0)
            {
                return RuleValidationResult.Violation(
                    $"UNSUPPORTED: Git could not count changed lines for {string.Join(", ", uncountableFiles.Take(3))} (usually binary content).");
            }

            var documentedFiles = ParseDocumentedFiles(sourceAgent, gitRoot);
            var violations = stagedFiles
                .Where(file => file.ChangedLineCount > threshold)
                .Where(file => !documentedFiles.Contains(file.RelativePath))
                .Select(file => $"{file.RelativePath} ({file.ChangedLineCount} staged lines changed)")
                .ToList();

            return violations.Count == 0
                ? RuleValidationResult.Pass("All staged deltas within threshold or documented")
                : RuleValidationResult.Violation(
                    $"{violations.Count} staged file delta(s) over {threshold} lines not documented: {string.Join(", ", violations.Take(3))}");
        }

        if (ruleLower.Contains("cyclomatic complexity disabled", StringComparison.Ordinal))
        {
            return RuleValidationResult.Pass("Cyclomatic complexity validation is disabled by rule");
        }

        if (ruleLower.Contains("cyclomatic complexity", StringComparison.Ordinal)
            || ruleLower.Contains("complexity <", StringComparison.Ordinal))
        {
            var match = Regex.Match(ruleLower, @"<\s*(\d+)");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var maxComplexity))
            {
                return RuleValidationResult.Violation(
                    "UNSUPPORTED: complexity rule is missing a numeric '< N' threshold.");
            }

            foreach (var file in stagedFiles.Where(file => file.ExistsInIndex && IsCodeFile(file.RelativePath)))
            {
                var content = file.Content
                    ?? await ReadIndexFileAsync(gitRoot, file.RelativePath, cancellationToken);
                var complexity = EstimateCyclomaticComplexity(content);
                if (complexity > maxComplexity)
                {
                    return RuleValidationResult.Violation(
                        $"Staged file '{file.RelativePath}' estimated complexity {complexity} exceeds {maxComplexity}");
                }
            }

            return RuleValidationResult.Pass("All staged files within complexity threshold");
        }

        if (ruleLower.Contains("skip test coverage", StringComparison.Ordinal))
        {
            return RuleValidationResult.Pass("Test coverage validation is disabled by rule");
        }

        if (ruleLower.Contains("test coverage", StringComparison.Ordinal)
            || ruleLower.Contains("coverage minimum", StringComparison.Ordinal))
        {
            var hasChangedProductionCode = stagedFiles.Any(file =>
                file.ExistsInIndex
                && IsCodeFile(file.RelativePath)
                && !IsTestFile(file.RelativePath));
            if (!hasChangedProductionCode)
            {
                return RuleValidationResult.Pass(
                    "No non-test code file is staged; coverage rule is not applicable");
            }

            return RuleValidationResult.Violation(
                "UNSUPPORTED: the Git hook has no coverage report for the staged snapshot, so it cannot verify a coverage percentage.");
        }

        if (ruleLower.Contains("package file changes", StringComparison.Ordinal))
        {
            var packageFiles = stagedFiles
                .Where(file => FileClassifier.IsPackageFile(file.RelativePath))
                .Select(file => file.RelativePath)
                .ToList();

            return packageFiles.Count == 0
                ? RuleValidationResult.Pass("No package files changed")
                : RuleValidationResult.Violation(
                    $"{packageFiles.Count} package file(s) changed: {string.Join(", ", packageFiles.Take(3))}");
        }

        if (ruleLower.Contains("check commit message for", StringComparison.Ordinal))
        {
            var wordsMatch = Regex.Match(ruleText, @":\s*(.+)$");
            var forbiddenWords = wordsMatch.Success
                ? wordsMatch.Groups[1].Value
                    .Split(',')
                    .Select(word => word.Trim())
                    .Where(word => word.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

            if (forbiddenWords.Count == 0)
            {
                return RuleValidationResult.Violation(
                    "UNSUPPORTED: commit-message rule has no comma-separated forbidden words after ':'.");
            }

            if (!validateCommitMessage)
            {
                return RuleValidationResult.Deferred("will be checked against the final commit message by commit-msg");
            }

            var foundWords = forbiddenWords
                .Where(word => Regex.IsMatch(
                    commitMessage ?? string.Empty,
                    $@"\b{Regex.Escape(word)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToList();

            return foundWords.Count == 0
                ? RuleValidationResult.Pass("Commit message contains no forbidden words")
                : RuleValidationResult.Violation(
                    $"Commit message contains forbidden words: {string.Join(", ", foundWords)}");
        }

        return RuleValidationResult.Violation(
            "UNSUPPORTED: this VCA rule has no validator and was not silently accepted.");
    }

    private static bool IsCommitMessageRule(string ruleText) =>
        ruleText.Contains("check commit message for", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> ParseDocumentedFiles(
        AgentFileSnapshot agentFile,
        string gitRoot)
    {
        var documentedFiles = new HashSet<string>(GitPathComparer);
        var sourceDirectory = Path.GetDirectoryName(agentFile.FullPath) ?? gitRoot;
        var root = Path.GetFullPath(gitRoot);

        foreach (var line in GetFilesSectionEntries(agentFile.Content))
        {
            try
            {
                // AGENTS.md paths are relative to the directory containing that AGENTS.md.
                var fullPath = Path.GetFullPath(Path.Combine(
                    sourceDirectory,
                    line.Replace('/', Path.DirectorySeparatorChar)));
                var relativePath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                if (!relativePath.Equals("..", StringComparison.Ordinal)
                    && !relativePath.StartsWith("../", StringComparison.Ordinal))
                {
                    documentedFiles.Add(NormalizeGitPath(relativePath));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Log.Warning(ex, "Ignoring invalid Files entry {Entry} from {AgentFile}", line, agentFile.RelativePath);
            }
        }

        return documentedFiles;
    }

    private static IEnumerable<string> GetFilesSectionEntries(string content)
    {
        var inFilesSection = false;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("## Files", StringComparison.OrdinalIgnoreCase))
            {
                inFilesSection = true;
                continue;
            }

            if (inFilesSection && trimmed.StartsWith("##", StringComparison.Ordinal))
            {
                yield break;
            }

            if (!inFilesSection || trimmed.Length == 0)
            {
                continue;
            }

            var entry = trimmed.TrimStart('-', '*', ' ').Trim();
            var linkMatch = Regex.Match(entry, @"\]\(([^)]+)\)");
            if (linkMatch.Success)
            {
                entry = linkMatch.Groups[1].Value;
            }

            entry = entry.Trim('`');
            var descriptionSeparator = entry.IndexOf(": ", StringComparison.Ordinal);
            if (descriptionSeparator >= 0)
            {
                entry = entry[..descriptionSeparator].Trim();
            }

            if (entry.Length > 0)
            {
                yield return entry;
            }
        }
    }

    private static List<StagedFileSnapshot> GetScopedFiles(
        IReadOnlyList<StagedFileSnapshot> stagedFiles,
        string sourceFile,
        string gitRoot)
    {
        var root = Path.GetFullPath(gitRoot);
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceFile)) ?? root;
        var relativeDirectory = Path.GetRelativePath(root, sourceDirectory).Replace('\\', '/');

        if (relativeDirectory == ".")
        {
            return stagedFiles.ToList();
        }

        if (relativeDirectory.Equals("..", StringComparison.Ordinal)
            || relativeDirectory.StartsWith("../", StringComparison.Ordinal))
        {
            return [];
        }

        var prefix = relativeDirectory.TrimEnd('/') + "/";
        return stagedFiles
            .Where(file => file.RelativePath.StartsWith(prefix, GitPathComparison))
            .ToList();
    }

    private static string GetRuleSourceId(string sourceFile, string gitRoot)
    {
        return GetRuleSourcePath(sourceFile, gitRoot).Replace('/', '-');
    }

    private static string GetRuleSourcePath(string sourceFile, string gitRoot) =>
        NormalizeGitPath(Path.GetRelativePath(gitRoot, sourceFile).Replace('\\', '/'));

    private static string BuildFindingGuidance(
        VcaRuleFindingKind kind,
        string reason,
        string sourcePath)
    {
        if (reason.StartsWith("UNSUPPORTED:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Git Guard cannot evaluate this rule as written. Update or disable it in {sourcePath}, or replace it with a supported rule before committing.";
        }

        return kind switch
        {
            VcaRuleFindingKind.Warning =>
                "Review this finding before committing. WARN rules do not block the commit.",
            VcaRuleFindingKind.Deferred =>
                "No pre-commit action is required. This rule will run against the final commit message.",
            VcaRuleFindingKind.AcknowledgmentRequired =>
                "Fix the issue, or include the shown acknowledgment token and a meaningful reason in the final commit message.",
            VcaRuleFindingKind.Blocked =>
                "Fix the issue, stage the correction, and run validation again. STOP rules cannot be acknowledged or bypassed.",
            _ => "Review this finding before committing."
        };
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message, TimedOut: false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GitCommandTimeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillGitProcess(process);
        }
        catch (OperationCanceledException)
        {
            TryKillGitProcess(process);
            throw;
        }

        if (!process.HasExited)
        {
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(2)));
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitCommandResult(
            process.HasExited ? process.ExitCode : -1,
            stdout,
            stderr,
            timedOut);
    }

    private static void EnsureGitSucceeded(
        GitCommandResult result,
        string operation,
        string gitRoot)
    {
        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"{operation} timed out after {(int)GitCommandTimeout.TotalSeconds} seconds in {gitRoot}.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed with exit code {result.ExitCode} in {gitRoot}: {result.StdErr.Trim()}");
        }
    }

    private static void TryKillGitProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort while preserving the original timeout or cancellation.
        }
    }

    private static IEnumerable<string> SplitNullDelimited(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeGitPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string ToFullPath(string gitRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(
            gitRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static bool IsCodeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".fs" or ".vb"
            or ".js" or ".jsx" or ".ts" or ".tsx"
            or ".py" or ".java" or ".kt" or ".kts"
            or ".go" or ".rb" or ".rs"
            or ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp"
            or ".php" or ".swift" or ".scala";
    }

    private static bool IsTestFile(string path)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        return fileName.Contains("test") || fileName.Contains("spec") ||
               path.Contains("test", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("spec", StringComparison.OrdinalIgnoreCase);
    }

    private static int EstimateCyclomaticComplexity(string content)
    {
        // Simple estimation based on decision points
        var keywords = new[] { "if", "else", "while", "for", "foreach", "switch", "case", "catch", "&&", "||", "?" };
        int complexity = 1;
        foreach (var keyword in keywords)
        {
            complexity += Regex.Matches(content, $@"\b{Regex.Escape(keyword)}\b", RegexOptions.IgnoreCase).Count;
        }
        return complexity;
    }

    private static string GenerateRuleSlug(string ruleName)
    {
        var slug = ruleName.ToLowerInvariant()
            .Replace("require ", "")
            .Replace("minimum ", "")
            .Replace("cyclomatic ", "")
            .Replace("complexity ", "complexity-")
            .Replace("test coverage", "test-coverage")
            .Replace("file changes", "file-changes")
            .Replace("log all", "log-all")
            .Replace(" > ", "-over-")
            .Replace(" < ", "-under-")
            .Replace(" ", "-")
            .Replace("%", "")
            .Trim('-');

        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        return slug;
    }
}

internal sealed record VcaToolValidationReport(
    string Output,
    bool HasError,
    bool HasStopViolation,
    IReadOnlyList<string> RequiredAcknowledgments,
    int StagedFileCount,
    int ApplicableRuleCount,
    IReadOnlyList<VcaRuleFinding> Findings)
{
    public static VcaToolValidationReport Pass(
        string output,
        int stagedFileCount = 0,
        int applicableRuleCount = 0) =>
        new(
            output,
            HasError: false,
            HasStopViolation: false,
            RequiredAcknowledgments: [],
            StagedFileCount: stagedFileCount,
            ApplicableRuleCount: applicableRuleCount,
            Findings: []);
}
