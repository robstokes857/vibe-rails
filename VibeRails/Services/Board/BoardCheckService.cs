using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MintLint;
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
        try
        {
            var snapshot = await CaptureAsync(workspace, scope, ct);
            record = record with { BaseCommit = snapshot.CheckIdentity?.BaseCommit,
                HeadCommit = snapshot.CheckIdentity?.HeadCommit, SnapshotHash = Fingerprint(snapshot),
                RulesHash = RulesFingerprint(snapshot), FileCount = snapshot.Files.Count,
                ScopeFiles = snapshot.Files.Select(file => $"{file.ChangeKind}: {file.RelativePath}").ToList() };
            var invocation = new VcaHookInvocation(VcaHookKind.Preview, null, workspace, false, TimeSpan.Zero,
                false, WorkingTreeScope: scope.Kind == "working-tree");
            var request = new GitPreflightRequest(workspace, invocation,
                WorkingTreeChanges: scope.Kind == "working-tree", UnpushedChanges: scope.Kind != "working-tree", ScopeLabel: scope.Kind);
            var step = kind == JobActionKind.CodeQuality
                ? (IGitPreflightStep)new MintLintPreflightStep(ignores) : new VcaPreflightStep(vca);
            var result = await step.ExecuteAsync(new(runId, request, snapshot,
                (_, _, _) => ValueTask.CompletedTask), ct);
            ct.ThrowIfCancellationRequested();
            record = Complete(record, snapshot, result, kind);
        }
        catch (OperationCanceledException)
        {
            record = record with { Status = "Cancelled", Summary = "Check cancelled; analysis is incomplete.", EndedUtc = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            record = record with { Status = "Failed to run", Summary = ex.Message, EndedUtc = DateTime.UtcNow };
        }
        if (board is not null && !await board.SaveCheckAsync(project, record, CancellationToken.None))
            throw new InvalidOperationException("The check finished but its evidence could not be saved.");
        return record;
    }

    internal async Task<GitStagedSnapshot> CaptureAsync(string workspace, JobCheckScope scope, CancellationToken ct)
    {
        if (scope.Kind == "range") return await snapshots.CaptureRangeAsync(workspace, scope.Base!, scope.Head!, ct);
        if (scope.Kind == "repository") return await snapshots.CaptureRepositoryAsync(workspace, ct);
        if (scope.Kind == "unpushed") return await snapshots.CaptureUnpushedAsync(workspace, ct);
        var head = await ReadHeadAsync(workspace, ct);
        var snapshot = await snapshots.CaptureWorkingTreeAsync(workspace, ct);
        var check = await snapshots.CaptureWorkingTreeAsync(workspace, ct);
        if (head != await ReadHeadAsync(workspace, ct) || Fingerprint(snapshot) != Fingerprint(check))
            throw new InvalidOperationException("Working files or HEAD changed during capture. Run checks again.");
        return snapshot with { CheckIdentity = new(head, head) };
    }

    public async Task<string> FreshnessAsync(string workspace, BoardCheckRecord record, CancellationToken ct)
    {
        if (record.SnapshotHash is null || record.EndedUtc is null) return "Unknown freshness";
        try
        {
            var snapshot = await CaptureAsync(workspace, new(record.Scope, record.BaseCommit, record.HeadCommit), ct);
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
                : "Net changes between the saved base and head; dirty and untracked working files are excluded. Linked card commits were not used to infer scope.",
            quality ? "Code quality is advisory. Only added code in supported languages is analyzed; reach is lexical, not compiler coverage. Local ignore settings apply."
                : "VCA preserves WARN, COMMIT and STOP findings. This run does not acknowledge commit messages or enforce a Git operation."
        };
        if (skipped > 0) limitations.Add($"{skipped} files skipped, ignored, deleted, binary or unavailable; see evidence for coverage.");
        if (deferred > 0) limitations.Add($"{deferred} rules deferred; commit-message validation remains outstanding.");
        if (!quality && missingRules > 0) limitations.Add($"{missingRules} rule files could not be captured. Rule coverage is incomplete.");
        if (quality && record.Scope == "working-tree") limitations.Add("Impact ranking reads live tracked sources and is not captured atomically with the changed files.");
        if (analyzed == 0) limitations.Add(quality ? "No supported added code was analyzed." : "No applicable rules were analyzed.");
        var status = result.Status switch
        {
            GitPreflightStepStatus.Error => "Failed to run",
            GitPreflightStepStatus.Cancelled => "Cancelled",
            GitPreflightStepStatus.Warning or GitPreflightStepStatus.Blocked => "Findings",
            GitPreflightStepStatus.Skipped => "Skipped/not applicable",
            _ when analyzed == 0 || skipped > 0 || deferred > 0 || (!quality && missingRules > 0) => "Skipped/not applicable",
            _ => "Passed"
        };
        var summary = quality && result.Status is not (GitPreflightStepStatus.Error or GitPreflightStepStatus.Cancelled)
            ? $"{analyzed} files analyzed; {findings} files with code-quality findings; {skipped} skipped. Advisory analysis."
            : status == "Skipped/not applicable" && result.Status == GitPreflightStepStatus.Passed
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

    private static async Task<string> ReadHeadAsync(string workspace, CancellationToken ct)
    {
        var head = await GitCli.RunAsync(workspace, ["rev-parse", "--verify", "HEAD^{commit}"], ct);
        if (!head.Succeeded) throw new InvalidOperationException("Cannot resolve HEAD. Commit an initial baseline before running checks.");
        return head.StdOut.Trim();
    }

    internal static string RulesFingerprint(GitStagedSnapshot snapshot) => Hash(snapshot.AgentFiles
        .OrderBy(file => file.RelativePath, StringComparer.Ordinal).SelectMany(file => new[] { file.RelativePath, file.Content }));

    internal static string Fingerprint(GitStagedSnapshot snapshot) => Hash(new[]
        { snapshot.CheckIdentity?.BaseCommit ?? "", snapshot.CheckIdentity?.HeadCommit ?? "", RulesFingerprint(snapshot) }
        .Concat(snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).SelectMany(file => new[]
        { file.RelativePath, file.PreviousRelativePath ?? "", file.ChangeKind.ToString(), file.Content ?? "<unavailable>",
            file.PreviousContent ?? "", file.AddedContent ?? "", file.IsBinary.ToString() })));

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
