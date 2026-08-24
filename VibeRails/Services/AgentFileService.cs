using System.Text;
using VibeRails.Services.VCA;
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
        /// Returns an insertion point in the first live rules section, creating a canonical
        /// section when none exists. Discovery owns the Markdown parsing so fenced examples and
        /// legacy headings cannot send writes to a section the Rules page does not display.
        ///
        /// Adds target the first section only; update and delete deliberately span all of them.
        /// A new rule has to land in exactly one place, but an existing rule may already be
        /// declared in several, and leaving the other copies alone is what made a rule the Rules
        /// page had just changed or removed still fire from the hook.
        /// </summary>
        private static int EnsureRulesSectionAndGetInsertIndex(List<string> lines)
        {
            var document = AgentRuleSectionReader.ParseDocument(string.Join("\n", lines));
            var section = document.Sections.FirstOrDefault();
            if (section is null)
            {
                lines.Add("");
                lines.Add(STRINGS.RULE_HEADER);
                return lines.Count;
            }

            var lastRule = document.Rules.LastOrDefault(
                rule => rule.SectionHeadingLineIndex == section.HeadingLineIndex);
            return lastRule?.LineIndex + 1 ?? section.HeadingLineIndex + 1;
        }

        /// <summary>
        /// Rejects rule text vc.rules.md cannot hold on a single line.
        ///
        /// Every writer below emits the rule as one "- {rule}" list item, so a line break in the
        /// text becomes two real lines on disk. A second line beginning with '#' closes the rules
        /// section — <see cref="AgentRuleSectionReader"/> ends one at any non-rules heading — which
        /// drops every rule below it from the Git hook and the Rules page at once. That reads as an
        /// empty section rather than as tampering, so it is refused at the boundary instead.
        /// </summary>
        private static void EnsureSingleLineRule(string ruleText)
        {
            if (ruleText is not null && ruleText.AsSpan().ContainsAny('\r', '\n', '\0'))
            {
                throw new ArgumentException(
                    "A rule may not contain a line break or null character.");
            }
        }

        /// <summary>
        /// Rejects rule text the validator will not be able to parse later.
        ///
        /// This is the only place that can stop malformed rule text from reaching vc.rules.md, so it
        /// fails closed: text that merely looks like a path lock but does not parse — a missing
        /// argument, an empty path, an embedded line break — is refused rather than written.
        /// Persisting it instead would leave a rule the Git hook can see, cannot resolve, and
        /// reports on every commit.
        /// </summary>
        private async Task ValidateRuleTextForWriteAsync(
            string agentPath,
            string ruleText,
            CancellationToken cancellationToken)
        {
            EnsureSingleLineRule(ruleText);

            if (!PathLockRule.TryParse(ruleText, out var pathLock))
            {
                if (PathLockRule.LooksLikePathLock(ruleText))
                {
                    throw new ArgumentException(
                        $"Invalid path lock '{ruleText}': use "
                        + $"{PathLockRule.FileTemplate} or {PathLockRule.DirectoryTemplate}.");
                }

                return;
            }

            var rootPath = await _gitService.GetRootPathAsync(cancellationToken);
            if (!PathLockRule.TryResolveRepositoryPath(
                    pathLock,
                    agentPath,
                    rootPath,
                    out _,
                    out var error))
            {
                throw new ArgumentException($"Invalid path lock '{ruleText}': {error}.");
            }
        }

        public async Task<List<string>> GetAgentFiles(CancellationToken cancellationToken)
        {
            if (!ParserConfigs.GetIsInGit())
                return new();

            var root = await _gitService.GetRootPathAsync(cancellationToken);

            // Enumerate through git instead of walking the tree: the raw recursive walk
            // visits .git, bin/obj, node_modules and every other ignored directory —
            // tens of thousands of entries per call on a warm repo, seconds on a laptop
            // with AV — and it surfaced stale vc.rules.md copies inside build output that
            // the Git hook never enforces. Tracked + untracked-unignored is exactly the
            // hook's universe, so the Rules page and the hook now agree.
            var viaGit = await TryGetAgentFilesViaGitAsync(root, cancellationToken);
            if (viaGit is not null)
                return viaGit;

            // Fallback (git missing or timed out): the original full walk, so the Rules
            // page still works rather than reading an error or an empty list.
            return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                .Where(f => IsAgentFileName(Path.GetFileName(f)))
                .Select(Path.GetFullPath)
                .ToList();
        }

        private static bool IsAgentFileName(string name)
        {
            return name.Equals("vc.rules.md", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<List<string>?> TryGetAgentFilesViaGitAsync(string root, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(root))
                return null;

            try
            {
                var result = await GitProcessRunner.RunRawAsync(
                    ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
                    root,
                    TimeSpan.FromSeconds(10),
                    cancellationToken);

                if (result.TimedOut || result.ExitCode != 0)
                    return null;

                var files = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var relative in result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!IsAgentFileName(Path.GetFileName(relative)))
                        continue;

                    var fullPath = Path.GetFullPath(Path.Combine(root, relative));
                    // --cached also lists files deleted from disk but still in the index.
                    if (File.Exists(fullPath) && seen.Add(fullPath))
                        files.Add(fullPath);
                }

                return files;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        public async Task CreateAgentFileAsync(string path, CancellationToken cancellationToken, params string[] rules)
        {
            foreach (var rule in rules)
            {
                await ValidateRuleTextForWriteAsync(path, rule, cancellationToken);
            }

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
            // Validate this is actually a rule file
            var agentFiles = await GetAgentFiles(cancellationToken);
            var normalizedPath = Path.GetFullPath(path);

            if (!agentFiles.Any(f => Path.GetFullPath(f).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UnauthorizedAccessException($"Path is not a valid rule file: {path}");
            }

            return await File.ReadAllTextAsync(normalizedPath, cancellationToken);
        }

        public async Task<List<string>> GetRulesAsync(string path, CancellationToken cancellationToken)
        {
            var rulesWithEnforcement = await GetRulesWithEnforcementAsync(path, cancellationToken);
            return rulesWithEnforcement.Select(r => r.RuleText).ToList();
        }

        /// <summary>
        /// Rules this rule file declares, read through <see cref="AgentRuleSectionReader"/> — the
        /// same reader the Git hook uses.
        ///
        /// Unrecognized rule text is returned rather than dropped. Silently hiding a rule the hook
        /// can still see is what let a commit be blocked by a rule the Rules page insisted did not
        /// exist. Writing is still validated (see <see cref="AddRulesAsync"/>), so the UI cannot
        /// author junk; it just no longer pretends hand-edited junk is absent.
        /// </summary>
        public async Task<List<RuleWithEnforcement>> GetRulesWithEnforcementAsync(string path, CancellationToken cancellationToken)
        {
            string content = await File.ReadAllTextAsync(path, cancellationToken);
            return AgentRuleSectionReader.Read(content)
                .Select(rule => new RuleWithEnforcement(
                    rule.RuleText,
                    EnforcementParser.Parse(rule.Enforcement)))
                .ToList();
        }

        public async Task AddRulesAsync(string path, CancellationToken cancellationToken, params string[] rules)
        {
            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            int insertIndex = EnsureRulesSectionAndGetInsertIndex(lines);

            // Insert new rules before the next section. Path locks are validated before the
            // TryParse gate so malformed lock syntax reports an error instead of being silently
            // dropped as an unknown rule.
            foreach (var rule in rules)
            {
                await ValidateRuleTextForWriteAsync(path, rule, cancellationToken);
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
            // Path-lock validation runs first so malformed lock syntax reports what is actually
            // wrong with it rather than the generic "not one of the allowed rules".
            await ValidateRuleTextForWriteAsync(path, ruleText, cancellationToken);

            if (!_rulesService.TryParse(ruleText, out Rule _))
                throw new ArgumentException($"Invalid rule text: '{ruleText}'. Rule must be one of the allowed rules.");

            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            int insertIndex = EnsureRulesSectionAndGetInsertIndex(lines);

            // Insert the rule with enforcement level
            string formattedRule = EnforcementParser.FormatRuleWithEnforcement(ruleText, enforcement);
            lines.Insert(insertIndex, $"- {formattedRule}");

            await File.WriteAllLinesAsync(path, lines, cancellationToken);
        }

        public async Task DeleteRulesAsync(string path, CancellationToken cancellationToken, params string[] rules)
        {
            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            var rulesToDelete = new HashSet<string>(rules, StringComparer.OrdinalIgnoreCase);
            var document = AgentRuleSectionReader.ParseDocument(string.Join("\n", lines));
            var lineIndexes = document.Rules
                .Where(rule => rulesToDelete.Contains(rule.Rule.RuleText))
                .Select(rule => rule.LineIndex)
                .Distinct()
                .OrderDescending();

            // Iterate backwards so source locations remain valid while rows are removed.
            var changed = false;
            foreach (var lineIndex in lineIndexes)
            {
                lines.RemoveAt(lineIndex);
                changed = true;
            }

            if (changed)
            {
                await File.WriteAllLinesAsync(path, lines, cancellationToken);
            }
        }

        /// <summary>
        /// Retargets every occurrence of the rule, not just the first.
        ///
        /// A file may declare more than one rules section, and the Git hook enforces the rules it
        /// finds in all of them. Updating only the first left the other copies at their old
        /// enforcement, so a rule the Rules page showed as WARN could still block a commit at
        /// STOP. This matches <see cref="DeleteRulesAsync"/>, which already removes every copy.
        /// </summary>
        public async Task UpdateRuleEnforcementAsync(string path, string ruleText, Enforcement enforcement, CancellationToken cancellationToken)
        {
            var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).ToList();
            var document = AgentRuleSectionReader.ParseDocument(string.Join("\n", lines));
            var matches = document.Rules
                .Where(rule => rule.Rule.RuleText.Equals(ruleText, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                return;
            }

            foreach (var match in matches)
            {
                var originalLine = lines[match.LineIndex];
                var indentationLength = originalLine.Length - originalLine.TrimStart().Length;
                var indentation = originalLine[..indentationLength];
                string formattedRule = EnforcementParser.FormatRuleWithEnforcement(match.Rule.RuleText, enforcement);
                lines[match.LineIndex] = $"{indentation}- {formattedRule}";
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
