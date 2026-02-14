using System.Text.RegularExpressions;

namespace VibeRails.Services
{
    public record RuleInfo(string Name, string Description);

    public interface IRulesService
    {
        List<string> AllowedRules();
        List<RuleInfo> AllowedRulesWithDescriptions();
        string ToDisplayString(Rule value);
        string GetDescription(Rule value);
        bool TryParse(string value, out Rule rule);
    }

    // Enforcement levels for rules
    public enum Enforcement
    {
        WARN,    // Warn the user
        COMMIT,  // Require explanation in commit/PR message
        STOP     // Block the commit/PR
    }

    // Model for a rule with its enforcement level
    public record RuleWithEnforcement(string RuleText, Enforcement Enforcement);

    public static class EnforcementParser
    {
        private static readonly Regex EnforcementPattern = new Regex(@"\s*\((WARN|COMMIT|STOP)\)\s*$", RegexOptions.IgnoreCase);

        public static Enforcement Parse(string value)
        {
            return value?.ToUpperInvariant() switch
            {
                "WARN" => Enforcement.WARN,
                "COMMIT" => Enforcement.COMMIT,
                "STOP" => Enforcement.STOP,
                _ => Enforcement.WARN // Default to WARN
            };
        }

        public static string ToDisplayString(Enforcement value)
        {
            return value.ToString();
        }

        /// <summary>
        /// Extracts the rule text and enforcement level from a line like "Rule text (STOP)"
        /// </summary>
        public static (string ruleText, Enforcement enforcement) ParseRuleWithEnforcement(string line)
        {
            var match = EnforcementPattern.Match(line);
            if (match.Success)
            {
                var ruleText = line.Substring(0, match.Index).Trim();
                var enforcement = Parse(match.Groups[1].Value);
                return (ruleText, enforcement);
            }
            // No enforcement specified, default to WARN
            return (line.Trim(), Enforcement.WARN);
        }

        /// <summary>
        /// Formats a rule with its enforcement level for storage
        /// </summary>
        public static string FormatRuleWithEnforcement(string ruleText, Enforcement enforcement)
        {
            return $"{ruleText} ({enforcement})";
        }
    }

    public enum Rule
    {
        LogAllFileChanges,
        LogFileChangesOver5Lines,
        LogFileChangesOver10Lines,
        CyclomaticComplexityUnder20,
        CyclomaticComplexityUnder35,
        CyclomaticComplexityUnder60,
        CyclomaticComplexityDisabled,
        RequireTestCoverageMinimum50,
        RequireTestCoverageMinimum70,
        RequireTestCoverageMinimum80,
        RequireTestCoverageMinimum100,
        SkipTestCoverage,
        PackageChangeDetected,
        CheckCommitMessageForWords
    }
    public static class RuleParser
    {
        private static Dictionary<Rule, string> _keyValuePairs = new Dictionary<Rule, string>()
        {
            { Rule.LogAllFileChanges, "Log all file changes" },
            { Rule.LogFileChangesOver5Lines, "Log file changes > 5 lines" },
            { Rule.LogFileChangesOver10Lines, "Log file changes > 10 lines" },
            { Rule.CyclomaticComplexityUnder20, "Cyclomatic complexity < 20" },
            { Rule.CyclomaticComplexityUnder35, "Cyclomatic complexity < 35" },
            { Rule.CyclomaticComplexityUnder60, "Cyclomatic complexity < 60" },
            { Rule.CyclomaticComplexityDisabled, "Cyclomatic complexity disabled" },
            { Rule.RequireTestCoverageMinimum50, "Require test coverage minimum 50%" },
            { Rule.RequireTestCoverageMinimum70, "Require test coverage minimum 70%" },
            { Rule.RequireTestCoverageMinimum80, "Require test coverage minimum 80%" },
            { Rule.RequireTestCoverageMinimum100, "Require test coverage minimum 100%" },
            { Rule.SkipTestCoverage, "Skip test coverage" },
            { Rule.PackageChangeDetected, "Package file changes" },
            { Rule.CheckCommitMessageForWords, "Check commit message for" }
        };

