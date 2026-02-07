using System.Text.RegularExpressions;
using VibeRails.Services;
using VibeRails.Services.VCA;

namespace VibeRails.Utils
{
    public static class VcaValidationRunner
    {
        // Pattern: [VCA:filename:rule-slug] Reason: explanation
        private static readonly Regex AcknowledgmentPattern = new Regex(
            @"\[VCA:([^\]]+):([^\]]+)\]\s*Reason:\s*(.+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static async Task<int> RunAsync(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var scopedServices = scope.ServiceProvider;

            var gitService = scopedServices.GetRequiredService<IGitService>();
            var validationService = scopedServices.GetRequiredService<VibeRails.Services.VCA.ValidationService>();

            var args = Configs.GetAarguments();
            var isPreCommit = args.PreCommit;

            try
            {
                var rootPath = await gitService.GetRootPathAsync();
                if (string.IsNullOrEmpty(rootPath))
                {
                    Console.Error.WriteLine("Error: Not in a git repository");
                    return 2;
                }

                // Run new modular validation service
                var results = await validationService.ValidateAsync(rootPath, isPreCommit, CancellationToken.None);

                if (results.TotalFiles == 0)
                {
                    Console.WriteLine("No files to validate");
                    return 0;
                }

                if (results.TotalRules == 0)
                {
                    Console.WriteLine("No VCA rules defined");
                    return 0;
                }

                // Convert new result format to old format for OutputResults
                var resultsWithSource = results.Results
                    .Select(r => (
                        Result: new ValidationResult(
                            r.RuleName,
                            r.Enforcement,
                            r.Passed,
                            r.Message,
                            r.FilePath != null ? new List<string> { r.FilePath } : null),
                        SourceFile: r.SourceFile))
                    .ToList();

                return OutputResults(resultsWithSource, results.TotalFiles, rootPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VCA Validation Error: {ex.Message}");
                return 2;
            }
        }

        /// <summary>
        /// Validates commit message for required acknowledgments (commit-msg hook)
        /// </summary>
        public static async Task<int> RunCommitMsgValidationAsync(IServiceProvider services, string commitMsgFile)
        {
            using var scope = services.CreateScope();
            var scopedServices = scope.ServiceProvider;

            var gitService = scopedServices.GetRequiredService<IGitService>();
            var agentFileService = scopedServices.GetRequiredService<IAgentFileService>();
            var validationService = scopedServices.GetRequiredService<IRuleValidationService>();

            try
            {
                var rootPath = await gitService.GetRootPathAsync();
                if (string.IsNullOrEmpty(rootPath))
                {
                    return 0; // Not in git, allow
                }

                // Read commit message
                if (!File.Exists(commitMsgFile))
                {
                    Console.Error.WriteLine($"Commit message file not found: {commitMsgFile}");
                    return 1;
                }

                var commitMessage = await File.ReadAllTextAsync(commitMsgFile);

                // Get staged files and rules
                var changedFiles = await gitService.GetStagedFilesAsync(CancellationToken.None);
                if (changedFiles.Count == 0)
                {
                    return 0;
                }

                var agentFiles = await agentFileService.GetAgentFiles(CancellationToken.None);
                var allRulesWithSource = new List<(RuleWithEnforcement Rule, string SourceFile)>();

                foreach (var agentFile in agentFiles)
                {
                    var rules = await agentFileService.GetRulesWithEnforcementAsync(
                        agentFile, CancellationToken.None);
                    foreach (var rule in rules)
                    {
                        allRulesWithSource.Add((rule, agentFile));
                    }
                }

                if (allRulesWithSource.Count == 0)
                {
                    return 0;
                }

                // Convert to RuleWithSource list for proper validation (using OLD validation service)
                var rulesWithSource = allRulesWithSource
                    .Select(r => new VibeRails.Services.RuleWithSource(r.Rule, r.SourceFile))
                    .ToList();

                var results = await validationService.ValidateWithSourceAsync(
                    changedFiles, rulesWithSource, rootPath, CancellationToken.None);

                // Find COMMIT-level violations that need acknowledgment
                var commitViolations = new List<(ValidationResult Result, string SourceFile, string Slug)>();
                for (int i = 0; i < results.Results.Count && i < allRulesWithSource.Count; i++)
                {
                    var result = results.Results[i];
                    var sourceFile = allRulesWithSource[i].SourceFile;

                    if (!result.Passed && result.Enforcement == Enforcement.COMMIT)
                    {
                        var slug = GenerateRuleSlug(result.RuleName);
                        commitViolations.Add((result, sourceFile, slug));
                    }
                }

                if (commitViolations.Count == 0)
                {
                    return 0; // No COMMIT violations, allow
                }

                // Check if all COMMIT violations have acknowledgments
                var missingAcknowledgments = new List<(ValidationResult Result, string SourceFile, string Slug)>();

                foreach (var (result, sourceFile, slug) in commitViolations)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    if (!HasAcknowledgment(commitMessage, fileName, slug))
                    {
                        missingAcknowledgments.Add((result, sourceFile, slug));
                    }
                }

                if (missingAcknowledgments.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("VCA: All COMMIT violations acknowledged");
                    Console.ResetColor();
                    return 0;
                }

                // Output missing acknowledgments
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("VCA: Missing acknowledgments for COMMIT-level violations");
                Console.ResetColor();
                Console.WriteLine();

                foreach (var (result, sourceFile, slug) in missingAcknowledgments)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    Console.WriteLine($"  Rule: {result.RuleName}");
                    Console.WriteLine($"  Source: {sourceFile}");
                    Console.WriteLine();
                    Console.WriteLine($"  Add this to your commit message:");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    [VCA:{fileName}:{slug}] Reason: <your explanation>");
                    Console.ResetColor();
                    Console.WriteLine();
                }

                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VCA commit-msg validation error: {ex.Message}");
                return 0; // On error, allow commit
            }
        }

