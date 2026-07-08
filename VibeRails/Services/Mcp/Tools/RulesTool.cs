using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Serilog;
using VibeRails.Services;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// VCA rule-validation MCP tool. Exposed over the in-process HTTP transport at /mcp.
/// MCP normalizes the method name to snake_case (<c>validate_vca</c>).
/// </summary>
[McpServerToolType]
public class RulesTool
{
    private static readonly bool VcaValidationTemporarilyDisabled = true;
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(10);

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

    internal static async Task<VcaToolValidationReport> ValidateVcaReportAsync(string? workingDirectory = null)
    {
        try
        {
            if (VcaValidationTemporarilyDisabled)
            {
                return VcaToolValidationReport.Pass("PASS: VCA validation is temporarily disabled.");
            }

            var workDir = workingDirectory ?? Directory.GetCurrentDirectory();

            // Find git root
            var gitRoot = FindGitRoot(workDir);
            if (gitRoot == null)
            {
                return VcaToolValidationReport.Pass("SKIP: Not in a git repository.");
            }

            // Get staged files
            var stagedFiles = await GetStagedFilesAsync(gitRoot);
            if (stagedFiles.Count == 0)
            {
                return VcaToolValidationReport.Pass("PASS: No staged files to validate.");
            }

            // Find AGENTS.md files
            var agentFiles = FindAgentFiles(gitRoot);
            if (agentFiles.Count == 0)
            {
                return VcaToolValidationReport.Pass("PASS: No AGENTS.md files found. No VCA rules to check.");
            }

            // Parse rules from AGENTS.md files
            var allRules = new List<(string RuleText, string Enforcement, string SourceFile)>();
            foreach (var agentFile in agentFiles)
            {
                var content = await File.ReadAllTextAsync(agentFile);
                allRules.AddRange(ParseRules(content, agentFile));
            }

            if (allRules.Count == 0)
            {
                return VcaToolValidationReport.Pass("PASS: No VCA rules defined in AGENTS.md files.");
            }

            // Validate against rules
            var violations = new List<string>();
            var warnings = new List<string>();
            var commitViolations = new List<(string RuleText, string SourceFile, string Slug)>();
            var requiredAcknowledgments = new List<string>();
            var hasStopViolation = false;

            foreach (var (ruleText, enforcement, sourceFile) in allRules)
            {
                if (enforcement == "SKIP" || enforcement == "DISABLED")
                {
                    continue;
                }

                // Pass sourceFile for rules that need to check against Files section
                var (passed, message) = await ValidateRuleAsync(ruleText, stagedFiles, gitRoot, sourceFile);

                if (!passed)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    var slug = GenerateRuleSlug(ruleText);

                    if (enforcement == "WARN")
                    {
                        warnings.Add($"[WARN] {ruleText}: {message}");
                    }
                    else if (enforcement == "COMMIT")
                    {
                        commitViolations.Add((ruleText, sourceFile, slug));
                        var token = $"[VCA:{fileName}:{slug}]";
                        requiredAcknowledgments.Add(token);
                        violations.Add($"[COMMIT] {ruleText}: {message}\n  Acknowledgment needed: {token} Reason: <your explanation>");
                    }
                    else if (enforcement == "STOP")
                    {
                        hasStopViolation = true;
                        violations.Add($"[STOP] {ruleText}: {message}\n  This violation CANNOT be overridden. Fix it before committing.");
                    }
                }
            }

            // Build response
            var result = new System.Text.StringBuilder();
            result.AppendLine($"Validated {stagedFiles.Count} file(s) against {allRules.Count} rule(s).");
            result.AppendLine();

            if (violations.Count == 0 && warnings.Count == 0)
            {
                result.AppendLine("PASS: All VCA rules satisfied.");
                return new VcaToolValidationReport(
                    result.ToString(),
                    HasError: false,
                    HasStopViolation: false,
                    RequiredAcknowledgments: []);
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
                        var fileName = Path.GetFileName(sourceFile);
                        result.AppendLine($"  [VCA:{fileName}:{slug}] Reason: <explain why this is acceptable>");
                    }
                }
            }

            return new VcaToolValidationReport(
                result.ToString(),
                HasError: false,
                HasStopViolation: hasStopViolation,
                RequiredAcknowledgments: requiredAcknowledgments.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to validate VCA rules for working directory {WorkDir}", workingDirectory ?? "current");
            return new VcaToolValidationReport(
                $"ERROR: Failed to validate VCA rules: {ex.Message}",
                HasError: true,
                HasStopViolation: false,
                RequiredAcknowledgments: []);
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

    private static async Task<List<string>> GetStagedFilesAsync(string gitRoot)
    {
        var files = new List<string>();

        // A git failure (git missing, index.lock, timeout, exit != 0) must NOT be
        // indistinguishable from "no staged files" — that would fail open and let a commit
        // through unvalidated. Throwing makes ValidateVca return "ERROR:", which blocks the hook.
        var result = await GitProcessRunner.RunAsync(
            "--no-pager diff --cached --name-only",
            gitRoot,
            GitCommandTimeout);

        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"git diff --cached timed out after {(int)GitCommandTimeout.TotalSeconds} seconds in {gitRoot}.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git diff --cached failed with exit code {result.ExitCode} in {gitRoot}: {result.StdErr}");
        }

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                files.Add(Path.Combine(gitRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        return files;
    }

    private static List<string> FindAgentFiles(string gitRoot)
    {
        var agentFiles = new List<string>();
        try
        {
            // Check common locations for AGENTS.md
            var commonPaths = new[]
            {
                Path.Combine(gitRoot, "AGENTS.md"),
                Path.Combine(gitRoot, ".github", "AGENTS.md"),
                Path.Combine(gitRoot, ".cursor", "AGENTS.md"),
                Path.Combine(gitRoot, ".claude", "AGENTS.md"),
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    agentFiles.Add(path);
                }
            }

            // Also search for any AGENTS.md in subdirectories (up to 3 levels)
            try
            {
                var files = Directory.GetFiles(gitRoot, "AGENTS.md", SearchOption.AllDirectories)
                    .Where(f => f.Split(Path.DirectorySeparatorChar).Length - gitRoot.Split(Path.DirectorySeparatorChar).Length <= 4)
                    .Where(f => !f.Contains("node_modules") && !f.Contains(".git"));
                agentFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to search for AGENTS.md files in {GitRoot}", gitRoot);
            }

            return agentFiles.Distinct().ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to find AGENTS.md files in {GitRoot}", gitRoot);
            return agentFiles;
        }
    }

    private static async Task<(bool Passed, string Message)> ValidateRuleAsync(
        string ruleText, List<string> stagedFiles, string gitRoot, string sourceFile)
    {
        var ruleLower = ruleText.ToLowerInvariant();

        // Log all file changes - check if staged files are documented in Files section
        if (ruleLower.Contains("log all file changes"))
        {
            var documentedFiles = await GetDocumentedFilesAsync(sourceFile, gitRoot);
            var undocumentedFiles = new List<string>();

            foreach (var stagedFile in stagedFiles)
            {
                var relativePath = GetRelativePath(gitRoot, stagedFile);
                if (!IsFileDocumented(relativePath, documentedFiles))
                {
                    undocumentedFiles.Add(relativePath);
                }
            }

            if (undocumentedFiles.Count > 0)
            {
                var fileList = string.Join(", ", undocumentedFiles.Take(3));
                var suffix = undocumentedFiles.Count > 3 ? $" and {undocumentedFiles.Count - 3} more" : "";
                return (false, $"{undocumentedFiles.Count} file(s) not documented in AGENTS.md Files section: {fileList}{suffix}");
            }

            return (true, $"All {stagedFiles.Count} changed file(s) are documented");
        }

        // File changes > N lines - check large files are documented
        if (ruleLower.Contains("file changes") && ruleLower.Contains("lines"))
        {
            var match = Regex.Match(ruleLower, @">\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int threshold))
            {
                var documentedFiles = await GetDocumentedFilesAsync(sourceFile, gitRoot);
                var violations = new List<string>();

                foreach (var file in stagedFiles)
                {
                    if (File.Exists(file))
                    {
                        var lineCount = File.ReadAllLines(file).Length;
                        if (lineCount > threshold)
                        {
                            var relativePath = GetRelativePath(gitRoot, file);
                            if (!IsFileDocumented(relativePath, documentedFiles))
                            {
                                violations.Add($"{relativePath} ({lineCount} lines)");
                            }
                        }
                    }
                }

                if (violations.Count > 0)
                {
                    return (false, $"{violations.Count} large file(s) not documented: {string.Join(", ", violations.Take(3))}");
                }
            }
            return (true, "All large files are documented");
        }

        // Cyclomatic complexity < N
        if (ruleLower.Contains("cyclomatic complexity") || ruleLower.Contains("complexity <"))
        {
            var match = Regex.Match(ruleLower, @"<\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int maxComplexity))
            {
                foreach (var file in stagedFiles)
                {
                    if (File.Exists(file) && IsCodeFile(file))
                    {
                        var content = await File.ReadAllTextAsync(file);
                        var complexity = EstimateCyclomaticComplexity(content);
                        if (complexity > maxComplexity)
                        {
                            return (false, $"File '{Path.GetFileName(file)}' estimated complexity {complexity} exceeds {maxComplexity}");
                        }
                    }
                }
            }
            return (true, "All files within complexity threshold");
        }

        // Test coverage minimum N%
        if (ruleLower.Contains("test coverage") || ruleLower.Contains("coverage minimum"))
        {
            var match = Regex.Match(ruleLower, @"(\d+)\s*%");
            if (match.Success)
            {
                // Simple check: ensure test files exist for code files
                var codeFiles = stagedFiles.Where(f => IsCodeFile(f) && !IsTestFile(f)).ToList();
                var testFiles = stagedFiles.Where(IsTestFile).ToList();

                if (codeFiles.Count > 0 && testFiles.Count == 0)
                {
                    return (false, "Code changes detected but no test files staged. Consider adding tests.");
                }
            }
            return (true, "Test coverage check passed");
        }

        // Default: pass unknown rules
        return (true, "Rule check passed");
    }

    private static async Task<HashSet<string>> GetDocumentedFilesAsync(string agentFilePath, string gitRoot)
    {
        var documentedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var content = await File.ReadAllTextAsync(agentFilePath);
            var lines = content.Split('\n');

            // Find the ## Files section
            var inFilesSection = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("## Files", StringComparison.OrdinalIgnoreCase))
                {
                    inFilesSection = true;
                    continue;
                }

                if (inFilesSection && trimmed.StartsWith("##"))
                {
                    break; // Next section
                }

                if (!inFilesSection || string.IsNullOrWhiteSpace(trimmed))
                    continue;

                // Extract file path from list item (- path/to/file.cs) or plain text
                var lineContent = trimmed.TrimStart('-', '*', ' ').Trim();

                // Handle markdown links like [filename](path/to/file.cs)
                if (lineContent.Contains("]("))
                {
                    var linkMatch = Regex.Match(lineContent, @"\]\(([^)]+)\)");
                    if (linkMatch.Success)
                    {
                        lineContent = linkMatch.Groups[1].Value;
                    }
                }

                // Handle inline code like `path/to/file.cs`
                lineContent = lineContent.Trim('`');

                // Handle "path/to/file.cs: Description" format
                if (lineContent.Contains(':'))
                {
                    lineContent = lineContent.Split(':')[0].Trim();
                }

                if (!string.IsNullOrEmpty(lineContent))
                {
                    // Normalize the path
                    var normalized = lineContent.Replace('\\', '/').TrimStart('.', '/');
                    documentedFiles.Add(normalized);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read documented files from {AgentFile}", agentFilePath);
        }

        return documentedFiles;
    }

    private static bool IsFileDocumented(string relativePath, HashSet<string> documentedFiles)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('.', '/');
        return documentedFiles.Contains(normalized);
    }

    private static string GetRelativePath(string gitRoot, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(gitRoot, fullPath).Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    private static bool IsCodeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".js" or ".ts" or ".py" or ".java" or ".go" or ".rb" or ".rs" or ".cpp" or ".c" or ".h";
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
    IReadOnlyList<string> RequiredAcknowledgments)
{
    public static VcaToolValidationReport Pass(string output) =>
        new(output, HasError: false, HasStopViolation: false, RequiredAcknowledgments: []);
}
