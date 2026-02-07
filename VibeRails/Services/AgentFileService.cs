using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services
{
    public interface IAgentFileService
    {
        Task<List<string>> GetAgentFiles(CancellationToken cancellationToken);
        Task<string> GetAgentFileContentAsync(string path, CancellationToken cancellationToken);
        Task CreateAgentFileAsync(string path, CancellationToken cancellationToken, params string[] rules);
        Task<List<string>> GetRulesAsync(string path, CancellationToken cancellationToken);
        Task<List<RuleWithEnforcement>> GetRulesWithEnforcementAsync(string path, CancellationToken cancellationToken);
        Task AddRulesAsync(string path, CancellationToken cancellationToken, params string[] rules);
        Task AddRuleWithEnforcementAsync(string path, string ruleText, Enforcement enforcement, CancellationToken cancellationToken);
        Task DeleteRulesAsync(string path, CancellationToken cancellationToken, params string[] rules);
        Task UpdateRuleEnforcementAsync(string path, string ruleText, Enforcement enforcement, CancellationToken cancellationToken);
        Task<List<string>> GetDocumentedFilesAsync(string path, CancellationToken cancellationToken);
    }

    public class AgentFileService : IAgentFileService
    {
        private readonly IGitService _gitService;
        private readonly IRulesService _rulesService;
        public AgentFileService(IGitService gitService, IRulesService rulesService)
        {
            this._gitService = gitService;
            this._rulesService = rulesService;
        }

        /// <summary>
        /// Ensures the rules section header exists in the file. If not, adds it at the end.
        /// Returns the index of the header in the lines list.
        /// </summary>
        private static int EnsureRulesSection(List<string> lines)
        {
            int index = lines.IndexOf(STRINGS.RULE_HEADER);
            if (index == -1)
            {
                lines.Add("");
                lines.Add(STRINGS.RULE_HEADER);
                index = lines.Count - 1;
            }
            return index;
        }

        public async Task<List<string>> GetAgentFiles(CancellationToken cancellationToken)
        {
            if (!Configs.IsLocalContext())
                return new();

            var root = await _gitService.GetRootPathAsync(cancellationToken);

            return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.Equals("agent.md", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("agents.md", StringComparison.OrdinalIgnoreCase);
                })
                .Select(Path.GetFullPath)
                .ToList();
        }

        public async Task CreateAgentFileAsync(string path, CancellationToken cancellationToken, params string[] rules)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(STRINGS.AGENT_FILE_HEADER);
            sb.AppendLine();
            sb.AppendLine(STRINGS.RULE_HEADER);
            foreach (var rule in rules)
            {
                sb.AppendLine($"- {rule}");
            }
            sb.AppendLine();
            sb.AppendLine(STRINGS.FILE_HEADER);
            await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken);
        }
        public async Task<string> GetAgentFileContentAsync(string path, CancellationToken cancellationToken)
        {
            // Validate this is actually an agent file
            var agentFiles = await GetAgentFiles(cancellationToken);
            var normalizedPath = Path.GetFullPath(path);

            if (!agentFiles.Any(f => Path.GetFullPath(f).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UnauthorizedAccessException($"Path is not a valid agent file: {path}");
            }

            return await File.ReadAllTextAsync(normalizedPath, cancellationToken);
        }

        public async Task<List<string>> GetRulesAsync(string path, CancellationToken cancellationToken)
        {
            var rulesWithEnforcement = await GetRulesWithEnforcementAsync(path, cancellationToken);
            return rulesWithEnforcement.Select(r => r.RuleText).ToList();
        }

        public async Task<List<RuleWithEnforcement>> GetRulesWithEnforcementAsync(string path, CancellationToken cancellationToken)
        {
            string[] lines = await File.ReadAllLinesAsync(path, cancellationToken);
            int index = lines.IndexOf(STRINGS.RULE_HEADER);
            if (index == -1)
                return new();

            List<RuleWithEnforcement> rules = new List<RuleWithEnforcement>();
            for (int i = index + 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("##"))
                    break;

                if (lines[i].StartsWith("#") || string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                string lineContent = lines[i].TrimStart('-', ' ').Trim();
                var (ruleText, enforcement) = EnforcementParser.ParseRuleWithEnforcement(lineContent);

                if (_rulesService.TryParse(ruleText, out Rule _))
                    rules.Add(new RuleWithEnforcement(ruleText, enforcement));
            }
            return rules;
        }

        public async Task AddRulesAsync(string path, CancellationToken cancellationToken, params string[] rules)
        {
            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            int index = EnsureRulesSection(lines);

            // Find the insertion point (after existing rules, before next section or empty line)
            int insertIndex = index + 1;
            while (insertIndex < lines.Count &&
                   !lines[insertIndex].StartsWith("##") &&
                   (lines[insertIndex].StartsWith("-") || string.IsNullOrWhiteSpace(lines[insertIndex])))
            {
                insertIndex++;
            }

            // Insert new rules before the next section
            foreach (var rule in rules)
            {
                if (_rulesService.TryParse(rule, out Rule _))
                {
                    lines.Insert(insertIndex, $"- {rule}");
                    insertIndex++;
                }
            }

            await File.WriteAllLinesAsync(path, lines, cancellationToken);
        }

        public async Task AddRuleWithEnforcementAsync(string path, string ruleText, Enforcement enforcement, CancellationToken cancellationToken)
        {
            if (!_rulesService.TryParse(ruleText, out Rule _))
                throw new ArgumentException($"Invalid rule text: '{ruleText}'. Rule must be one of the allowed rules.");

            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            int index = EnsureRulesSection(lines);

            // Find the insertion point (after existing rules, before next section)
            int insertIndex = index + 1;
            while (insertIndex < lines.Count &&
                   !lines[insertIndex].StartsWith("##") &&
                   (lines[insertIndex].StartsWith("-") || string.IsNullOrWhiteSpace(lines[insertIndex])))
            {
                insertIndex++;
            }

            // Insert the rule with enforcement level
            string formattedRule = EnforcementParser.FormatRuleWithEnforcement(ruleText, enforcement);
            lines.Insert(insertIndex, $"- {formattedRule}");

            await File.WriteAllLinesAsync(path, lines, cancellationToken);
        }

        public async Task DeleteRulesAsync(string path, CancellationToken cancellationToken, params string[] rules)
        {
            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            int index = EnsureRulesSection(lines);

            var rulesToDelete = new HashSet<string>(rules, StringComparer.OrdinalIgnoreCase);

            // Find the end of the rules section
            int endIndex = index + 1;
            while (endIndex < lines.Count && !lines[endIndex].StartsWith("##"))
            {
                endIndex++;
            }

            // Iterate backwards within the rules section to safely remove items
            for (int i = endIndex - 1; i > index; i--)
            {
                if (lines[i].StartsWith("-"))
                {
                    string lineContent = lines[i].TrimStart('-', ' ').Trim();
                    // Parse to extract just the rule text (without enforcement)
                    var (ruleText, _) = EnforcementParser.ParseRuleWithEnforcement(lineContent);
                    if (rulesToDelete.Contains(ruleText))
                    {
                        lines.RemoveAt(i);
                    }
                }
            }

            await File.WriteAllLinesAsync(path, lines, cancellationToken);
        }

        public async Task UpdateRuleEnforcementAsync(string path, string ruleText, Enforcement enforcement, CancellationToken cancellationToken)
        {
            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            int index = EnsureRulesSection(lines);

            // Find the end of the rules section
            int endIndex = index + 1;
            while (endIndex < lines.Count && !lines[endIndex].StartsWith("##"))
            {
                endIndex++;
            }

            // Find and update the rule
            for (int i = index + 1; i < endIndex; i++)
            {
                if (lines[i].StartsWith("-"))
                {
                    string lineContent = lines[i].TrimStart('-', ' ').Trim();
                    var (existingRuleText, _) = EnforcementParser.ParseRuleWithEnforcement(lineContent);

                    if (existingRuleText.Equals(ruleText, StringComparison.OrdinalIgnoreCase))
                    {
                        // Update the line with new enforcement level
                        string formattedRule = EnforcementParser.FormatRuleWithEnforcement(ruleText, enforcement);
                        lines[i] = $"- {formattedRule}";
                        break;
                    }
                }
            }

            await File.WriteAllLinesAsync(path, lines, cancellationToken);
        }

        public async Task<List<string>> GetDocumentedFilesAsync(string path, CancellationToken cancellationToken)
        {
            string[] lines = await File.ReadAllLinesAsync(path, cancellationToken);
            int index = lines.IndexOf(STRINGS.FILE_HEADER);
            if (index == -1)
                return new();

            List<string> files = new List<string>();
            for (int i = index + 1; i < lines.Length; i++)
            {
                // Stop at next section header
                if (lines[i].StartsWith("##"))
                    break;

                // Skip empty lines and comments
                if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].TrimStart().StartsWith("#"))
                    continue;

                // Extract file path from list item (- path/to/file.cs) or plain text
                string lineContent = lines[i].TrimStart('-', '*', ' ').Trim();

                // Handle markdown links like [filename](path/to/file.cs)
                if (lineContent.Contains("]("))
                {
                    var linkMatch = System.Text.RegularExpressions.Regex.Match(lineContent, @"\]\(([^)]+)\)");
                    if (linkMatch.Success)
                    {
                        lineContent = linkMatch.Groups[1].Value;
                    }
                }

                // Handle inline code like `path/to/file.cs`
                lineContent = lineContent.Trim('`');

                if (!string.IsNullOrEmpty(lineContent))
                {
                    files.Add(lineContent);
                }
            }
            return files;
        }
    }
}