        public static async Task<int> RunHookManagementAsync(IServiceProvider services, bool install)
        {
            using var scope = services.CreateScope();
            var scopedServices = scope.ServiceProvider;

            var hookService = scopedServices.GetRequiredService<IHookInstallationService>();
            var gitService = scopedServices.GetRequiredService<IGitService>();

            try
            {
                var rootPath = await gitService.GetRootPathAsync();
                if (string.IsNullOrEmpty(rootPath))
                {
                    Console.Error.WriteLine("Error: Not in a git repository");
                    return 1;
                }

                if (install)
                {
                    var result = await hookService.InstallHooksAsync(rootPath, CancellationToken.None);
                    if (result.Success)
                    {
                        Console.WriteLine("VCA hooks installed successfully (pre-commit + commit-msg)");
                        return 0;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Failed to install hooks: {result.ErrorMessage}");
                        if (result.Details != null)
                        {
                            Console.Error.WriteLine($"Details: {result.Details}");
                        }
                        return 1;
                    }
                }
                else
                {
                    var result = await hookService.UninstallHooksAsync(rootPath, CancellationToken.None);
                    if (result.Success)
                    {
                        Console.WriteLine("VCA hooks uninstalled");
                        return 0;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Failed to uninstall hooks: {result.ErrorMessage}");
                        if (result.Details != null)
                        {
                            Console.Error.WriteLine($"Details: {result.Details}");
                        }
                        return 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Hook management error: {ex.Message}");
                return 1;
            }
        }

        private static int OutputResults(List<(ValidationResult Result, string SourceFile)> resultsWithSource, int fileCount, string rootPath)
        {
            var hasStopViolation = false;
            var commitViolations = new List<(ValidationResult Result, string SourceFile, string Slug)>();

            Console.WriteLine($"Validating {fileCount} file(s) against {resultsWithSource.Count} rule(s)...");
            Console.WriteLine();

            foreach (var (result, sourceFile) in resultsWithSource)
            {
                var symbol = result.Passed ? "[PASS]" : "[FAIL]";

                if (result.Passed)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }

                Console.Write(symbol);
                Console.ResetColor();
                Console.WriteLine($" {result.RuleName} ({result.Enforcement})");

                if (result.Message != null)
                {
                    Console.WriteLine($"       {result.Message}");
                }

                if (result.AffectedFiles?.Count > 0)
                {
                    foreach (var file in result.AffectedFiles)
                    {
                        Console.WriteLine($"       - {file}");
                    }
                }

                if (!result.Passed)
                {
                    if (result.Enforcement == Enforcement.STOP)
                    {
                        hasStopViolation = true;
                    }
                    else if (result.Enforcement == Enforcement.COMMIT)
                    {
                        var slug = GenerateRuleSlug(result.RuleName);
                        commitViolations.Add((result, sourceFile, slug));
                    }
                }
            }

            Console.WriteLine();

            // STOP violations block entirely
            if (hasStopViolation)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("VCA Validation FAILED - STOP-level violation detected");
                Console.WriteLine("This violation cannot be overridden. Fix the issue before committing.");
                Console.ResetColor();
                return 1;
            }

            // COMMIT violations require acknowledgment
            if (commitViolations.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("VCA: COMMIT-level violations detected");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("To proceed, include acknowledgments in your commit message:");
                Console.WriteLine();

                foreach (var (result, sourceFile, slug) in commitViolations)
                {
                    var fileName = Path.GetFileName(sourceFile);
                    var relativePath = GetRelativePath(rootPath, sourceFile);
                    Console.WriteLine($"  Rule: {result.RuleName}");
                    Console.WriteLine($"  Source: {relativePath}");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"    [VCA:{fileName}:{slug}] Reason: <explain why this is acceptable>");
                    Console.ResetColor();
                    Console.WriteLine();
                }

                Console.WriteLine("Example commit:");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  git commit -m \"Fix bug in utils");
                Console.WriteLine();
                Console.WriteLine($"  [VCA:{Path.GetFileName(commitViolations[0].SourceFile)}:{commitViolations[0].Slug}] Reason: Only changed test fixtures\"");
                Console.ResetColor();
                Console.WriteLine();

                // Allow the commit - the commit-msg hook will verify acknowledgments
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("VCA pre-commit PASSED (commit-msg hook will verify acknowledgments)");
                Console.ResetColor();
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("VCA Validation PASSED");
            Console.ResetColor();
            return 0;
        }

        private static string GenerateRuleSlug(string ruleName)
        {
            // Convert "Require test coverage minimum 80%" to "test-coverage-80"
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

            // Remove duplicate dashes
            while (slug.Contains("--"))
            {
                slug = slug.Replace("--", "-");
            }

            return slug;
        }

        private static bool HasAcknowledgment(string commitMessage, string fileName, string slug)
        {
            // Look for [VCA:filename:slug] Reason: ...
            var matches = AcknowledgmentPattern.Matches(commitMessage);
            foreach (Match match in matches)
            {
                var ackFile = match.Groups[1].Value.Trim();
                var ackSlug = match.Groups[2].Value.Trim();
                var reason = match.Groups[3].Value.Trim();

                if (ackFile.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                    ackSlug.Equals(slug, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(reason))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            try
            {
                return Path.GetRelativePath(rootPath, fullPath);
            }
            catch
            {
                return fullPath;
            }
        }
    }
}
