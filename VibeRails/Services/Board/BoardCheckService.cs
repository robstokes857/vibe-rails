using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MintLint;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.Board;

/// <summary>Runs only the deterministic engines. Never calls the preflight pipeline or moves a card.</summary>
public sealed class BoardCheckService(
    GitStagedSnapshotProvider snapshots, IVcaHookValidationService vca,
    IServiceProvider services, ICodeAnalyzerIgnoreStore? ignores = null)
{
    public async Task<BoardCheckRecord> ExecuteAsync(string project, string workspace, string? cardKey,
        string runId, string actionId, JobActionKind kind, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var scope = JobCheckScope.Parse(arguments);
        var board = cardKey is null ? null : services.GetRequiredService<IBoardStore>();
        var card = board is null ? null : await board.FindCardAsync(project, cardKey!, ct)
            ?? throw new InvalidOperationException("The check's card is no longer available in this project.");
        var record = new BoardCheckRecord(Guid.NewGuid().ToString("N"), card?.Id ?? "", runId, actionId,
            kind == JobActionKind.CodeQuality ? "Code quality" : "VCA", "Running", scope.Kind,
            scope.Base, scope.Head, null, null, Version(kind), DateTime.UtcNow, null,
            "Capturing the selected scope.", 0, 0, 0, 0, [], WorkspacePath: workspace);
        if (board is not null && !await board.SaveCheckAsync(project, record, ct))
            throw new InvalidOperationException("Could not save the check attempt.");
        var evidenceSaved = false;
        try
        {
            IReadOnlyList<string>? commits = null;
            if (scope.Kind == "card")
            {
                if (board is null || card is null)
                    throw new InvalidOperationException("Card scope requires a Board card. Select commit scope for a standalone check.");
                commits = (await board.GetCommitsAsync(project, card.Id, ct)).Select(commit => commit.Sha).ToArray();
                if (commits.Count == 0) throw new NoAttachedCodeException(card.Type == BoardCardTypes.ResearchSpike);
            }
            var snapshot = await CaptureAsync(workspace, scope, ct, captureImpact: kind == JobActionKind.CodeQuality, commits: commits);
            record = record with { BaseCommit = snapshot.CheckIdentity?.BaseCommit,
                HeadCommit = snapshot.CheckIdentity?.HeadCommit, SnapshotHash = Fingerprint(snapshot),
                RulesHash = RulesFingerprint(snapshot), FileCount = snapshot.Files.Count,
                ScopeFiles = snapshot.Files.Select(file => $"{file.ChangeKind}: {file.RelativePath}").ToList() };
            // Freeze the preferences once: the evidence and engine must use the same rules. Like the
            // commit hook's MintLint step, a store failure means nothing is ignored, never a failed check.
            IReadOnlyList<CodeAnalyzerIgnoredFile>? ignoreRules = null;
            var ignoresUnavailable = false;
            if (kind == JobActionKind.CodeQuality)
            {
                try { ignoreRules = ignores is null ? [] : await ignores.GetIgnoreRulesAsync(snapshot.RepositoryPath, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "[BoardChecks] Could not load the Code quality ignore list; checking every file.");
                    ignoreRules = [];
                    ignoresUnavailable = true;
                }
            }
            record = record with { Evidence = Evidence(snapshot, ignoreRules) };
            // Preserve captured inputs even if the process ends during analysis. Once stored, later
            // saves pass null, which keeps them, instead of serializing and sending a second copy
            // (SQLite still rewrites the row on the completion update; see CHECKS.md).
            if (board is not null && !await board.SaveCheckAsync(project, record, ct))
                throw new InvalidOperationException("Could not save the captured check inputs.");
            evidenceSaved = true;
            var invocation = new VcaHookInvocation(VcaHookKind.Preview, null, workspace, false, TimeSpan.Zero,
                false, WorkingTreeScope: scope.Kind == "working-tree");
            var request = new GitPreflightRequest(workspace, invocation,
                WorkingTreeChanges: scope.Kind == "working-tree", UnpushedChanges: scope.Kind != "working-tree", ScopeLabel: scope.Kind);
            var step = kind == JobActionKind.CodeQuality
                ? (IGitPreflightStep)new MintLintPreflightStep(ignores, ignoreRules) : new VcaPreflightStep(vca);
            var result = await step.ExecuteAsync(new(runId, request, snapshot,
                (_, _, _) => ValueTask.CompletedTask), ct);
            ct.ThrowIfCancellationRequested();
            record = Complete(record, snapshot, result, kind);
            if (ignoresUnavailable)
                record = record with { Limitations = [.. record.Limitations, "The Code quality ignore list could not be read; no files were ignored."] };
        }
        catch (OperationCanceledException)
        {
            record = record with { Status = "Cancelled", Summary = "Check cancelled; analysis is incomplete.", EndedUtc = DateTime.UtcNow };
        }
        catch (NoCommitsException ex)
        {
            record = record with { Status = "Skipped/not applicable", Summary = ex.Message,
                Limitations = ["No baseline commit is available for this scope; no files were analyzed."], EndedUtc = DateTime.UtcNow };
        }
        catch (NoAttachedCodeException ex)
        {
            record = record with { Status = ex.ResearchSpike ? "Skipped/not applicable" : "Needs review",
                Summary = ex.ResearchSpike ? "Research spike: no code attached to this card." : "no code attached to this card",
                Limitations = ["No files were analyzed. Attach commits and run the check again, or explicitly skip the lane check for non-code work."],
                EndedUtc = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            record = record with { Status = "Failed to run", Summary = ex.Message, EndedUtc = DateTime.UtcNow };
        }
        if (board is not null && !await board.SaveCheckAsync(project, evidenceSaved ? record with { Evidence = null } : record, CancellationToken.None))
            throw new InvalidOperationException("The check finished but its evidence could not be saved.");
        return record;
    }

    /// <summary>The captured inputs. An impact source identical to a scope file's text is that file:
    /// on a working-tree or committed check most changed files would otherwise be stored twice.</summary>
    internal static BoardCheckEvidence Evidence(GitStagedSnapshot snapshot, IReadOnlyList<CodeAnalyzerIgnoredFile>? ignoreRules)
    {
        var scopeText = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
            if (file.Content is not null) scopeText.TryAdd(file.RelativePath, file.Content);
        return new(
            snapshot.Files.Select(file => new BoardCheckFile(file.RelativePath, file.PreviousRelativePath,
                file.ChangeKind.ToString(), file.IsBinary, file.Content, file.PreviousContent,
                file.AddedContent, file.AddedLineNumbers)).ToList(),
            snapshot.AgentFiles.Select(file => new BoardCheckTextFile(file.RelativePath, file.Content)).ToList(),
            snapshot.ImpactFiles?
                .Where(file => !(scopeText.TryGetValue(file.RelativePath, out var text) && string.Equals(text, file.Content, StringComparison.Ordinal)))
                .Select(file => new BoardCheckTextFile(file.RelativePath, file.Content)).ToList(),
            ignoreRules, snapshot.CheckIdentity?.CommitShas);
    }

    internal async Task<GitStagedSnapshot> CaptureAsync(string workspace, JobCheckScope scope, CancellationToken ct,
        bool captureImpact = true, IReadOnlyList<string>? commits = null)
    {
        if (scope.Kind is "card" or "commit")
            return await snapshots.CaptureCommitsAsync(workspace,
                scope.Kind == "commit" ? [scope.Head!] : commits ?? [], captureImpact, ct);
        if (scope.Kind == "range")
        {
            if (!JobCheckScope.IsSha(scope.Base) || !JobCheckScope.IsSha(scope.Head))
                throw new ArgumentException("Check ranges require full base and head commit SHAs.");
            return await snapshots.CaptureRangeAsync(workspace, scope.Base!, scope.Head!, captureImpact, ct);
        }
        var head = await ReadHeadAsync(workspace, ct, allowUnborn: true);
        if (scope.Kind == "repository") return await snapshots.CaptureRepositoryAsync(workspace, captureImpact, ct);
        if (scope.Kind == "unpushed") return await snapshots.CaptureUnpushedAsync(workspace, captureImpact, ct);
        var snapshot = captureImpact ? await snapshots.CaptureWorkingTreeCheckAsync(workspace, ct) : await snapshots.CaptureWorkingTreeAsync(workspace, ct);
        // The second pass only proves the changed set held still. Impact sources are unchanged tracked
        // files: one edited after the first pass read it shows up here as a changed file instead.
        var check = await snapshots.CaptureWorkingTreeAsync(workspace, ct);
        if (head != await ReadHeadAsync(workspace, ct) || Fingerprint(snapshot with { ImpactFiles = null }) != Fingerprint(check))
            throw new InvalidOperationException("Working files or HEAD changed during capture. Run checks again.");
        return snapshot with { CheckIdentity = new(head, head) };
    }

    public async Task<string> FreshnessAsync(string workspace, BoardCheckRecord record, CancellationToken ct)
    {
        if (record.SnapshotHash is null || record.EndedUtc is null) return "Unknown freshness";
        try
        {
            IReadOnlyList<string>? commits = null;
            if (record.Scope == "card")
            {
                commits = (await services.GetRequiredService<IBoardStore>().GetCommitsAsync(workspace, record.CardId, ct))
                    .Select(commit => commit.Sha).ToArray();
                if (commits.Count == 0) return "Stale: no commits are attached to the card anymore";
            }
            var snapshot = await CaptureAsync(workspace, new(record.Scope, record.BaseCommit, record.HeadCommit), ct,
                captureImpact: record.Tool != "VCA", commits: commits);
            var current = Fingerprint(snapshot) == record.SnapshotHash
                && Version(record.Tool == "VCA" ? JobActionKind.Vca : JobActionKind.CodeQuality) == record.ToolVersion;
            // A pinned range remains valid for that range, but does not cover newer HEAD changes.
            if (record.Scope == "range" && record.HeadCommit != await ReadHeadAsync(workspace, ct)) current = false;
            return current ? "Captured inputs match; local ignore settings may have changed" : "Stale: inputs or tool version changed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "Unknown freshness: scope is unavailable"; }
    }

    internal static BoardCheckRecord Complete(BoardCheckRecord record, GitStagedSnapshot snapshot,
        GitPreflightStepResult result, JobActionKind kind)
    {
        var quality = kind == JobActionKind.CodeQuality;
        int Count(string key) => result.Details?.TryGetValue(key, out var text) == true && int.TryParse(text, out var value) ? value : 0;
        var analyzed = quality ? Count("supportedFileCount") : result.VcaSummary?.ApplicableRuleCount ?? 0;
        var skipped = quality ? Count("skippedFileCount") + Count("ignoredFileCount")
            : snapshot.Files.Count(file => file.IsBinary || !file.ExistsInIndex || file.Content is null);
        var deferred = Count("deferredCount");
        var missingRules = (snapshot.TrackedFiles ?? []).Concat(snapshot.Files.Select(file => file.RelativePath))
            .Distinct(StringComparer.Ordinal).Where(path => Path.GetFileName(path).Equals("vc.rules.md", StringComparison.OrdinalIgnoreCase))
            .Count(path => !snapshot.AgentFiles.Any(file => file.RelativePath == path)
                && !snapshot.Files.Any(file => file.RelativePath == path && file.ChangeKind == GitStagedChangeKind.Deleted));
        var findings = quality ? QualityFindingCount(result) : result.VcaSummary?.Findings?.Count(f => f.Kind != VcaRuleFindingKind.Deferred) ?? 0;
        var limitations = new List<string>
        {
            record.Scope == "working-tree"
                ? "Working-tree changes relative to HEAD, including untracked files; committed changes are excluded. All changed files in this checkout are included, even if unrelated to this card."
                : record.Scope == "repository" ? "All committed files at HEAD; dirty and untracked working files are excluded."
                : record.Scope is "card" or "commit" ? "Only surviving added lines from the selected commits at the newest selected tree are analyzed. Interleaved commits and dirty/untracked files are excluded; rules and impact context come from that committed tree."
                : "Net changes between the saved base and head; dirty and untracked working files are excluded.",
            quality ? "Code quality is advisory. Only added code in supported languages is analyzed; reach is lexical, not compiler coverage. Local ignore settings apply."
                : "VCA preserves WARN, COMMIT and STOP findings. This run does not acknowledge commit messages or enforce a Git operation."
        };
        if (skipped > 0) limitations.Add($"{skipped} files skipped, ignored, deleted, binary or unavailable; see evidence for coverage.");
        if (quality && snapshot.ImpactFilesOmitted > 0)
            limitations.Add($"{snapshot.ImpactFilesOmitted} source files were left out of impact ranking by the capture size budget; reference counts may be low.");
        if (deferred > 0) limitations.Add($"{deferred} rules deferred; commit-message validation remains outstanding.");
        if (!quality && missingRules > 0) limitations.Add($"{missingRules} rule files could not be captured. Rule coverage is incomplete.");
        if (analyzed == 0) limitations.Add(quality ? "No supported added code was analyzed." : "No applicable rules were analyzed.");
        var status = result.Status switch
        {
            GitPreflightStepStatus.Error => "Failed to run",
            GitPreflightStepStatus.Cancelled => "Cancelled",
            GitPreflightStepStatus.Warning or GitPreflightStepStatus.Blocked => "Findings",
            GitPreflightStepStatus.Skipped => "Skipped/not applicable",
            _ when analyzed == 0 => "Skipped/not applicable",
            _ when skipped > 0 || deferred > 0 || (!quality && missingRules > 0) => "Completed with limitations",
            _ => "Passed"
        };
        var summary = quality && result.Status is not (GitPreflightStepStatus.Error or GitPreflightStepStatus.Cancelled)
            ? $"{analyzed} files analyzed; {findings} files with code-quality findings; {skipped} skipped. Advisory analysis."
            : status is "Skipped/not applicable" or "Completed with limitations" && result.Status == GitPreflightStepStatus.Passed
                ? "Analysis completed with incomplete or missing coverage. See limitations." : result.Summary;
        return record with { Status = status, Summary = summary, AnalyzedCount = analyzed,
            SkippedCount = skipped, FindingCount = findings, Limitations = limitations,
            EndedUtc = DateTime.UtcNow,
            ResultJson = JsonSerializer.Serialize(result, AppJsonSerializerContext.Default.GitPreflightStepResult) };
    }

    private static int QualityFindingCount(GitPreflightStepResult result)
    {
        if (result.Details?.TryGetValue("report", out var json) != true) return 0;
        var report = JsonSerializer.Deserialize(json!, AppJsonSerializerContext.Default.MintLintReportResponse);
        return report?.Files.Count(file => file.Rating is "NeedsWork" or "AtRisk") ?? 0;
    }

    private static string Version(JobActionKind kind) => (kind == JobActionKind.CodeQuality
        ? typeof(MintLintAnalyzer).Assembly : typeof(VcaPreflightStep).Assembly).GetName().Version?.ToString() ?? "unknown";

    private static async Task<string> ReadHeadAsync(string workspace, CancellationToken ct, bool allowUnborn = false)
    {
        var head = await GitCli.RunAsync(workspace, ["rev-parse", "--verify", "HEAD^{commit}"], ct);
        if (head.Succeeded) return head.StdOut.Trim();
        if (head.TimedOut) throw new TimeoutException("Git timed out while resolving HEAD.");

        // Only a symbolic HEAD pointing to a missing branch is unborn. A detached HEAD,
        // corrupt object/ref, invalid repository or failed Git command must remain an error.
        var symbolic = await GitCli.RunAsync(workspace, ["symbolic-ref", "-q", "HEAD"], ct);
        if (symbolic.Succeeded && symbolic.StdOut.Trim().StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var branch = await GitCli.RunAsync(workspace, ["show-ref", "--verify", "--quiet", symbolic.StdOut.Trim()], ct);
            if (allowUnborn && !branch.TimedOut && branch.ExitCode == 1) throw new NoCommitsException();
        }
        throw new InvalidOperationException("Cannot resolve HEAD. Check that the repository and its HEAD reference are valid.");
    }

    private sealed class NoCommitsException() : InvalidOperationException("Nothing to check: this branch has no commits yet.");
    private sealed class NoAttachedCodeException(bool researchSpike) : InvalidOperationException
    {
        public bool ResearchSpike { get; } = researchSpike;
    }

    internal static string RulesFingerprint(GitStagedSnapshot snapshot) => Hash(snapshot.AgentFiles
        .OrderBy(file => file.RelativePath, StringComparer.Ordinal).SelectMany(file => new[] { file.RelativePath, file.Content }));

    internal static string Fingerprint(GitStagedSnapshot snapshot) => Hash(new[]
        { snapshot.CheckIdentity?.BaseCommit ?? "", snapshot.CheckIdentity?.HeadCommit ?? "", RulesFingerprint(snapshot) }
        .Concat(snapshot.CheckIdentity?.CommitShas ?? [])
        .Concat(snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).SelectMany(file => new[]
        { file.RelativePath, file.PreviousRelativePath ?? "", file.ChangeKind.ToString(), file.Content ?? "<unavailable>",
            file.PreviousContent ?? "", file.AddedContent ?? "", file.IsBinary.ToString() }))
        .Concat((snapshot.ImpactFiles ?? []).OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .SelectMany(file => new[] { file.RelativePath, file.Content })));

    private static string Hash(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
