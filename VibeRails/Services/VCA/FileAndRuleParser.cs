using VibeRails.Services;

namespace VibeRails.Services.VCA
{
    /// <summary>
    /// Builds the mapping of files to rules by combining git changes with AGENTS.md rules
    /// </summary>
    public class FileAndRuleParser : IFileAndRuleParser
    {
        private readonly IGitService _gitService;
        private readonly IAgentFileService _agentFileService;
        private readonly IPathNormalizer _pathNormalizer;

        public FileAndRuleParser(
            IGitService gitService,
            IAgentFileService agentFileService,
            IPathNormalizer pathNormalizer)
        {
            _gitService = gitService;
            _agentFileService = agentFileService;
            _pathNormalizer = pathNormalizer;
        }

        public async Task<Dictionary<string, List<RuleWithSource>>> GetFilesAndRulesAsync(
            string rootPath,
            bool stagedOnly,
            CancellationToken cancellationToken)
        {
            // Get changed files from git
            var changedFiles = stagedOnly
                ? await _gitService.GetStagedFilesAsync(cancellationToken)
                : await _gitService.GetChangedFileAsync(cancellationToken);
            if (changedFiles.Count == 0)
            {
                return new Dictionary<string, List<RuleWithSource>>();
            }

            // Get all AGENTS.md files and their rules
            var agentFiles = await _agentFileService.GetAgentFiles(cancellationToken);
            if (agentFiles.Count == 0)
            {
                return new Dictionary<string, List<RuleWithSource>>();
            }

            // Build dictionary: file path -> list of applicable rules with source
            var fileToRulesMap = new Dictionary<string, List<RuleWithSource>>(StringComparer.OrdinalIgnoreCase);

            foreach (var agentFile in agentFiles)
            {
                var rules = await _agentFileService.GetRulesWithEnforcementAsync(agentFile, cancellationToken);
                if (rules.Count == 0)
                    continue;

                // Get files that are in scope for this agent
                var scopedFiles = _pathNormalizer.GetScopedFiles(changedFiles, agentFile, rootPath);

                // Add rules to each scoped file
                foreach (var file in scopedFiles)
                {
                    var normalizedFile = _pathNormalizer.Normalize(file, rootPath);

                    if (!fileToRulesMap.ContainsKey(normalizedFile))
                    {
                        fileToRulesMap[normalizedFile] = new List<RuleWithSource>();
                    }

                    // Add rules with their source file
                    foreach (var rule in rules)
                    {
                        var ruleWithSource = new RuleWithSource(rule, agentFile);
                        // Check for duplicates (same rule from same source)
                        if (!fileToRulesMap[normalizedFile].Any(r =>
                            r.Rule.Equals(ruleWithSource.Rule) &&
                            r.SourceFile.Equals(ruleWithSource.SourceFile, StringComparison.OrdinalIgnoreCase)))
                        {
                            fileToRulesMap[normalizedFile].Add(ruleWithSource);
                        }
                    }
                }
            }

            return fileToRulesMap;
        }
    }
}
