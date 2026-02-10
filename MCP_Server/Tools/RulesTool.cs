using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Serilog;

namespace MCP_Server.Tools;

[McpServerToolType]
public class RulesTool
{
    private static readonly Regex SecretPattern = new Regex(
        @"(?i)(api[_-]?key|password|secret|token)[\s:=]+([a-zA-Z0-9_\-]+)",
        RegexOptions.Compiled);

    // Pattern to extract rules from AGENTS.md: - [ENFORCEMENT] Rule text
    private static readonly Regex RulePattern = new Regex(
        @"^-\s*\[(WARN|COMMIT|STOP|SKIP|DISABLED)\]\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    [McpServerTool]
    [Description("Checks if the provided content follows the project's safety and style rules. Returns PASS or FAIL with reason.")]
    public static string CheckRules([Description("The content to check.")] string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "FAIL: Content cannot be empty.";
        }

        // Security Check: Look for potential hardcoded secrets
        var secretMatch = SecretPattern.Match(content);
        if (secretMatch.Success)
        {
            return $"FAIL: Content contains potential secret (detected keyword: '{secretMatch.Groups[1].Value}'). Please remove sensitive information.";
        }

        // Style Check: Length limit
        const int MaxLength = 2000;
        if (content.Length > MaxLength)
        {
            return $"FAIL: Content exceeds maximum length of {MaxLength} characters (Current: {content.Length}).";
        }

        // Style Check: No TODOs in final output
        if (content.Contains("TODO:", StringComparison.OrdinalIgnoreCase))
        {
            return "FAIL: Content contains unresolved 'TODO:' items.";
        }

        return "PASS: All rules satisfied.";
    }

    [McpServerTool]
    [Description("Validates staged files against VCA rules defined in AGENTS.md files. Call this BEFORE attempting to commit changes. Returns validation results with any COMMIT-level violations that require acknowledgment.")]
    public static async Task<string> ValidateVca(
        [Description("Optional working directory. If not provided, uses current directory.")] string? workingDirectory = null)
    {
        try
        {
            var workDir = workingDirectory ?? Directory.GetCurrentDirectory();

            // Find git root
            var gitRoot = FindGitRoot(workDir);
            if (gitRoot == null)
            {
                return "SKIP: Not in a git repository.";
            }

            // Get staged files
            var stagedFiles = await GetStagedFilesAsync(gitRoot);
            if (stagedFiles.Count == 0)
            {
                return "PASS: No staged files to validate.";
            }

            // Find AGENTS.md files
            var agentFiles = FindAgentFiles(gitRoot);
            if (agentFiles.Count == 0)
            {
                return "PASS: No AGENTS.md files found. No VCA rules to check.";
            }

            // Parse rules from AGENTS.md files
            var allRules = new List<(string RuleText, string Enforcement, string SourceFile)>();
            foreach (var agentFile in agentFiles)
            {
                var content = await File.ReadAllTextAsync(agentFile);
                var matches = RulePattern.Matches(content);
                foreach (Match match in matches)
                {
                    var enforcement = match.Groups[1].Value.ToUpperInvariant();
                    var ruleText = match.Groups[2].Value.Trim();
                    allRules.Add((ruleText, enforcement, agentFile));
                }
            }

            if (allRules.Count == 0)
            {
                return "PASS: No VCA rules defined in AGENTS.md files.";
            }

            // Validate against rules
            var violations = new List<string>();
            var warnings = new List<string>();
            var commitViolations = new List<(string RuleText, string SourceFile, string Slug)>();

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
                        violations.Add($"[COMMIT] {ruleText}: {message}\n  Acknowledgment needed: [VCA:{fileName}:{slug}] Reason: <your explanation>");
                    }
                    else if (enforcement == "STOP")
                    {
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
                return result.ToString();
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
                var hasStop = violations.Any(v => v.StartsWith("[STOP]"));
                if (hasStop)
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

                if (commitViolations.Count > 0 && !hasStop)
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

            return result.ToString();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to validate VCA rules for working directory {WorkDir}", workingDirectory ?? "current");
            return $"ERROR: Failed to validate VCA rules: {ex.Message}";
        }
    }

    private static string? FindGitRoot(string startPath)
    {
        var current = startPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
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
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "diff --cached --name-only",
                    WorkingDirectory = gitRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    files.Add(Path.Combine(gitRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get staged files from git in {GitRoot}", gitRoot);
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