        private static Dictionary<Rule, string> _descriptions = new Dictionary<Rule, string>()
        {
            { Rule.LogAllFileChanges, "Requires all changed files to be documented in the AGENTS.md 'Files' section. The LLM must add entries for each file it modifies so changes are tracked." },
            { Rule.LogFileChangesOver5Lines, "Requires files with more than 5 lines changed to be documented in the AGENTS.md 'Files' section. Smaller changes are allowed without documentation." },
            { Rule.LogFileChangesOver10Lines, "Requires files with more than 10 lines changed to be documented in the AGENTS.md 'Files' section. Smaller changes are allowed without documentation." },
            { Rule.CyclomaticComplexityUnder20, "Enforces that changed files have cyclomatic complexity under 20. High complexity code is harder to test and maintain." },
            { Rule.CyclomaticComplexityUnder35, "Enforces that changed files have cyclomatic complexity under 35. Allows moderately complex code while preventing excessive complexity." },
            { Rule.CyclomaticComplexityUnder60, "Enforces that changed files have cyclomatic complexity under 60. A lenient threshold for legacy codebases." },
            { Rule.CyclomaticComplexityDisabled, "Disables cyclomatic complexity checking for this project or directory." },
            { Rule.RequireTestCoverageMinimum50, "Requires at least 50% test coverage for changed code files. A basic level of testing is expected." },
            { Rule.RequireTestCoverageMinimum70, "Requires at least 70% test coverage for changed code files. A moderate level of testing is expected." },
            { Rule.RequireTestCoverageMinimum80, "Requires at least 80% test coverage for changed code files. High test coverage is expected for critical code." },
            { Rule.RequireTestCoverageMinimum100, "Requires 100% test coverage for changed code files. Full coverage is mandatory." },
            { Rule.SkipTestCoverage, "Disables test coverage checking for this project or directory." },
            { Rule.PackageChangeDetected, "Detects changes to package/dependency files (package.json, .csproj, requirements.txt, etc.) and alerts or blocks based on enforcement level." },
            { Rule.CheckCommitMessageForWords, "Checks commit messages for specific forbidden words (CSV list). Useful for catching recurring LLM mistakes. Format: 'Check commit message for: word1,word2,word3'" }
        };

        public static List<string> GetRules()
        {
            return _keyValuePairs.Values.ToList();
        }

        public static List<RuleInfo> GetRulesWithDescriptions()
        {
            return _keyValuePairs.Select(kvp =>
                new RuleInfo(kvp.Value, _descriptions.GetValueOrDefault(kvp.Key, "No description available."))
            ).ToList();
        }

        public static string ToDisplayString(Rule value)
        {
            return _keyValuePairs[value];
        }

        public static string GetDescription(Rule value)
        {
            return _descriptions.GetValueOrDefault(value, "No description available.");
        }

        public static bool TryParse(string value, out Rule rule)
        {
            foreach (var kvp in _keyValuePairs)
            {
                if (kvp.Value.Contains(value))
                {
                    rule = kvp.Key;
                    return true;
                }
            }
            rule = default;
            return false;
        }
    }


    public class RulesService : IRulesService
    {
        public List<string> AllowedRules()
        {
            return RuleParser.GetRules();
        }

        public List<RuleInfo> AllowedRulesWithDescriptions()
        {
            return RuleParser.GetRulesWithDescriptions();
        }

        public string ToDisplayString(Rule value)
        {
            return RuleParser.ToDisplayString(value);
        }

        public string GetDescription(Rule value)
        {
            return RuleParser.GetDescription(value);
        }

        public bool TryParse(string value, out Rule rule)
        {
            return RuleParser.TryParse(value, out rule);
        }
    }
}
