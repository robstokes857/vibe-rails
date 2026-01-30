namespace VibeRails.Services
{
    public interface IRuleValidationService
    {
        Task<ValidationResultSet> ValidateAsync(
            List<string> files,
            List<RuleWithEnforcement> rules,
            string rootPath,
            CancellationToken cancellationToken);

        Task<ValidationResultSet> ValidateWithSourceAsync(
            List<string> files,
            List<RuleWithSource> rulesWithSource,
            string rootPath,
            CancellationToken cancellationToken);
    }

    public record RuleWithSource(RuleWithEnforcement Rule, string SourceFile);

    public record ValidationResult(
        string RuleName,
        Enforcement Enforcement,
        bool Passed,
        string? Message = null,
        List<string>? AffectedFiles = null);

    public record ValidationResultSet(List<ValidationResult> Results);

    public class RuleValidationService : IRuleValidationService
    {
        private readonly IRulesService _rulesService;
        private readonly IAgentFileService _agentFileService;

        public RuleValidationService(IRulesService rulesService, IAgentFileService agentFileService)
        {
            _rulesService = rulesService;
            _agentFileService = agentFileService;
        }

        public async Task<ValidationResultSet> ValidateAsync(
            List<string> files,
            List<RuleWithEnforcement> rules,
            string rootPath,
            CancellationToken cancellationToken)
        {
            var results = new List<ValidationResult>();

            foreach (var rule in rules)
            {
                if (!_rulesService.TryParse(rule.RuleText, out Rule parsedRule))
                {
                    results.Add(new ValidationResult(
                        rule.RuleText, rule.Enforcement, true,
                        "Unknown rule - skipped"));
                    continue;
                }

                var result = parsedRule switch
                {
                    Rule.LogAllFileChanges => ValidateLogAllFileChanges(files, rule),
                    Rule.LogFileChangesOver5Lines => await ValidateLogFileChangesOverLines(files, rootPath, 5, rule, cancellationToken),
                    Rule.LogFileChangesOver10Lines => await ValidateLogFileChangesOverLines(files, rootPath, 10, rule, cancellationToken),
                    Rule.CyclomaticComplexityUnder20 => await ValidateCyclomaticComplexity(files, rootPath, 20, rule, cancellationToken),
                    Rule.CyclomaticComplexityUnder35 => await ValidateCyclomaticComplexity(files, rootPath, 35, rule, cancellationToken),
                    Rule.CyclomaticComplexityUnder60 => await ValidateCyclomaticComplexity(files, rootPath, 60, rule, cancellationToken),
                    Rule.CyclomaticComplexityDisabled => new ValidationResult(rule.RuleText, rule.Enforcement, true, "Disabled"),
                    Rule.RequireTestCoverageMinimum50 => ValidateTestCoverage(files, 50, rule),
                    Rule.RequireTestCoverageMinimum70 => ValidateTestCoverage(files, 70, rule),
                    Rule.RequireTestCoverageMinimum80 => ValidateTestCoverage(files, 80, rule),
                    Rule.RequireTestCoverageMinimum100 => ValidateTestCoverage(files, 100, rule),
                    Rule.SkipTestCoverage => new ValidationResult(rule.RuleText, rule.Enforcement, true, "Coverage check skipped"),
                    Rule.PackageChangeDetected => ValidatePackageChanges(files, rule),
                    _ => new ValidationResult(rule.RuleText, rule.Enforcement, true, "Rule not implemented for pre-commit")
                };

                results.Add(result);
            }

            return new ValidationResultSet(results);
        }

        public async Task<ValidationResultSet> ValidateWithSourceAsync(
            List<string> files,
            List<RuleWithSource> rulesWithSource,
            string rootPath,
            CancellationToken cancellationToken)
        {
            var results = new List<ValidationResult>();

            foreach (var ruleWithSource in rulesWithSource)
            {
                var rule = ruleWithSource.Rule;
                var sourceFile = ruleWithSource.SourceFile;

                if (!_rulesService.TryParse(rule.RuleText, out Rule parsedRule))
                {
                    results.Add(new ValidationResult(
                        rule.RuleText, rule.Enforcement, true,
                        "Unknown rule - skipped"));
                    continue;
                }

                // Scope files to the agent's directory — an AGENTS.md only governs
                // files in its own directory and subdirectories.
                var scopedFiles = GetScopedFiles(files, sourceFile, rootPath);

                var result = parsedRule switch
                {
                    Rule.LogAllFileChanges => await ValidateLogAllFileChangesWithSource(scopedFiles, rule, sourceFile, rootPath, cancellationToken),
                    Rule.LogFileChangesOver5Lines => await ValidateLogFileChangesOverLinesWithSource(scopedFiles, rootPath, 5, rule, sourceFile, cancellationToken),
                    Rule.LogFileChangesOver10Lines => await ValidateLogFileChangesOverLinesWithSource(scopedFiles, rootPath, 10, rule, sourceFile, cancellationToken),
                    Rule.CyclomaticComplexityUnder20 => await ValidateCyclomaticComplexity(scopedFiles, rootPath, 20, rule, cancellationToken),
                    Rule.CyclomaticComplexityUnder35 => await ValidateCyclomaticComplexity(scopedFiles, rootPath, 35, rule, cancellationToken),
                    Rule.CyclomaticComplexityUnder60 => await ValidateCyclomaticComplexity(scopedFiles, rootPath, 60, rule, cancellationToken),
                    Rule.CyclomaticComplexityDisabled => new ValidationResult(rule.RuleText, rule.Enforcement, true, "Disabled"),
                    Rule.RequireTestCoverageMinimum50 => ValidateTestCoverage(scopedFiles, 50, rule),
                    Rule.RequireTestCoverageMinimum70 => ValidateTestCoverage(scopedFiles, 70, rule),
                    Rule.RequireTestCoverageMinimum80 => ValidateTestCoverage(scopedFiles, 80, rule),
                    Rule.RequireTestCoverageMinimum100 => ValidateTestCoverage(scopedFiles, 100, rule),
                    Rule.SkipTestCoverage => new ValidationResult(rule.RuleText, rule.Enforcement, true, "Coverage check skipped"),
                    Rule.PackageChangeDetected => ValidatePackageChanges(scopedFiles, rule),
                    _ => new ValidationResult(rule.RuleText, rule.Enforcement, true, "Rule not implemented for pre-commit")
                };

                results.Add(result);
            }

            return new ValidationResultSet(results);
        }

        private async Task<ValidationResult> ValidateLogAllFileChangesWithSource(
            List<string> files, RuleWithEnforcement rule, string sourceFile, string rootPath,
            CancellationToken cancellationToken)
        {
            // Get documented files from the AGENTS.md Files section
            var documentedFiles = await _agentFileService.GetDocumentedFilesAsync(sourceFile, cancellationToken);

            // Normalize paths for comparison
            var normalizedDocumented = documentedFiles
                .Select(f => NormalizePath(f, rootPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Find files that were changed but not documented
            var undocumentedFiles = files
                .Where(f => !normalizedDocumented.Contains(NormalizePath(f, rootPath)))
                .ToList();

            if (undocumentedFiles.Count > 0)
            {
                return new ValidationResult(
                    rule.RuleText,
                    rule.Enforcement,
                    false,
                    $"{undocumentedFiles.Count} changed file(s) not documented in AGENTS.md Files section",
                    undocumentedFiles);
            }

            return new ValidationResult(
                rule.RuleText,
                rule.Enforcement,
                true,
                $"All {files.Count} changed file(s) are documented",
                null);
        }

        private async Task<ValidationResult> ValidateLogFileChangesOverLinesWithSource(
            List<string> files, string rootPath, int threshold,
            RuleWithEnforcement rule, string sourceFile, CancellationToken cancellationToken)
        {
            // Get documented files from the AGENTS.md Files section
            var documentedFiles = await _agentFileService.GetDocumentedFilesAsync(sourceFile, cancellationToken);

            var normalizedDocumented = documentedFiles
                .Select(f => NormalizePath(f, rootPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var violations = new List<string>();

            foreach (var file in files)
            {
                var fullPath = Path.Combine(rootPath, file);
                if (!File.Exists(fullPath)) continue;

                var lineCount = await GetFileLineCount(fullPath, cancellationToken);
                if (lineCount > threshold)
                {
                    // Check if this large file is documented
                    var normalizedFile = NormalizePath(file, rootPath);
                    if (!normalizedDocumented.Contains(normalizedFile))
                    {
                        violations.Add($"{file} ({lineCount} lines, not documented)");
                    }
                }
            }

            if (violations.Count > 0)
            {
                return new ValidationResult(
                    rule.RuleText,
                    rule.Enforcement,
                    false,
                    $"{violations.Count} large file(s) (>{threshold} lines) not documented in AGENTS.md",
                    violations);
            }

            return new ValidationResult(rule.RuleText, rule.Enforcement, true,
                $"All large files (>{threshold} lines) are documented");
        }

        /// <summary>
        /// Returns only the changed files that fall within the agent file's directory (and subdirectories).
        /// An AGENTS.md at "VibeRails/DB/AGENTS.md" only governs files under "VibeRails/DB/".
        /// An AGENTS.md at the repo root governs all files.
        /// </summary>
        private static List<string> GetScopedFiles(List<string> files, string sourceFile, string rootPath)
        {
            var agentDir = Path.GetDirectoryName(Path.GetFullPath(sourceFile)) ?? "";
            var rootFull = Path.GetFullPath(rootPath);

            // Get relative path of the agent's directory from the repo root
            var relativeAgentDir = Path.GetRelativePath(rootFull, agentDir)
                .Replace('\\', '/');

            // If agent is at repo root, all files are in scope
            if (relativeAgentDir == ".")
                return files;

            // Ensure prefix ends with / for proper prefix matching
            var prefix = relativeAgentDir + "/";

            return files
                .Where(f => NormalizePath(f, rootPath)
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static string NormalizePath(string path, string rootPath)
        {
            // Remove leading ./ or .\
            path = path.TrimStart('.').TrimStart('/', '\\');

            // Normalize separators
            path = path.Replace('\\', '/');

            return path;
        }

        private ValidationResult ValidateLogAllFileChanges(
            List<string> files, RuleWithEnforcement rule)
        {
            // Legacy method without source - just reports file count
            return new ValidationResult(
                rule.RuleText,
                rule.Enforcement,
                true,
                $"{files.Count} file(s) changed (use ValidateWithSourceAsync for full validation)",
                files);
        }

        private async Task<ValidationResult> ValidateLogFileChangesOverLines(
            List<string> files, string rootPath, int threshold,
            RuleWithEnforcement rule, CancellationToken cancellationToken)
        {
            var largeChanges = new List<string>();

            foreach (var file in files)
            {
                var fullPath = Path.Combine(rootPath, file);
                if (!File.Exists(fullPath)) continue;

                var lineCount = await GetFileLineCount(fullPath, cancellationToken);
                if (lineCount > threshold)
                {
                    largeChanges.Add($"{file} ({lineCount} lines)");
                }
            }

            if (largeChanges.Count > 0)
            {
                return new ValidationResult(
                    rule.RuleText,
                    rule.Enforcement,
                    false,
                    $"{largeChanges.Count} file(s) exceed {threshold} line threshold",
                    largeChanges);
            }

            return new ValidationResult(rule.RuleText, rule.Enforcement, true);
        }

        private async Task<int> GetFileLineCount(
            string filePath, CancellationToken cancellationToken)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
                return lines.Length;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<ValidationResult> ValidateCyclomaticComplexity(
            List<string> files, string rootPath, int threshold,
            RuleWithEnforcement rule, CancellationToken cancellationToken)
        {
            var violations = new List<string>();

            foreach (var file in files)
            {
                var fullPath = Path.Combine(rootPath, file);
                if (!File.Exists(fullPath)) continue;

                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".cs" && ext != ".js" && ext != ".ts") continue;

                var complexity = await EstimateComplexity(fullPath, cancellationToken);
                if (complexity > threshold)
                {
                    violations.Add($"{file} (estimated complexity: {complexity})");
                }
            }

            if (violations.Count > 0)
            {
                return new ValidationResult(
                    rule.RuleText,
                    rule.Enforcement,
                    false,
                    $"{violations.Count} file(s) exceed complexity threshold of {threshold}",
                    violations);
            }

            return new ValidationResult(rule.RuleText, rule.Enforcement, true);
        }

        private async Task<int> EstimateComplexity(
            string filePath, CancellationToken cancellationToken)
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);

            var decisionKeywords = new[] { " if ", " if(", " else ", " switch ", " case ", " for ", " for(", " foreach ", " foreach(", " while ", " while(", " do ", " catch ", "&&", "||", "?" };

            int complexity = 1;
            foreach (var keyword in decisionKeywords)
            {
                complexity += CountOccurrences(content, keyword);
            }

            return complexity;
        }

        private int CountOccurrences(string content, string keyword)
        {
            int count = 0;
            int index = 0;
            while ((index = content.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                count++;
                index += keyword.Length;
            }
            return count;
        }

        private ValidationResult ValidateTestCoverage(
            List<string> files, int minimumPercent, RuleWithEnforcement rule)
        {
            var codeFiles = files.Where(f => IsCodeFile(f) && !IsTestFile(f)).ToList();
            var testFiles = files.Where(f => IsTestFile(f)).ToList();

            var missingTests = new List<string>();

            foreach (var codeFile in codeFiles)
            {
                var baseName = Path.GetFileNameWithoutExtension(codeFile);
                var hasTest = files.Any(f =>
                    f.Contains($"{baseName}Tests", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains($"{baseName}Test", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains($"{baseName}.test", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains($"{baseName}.spec", StringComparison.OrdinalIgnoreCase));

                if (!hasTest)
                {
                    missingTests.Add(codeFile);
                }
            }

            return new ValidationResult(
                rule.RuleText,
                rule.Enforcement,
                true,
                $"Test coverage check: {codeFiles.Count} code files, {testFiles.Count} test files in commit",
                missingTests.Count > 0 ? missingTests : null);
        }

        private bool IsCodeFile(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            return ext is ".cs" or ".js" or ".ts" or ".py" or ".java";
        }

        private bool IsTestFile(string file)
        {
            var name = Path.GetFileName(file).ToLowerInvariant();
            return name.Contains("test") || name.Contains("spec") ||
                   file.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
                   file.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
                   file.Contains("\\test\\", StringComparison.OrdinalIgnoreCase) ||
                   file.Contains("\\tests\\", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> PackageFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Node.js / JavaScript
            "package.json",
            "package-lock.json",
            "yarn.lock",
            "pnpm-lock.yaml",
            // Python
            "requirements.txt",
            "Pipfile",
            "Pipfile.lock",
            "pyproject.toml",
            "poetry.lock",
            "setup.py",
            // .NET
            "packages.config",
            "Directory.Packages.props",
            // Java / Kotlin
            "pom.xml",
            "build.gradle",
            "build.gradle.kts",
            "settings.gradle",
            "settings.gradle.kts",
            // Ruby
            "Gemfile",
            "Gemfile.lock",
            // Rust
            "Cargo.toml",
            "Cargo.lock",
            // Go
            "go.mod",
            "go.sum",
            // PHP
            "composer.json",
            "composer.lock"
        };

        private static readonly HashSet<string> PackageFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csproj",
            ".fsproj",
            ".vbproj"
        };

        private ValidationResult ValidatePackageChanges(List<string> files, RuleWithEnforcement rule)
        {
            var packageFiles = files.Where(IsPackageFile).ToList();

            if (packageFiles.Count == 0)
            {
                return new ValidationResult(
                    rule.RuleText,
                    rule.Enforcement,
                    true,
                    "No package files changed");
            }

            return new ValidationResult(
                rule.RuleText,
                rule.Enforcement,
                false,
                $"{packageFiles.Count} package file(s) changed",
                packageFiles);
        }

        private bool IsPackageFile(string file)
        {
            var fileName = Path.GetFileName(file);
            if (PackageFileNames.Contains(fileName))
                return true;

            var ext = Path.GetExtension(file);
            if (PackageFileExtensions.Contains(ext))
                return true;

            return false;
        }
    }
}
