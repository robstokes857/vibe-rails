using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MintLint;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.Board;

/// <summary>Runs only the deterministic engines. Never calls the preflight pipeline or moves a card.</summary>
public sealed class BoardCheckService(
    GitStagedSnapshotProvider snapshots, IVcaHookValidationService vca,
    IServiceProvider services, ICodeAnalyzerIgnoreStore? ignores = null, CodeQualityExemptionService? exemptions = null)
{
    public async Task<BoardCheckRecord> ExecuteAsync(string project, string workspace, string? cardKey,
        string runId, string actionId, JobActionKind kind, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var scope = JobCheckScope.Parse(kind, arguments);
        var minimum = kind == JobActionKind.CodeQuality ? scope.MinimumGrade : null;
        var board = cardKey is null ? null : services.GetRequiredService<IBoardStore>();
        var card = board is null ? null : await board.FindCardAsync(project, cardKey!, ct)
            ?? throw new InvalidOperationException("The check's card is no longer available in this project.");
        var record = new BoardCheckRecord(Guid.NewGuid().ToString("N"), card?.Id ?? "", runId, actionId,
            kind == JobActionKind.CodeQuality ? "Code quality" : "VCA", "Running", scope.Kind,
            scope.Base, scope.Head, null, null, Version(kind), DateTime.UtcNow, null,
            "Capturing the selected scope.", 0, 0, 0, 0, [], WorkspacePath: workspace, MinimumGrade: minimum?.ToString());
        if (board is not null && !await board.SaveCheckAsync(project, record, ct))
            throw new InvalidOperationException("Could not save the check attempt.");
        var evidenceSaved = false;
        IReadOnlyList<string>? unlinked = null;
        try
        {
            IReadOnlyList<string>? commits = null;
            if (scope.Kind == "card")
            {
                if (board is null || card is null)
                    throw new InvalidOperationException("Card scope requires a Board card. Select commit scope for a standalone check.");
                commits = (await board.GetCommitsAsync(project, card.Id, ct)).Select(commit => commit.Sha).ToArray();
                unlinked = card.Type == BoardCardTypes.ResearchSpike ? null : await UnlinkedCommitsAsync(board, project, card.Id, ct);
                if (commits.Count == 0) throw new NoAttachedCodeException(card.Type == BoardCardTypes.ResearchSpike);
            }
            var snapshot = await CaptureAsync(workspace, scope, ct, captureImpact: kind == JobActionKind.CodeQuality, commits: commits);
            snapshot = snapshot with { QualityExemptions = exemptions is null ? []
                : await exemptions.GetRulesAsync(snapshot.RepositoryPath, ct, project, card?.Id) };
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
            record = record with { Evidence = Evidence(snapshot, [.. ignoreRules ?? [], .. snapshot.QualityExemptions]) };
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
            if (kind == JobActionKind.CodeQuality && record.Status is not ("Failed to run" or "Cancelled"))
                record = WithNotReviewed(record, NotReviewed(snapshot, ignoreRules ?? []));
            if (minimum is char grade && !record.FailsAction)
                record = WithHiddenFilesToFix(
                    ApplyMinimumGrade(record, grade, CodeQualityRule.EvaluateSnapshot(grade, snapshot, out var graded, ct),
                        HiddenByIgnores(snapshot, ignoreRules ?? [])),
                    graded, ignoreRules);
            record = WithUnlinkedCommits(record, unlinked);
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
            // A gate that graded nothing must not pass: the card's code was never seen.
            if (minimum is char grade && !ex.ResearchSpike)
                record = record with { MinimumGradeResult = BoardCheckRecord.GradeNotChecked, Summary = $"{record.Summary}. Minimum grade {grade} not checked; this step fails.",
                    GradeSummary = $"No code is attached to this card, so the minimum grade {grade} could not be checked. Link the card's commits and run the step again." };
            // Owner decision 2026-11-16: without a minimum too (BoardCheckRecord.NoCodeFailsCodeQuality).
            else if (kind == JobActionKind.CodeQuality && !ex.ResearchSpike)
                record = record with { Summary = $"{record.Summary}. This step fails: link the card's commits with link_board_commit, then retry it." };
            record = WithUnlinkedCommits(record, unlinked);
        }
        catch (Exception ex)
        {
            record = record with { Status = "Failed to run", Summary = ex.Message, EndedUtc = DateTime.UtcNow };
        }
        if (board is not null && !await board.SaveCheckAsync(project, evidenceSaved ? record with { Evidence = null } : record, CancellationToken.None))
            throw new InvalidOperationException("The check finished but its evidence could not be saved.");
        // Whatever lane or position ran it, the card's agents read the result where they read reviews.
        if (board is not null && card is not null && kind == JobActionKind.CodeQuality)
        {
            try
            {
                if (!await board.SaveCheckReviewAsync(project, CheckReport(record), CancellationToken.None, CheckReceipt(record)))
                    Log.Warning("[BoardChecks] Check {CheckId} finished, but its card was unavailable for the review report.", record.Id);
            }
            catch (Exception ex) { Log.Warning(ex, "[BoardChecks] Check {CheckId} finished, but its review report could not be saved.", record.Id); }
        }
        return record;
    }

    /// <summary>
    /// Commits this card's own sessions made (recorded by the post-commit hook) that are not linked
    /// to it, as "abc1234 subject". A failed read is logged and checks nothing: this is a backstop to
    /// the hook's automatic link, never a reason for the check itself to fail to run.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> UnlinkedCommitsAsync(IBoardStore board, string project, string cardId, CancellationToken ct)
    {
        try
        {
            var rows = await board.GetUnlinkedSessionCommitsAsync(project, cardId, ct);
            return rows.Count == 0 ? null : rows.Select(row => string.IsNullOrWhiteSpace(row.Message)
                ? row.Sha[..Math.Min(8, row.Sha.Length)]
                : $"{row.Sha[..Math.Min(8, row.Sha.Length)]} \"{BoardPromptComposer.SanitizeLine(row.Message, 80)}\"").ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[BoardChecks] Could not read the card's recorded session commits; unlinked commits were not checked.");
            return null;
        }
    }

    /// <summary>
    /// Names the card's unlinked commits. A Code quality step fails (owner decision 2026-11-16); an
    /// outcome that would otherwise read as passed becomes Needs review, so the card never shows a
    /// green check for code it did not see. VCA records them as a limitation and stays advisory.
    /// </summary>
    internal static BoardCheckRecord WithUnlinkedCommits(BoardCheckRecord record, IReadOnlyList<string>? unlinked)
    {
        if (unlinked is not { Count: > 0 } || record.Status is "Failed to run" or "Cancelled") return record;
        var sentence = BoardCheckRecord.UnlinkedCommitsSentence(unlinked);
        var limitation = $"{unlinked.Count} {(unlinked.Count == 1 ? "commit" : "commits")} made from this card's sessions "
            + $"{(unlinked.Count == 1 ? "is" : "are")} not linked to the card, so {(unlinked.Count == 1 ? "its" : "their")} code was not reviewed.";
        record = record with { UnlinkedCommits = unlinked, Limitations = [.. record.Limitations, limitation] };
        if (record.Tool != "Code quality") return record;
        return record with
        {
            Status = record.Status is "Passed" or "Completed with limitations" or "Skipped/not applicable" ? "Needs review" : record.Status,
            Summary = $"{record.Summary.TrimEnd()} {sentence}"
        };
    }

    // The reasons NotReviewed records. NotCheckedGroup sorts saved reasons by them, so keep the two together.
    private const string DeletedReason = "Deleted; no added code to review.";
    private const string BinaryReason = "Binary, over the 5 MiB text limit, or past the capture budget.";
    private const string UnreadableReason = "Could not be read from Git.";
    private const string UnsupportedReason = "Not a supported source language.";
    private const string ExemptedPrefix = "Exempted";
    private const string IgnoredPrefix = "Hidden by local Code quality ignore settings";
    private const string NoAddedLinesReason = "No added lines (only removals or whitespace).";

    /// <summary>Every scope file the Code quality engine did not analyse, and why, in path order.</summary>
    internal static IReadOnlyList<BoardCheckSkippedFile> NotReviewed(GitStagedSnapshot snapshot, IReadOnlyList<CodeAnalyzerIgnoredFile> ignoreRules)
    {
        var exemptions = snapshot.QualityExemptions ?? [];
        var result = new List<BoardCheckSkippedFile>();
        foreach (var file in snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            string? reason;
            if (file.ChangeKind == GitStagedChangeKind.Deleted || !file.ExistsInIndex) reason = DeletedReason;
            else if (file.IsBinary) reason = BinaryReason;
            else if (file.Content is null) reason = UnreadableReason;
            else if (!MintLintAnalyzer.SupportsFile(file.RelativePath)) reason = UnsupportedReason;
            else if (exemptions.FirstOrDefault(rule => CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, [rule])) is { } exemption)
                reason = $"{ExemptedPrefix}{(string.IsNullOrWhiteSpace(exemption.Author) ? "" : $" by {BoardPromptComposer.SanitizeLine(exemption.Author, 60)}")}: "
                    + BoardPromptComposer.SanitizeLine(exemption.ReasonText ?? "no reason recorded", 300);
            else if (ignoreRules.FirstOrDefault(rule => CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, [rule])) is { } ignore)
                reason = IgnoredPrefix
                    + (string.IsNullOrWhiteSpace(ignore.ReasonText) ? "." : ": " + BoardPromptComposer.SanitizeLine(ignore.ReasonText, 300));
            else if (string.IsNullOrWhiteSpace(file.AddedContent ?? file.Content)) reason = NoAddedLinesReason;
            else continue;
            result.Add(new(file.RelativePath, reason));
        }
        return result;
    }

    /// <summary>A saved not-reviewed reason in a few words, for counting them in the card's comment.</summary>
    internal static string NotCheckedGroup(string reason) =>
        reason.StartsWith(IgnoredPrefix, StringComparison.Ordinal) ? "hidden by your ignore list"
        : reason.StartsWith(ExemptedPrefix, StringComparison.Ordinal) ? "exempted"
        : reason switch
        {
            UnsupportedReason => "not a supported language",
            NoAddedLinesReason => "no added lines",
            DeletedReason => "deleted",
            BinaryReason => "binary or too large",
            UnreadableReason => "unreadable",
            _ => "other reasons"
        };

    private static BoardCheckRecord WithNotReviewed(BoardCheckRecord record, IReadOnlyList<BoardCheckSkippedFile> files) =>
        files.Count == 0 ? record : record with
        {
            NotReviewed = files.Take(BoardCheckRecord.NotReviewedLimit).ToList(),
            NotReviewedOmitted = Math.Max(0, files.Count - BoardCheckRecord.NotReviewedLimit)
        };

    /// <summary>The prefix of review reports that come from a saved check rather than a reviewer.</summary>
    public const string CheckReviewPrefix = "review_check_";

    /// <summary>A finished Code quality check as a canonical review report: the outcome, the minimum grade
    /// and the worst files, with the full evidence one <c>read_board_check</c> away.</summary>
    internal static BoardReviewRecord CheckReport(BoardCheckRecord record)
    {
        // A minimum that could not be checked leaves the step's question unanswered: incomplete, not a finding.
        var result = record.Status is "Failed to run" or "Cancelled" or "Needs review"
                || record.MinimumGradeResult == BoardCheckRecord.GradeNotChecked && record.Status != "Findings" ? "Incomplete"
            : record.Status == "Findings" ? "Findings" : "No findings reported";
        var findings = new StringBuilder()
            .Append(record.Status).Append(": ").AppendLine(record.Summary)
            .AppendLine(record.MinimumGrade is null
                ? record.UnlinkedCommitsFailCodeQuality
                    ? "Minimum grade: none set. Commits made from this card's sessions are not linked to it, so this step failed. Link them with link_board_commit, then retry the step."
                : record.NoCodeFailsCodeQuality
                    ? "Minimum grade: none set. No code is attached to this card, so this step failed. Link the card's commits with link_board_commit, then retry the step."
                    : "Minimum grade: none set. Findings are feedback only and do not fail the step."
                : $"Minimum grade {record.MinimumGrade}: {record.MinimumGradeResult switch
                {
                    BoardCheckRecord.GradeMet => "met",
                    BoardCheckRecord.GradeNotMet => "not met, so this step failed",
                    BoardCheckRecord.GradeNotChecked => "not checked, so this step failed",
                    _ => "not evaluated"
                }}. {record.GradeSummary}".TrimEnd());
        var files = CodeQualityFindings.Read(record.ResultJson);
        if (files.Count > 0) AppendCheckedFiles(findings, files, Bar(record));
        else if (WorstFiles(record.ResultJson) is { Length: > 0 } worst)
            findings.AppendLine("Worst files (concern score/100, rating, top categories):").AppendLine(worst);
        if (record.UnlinkedCommits is { Count: > 0 } unlinked)
        {
            findings.AppendLine($"Unlinked commits ({unlinked.Count}): made from this card's sessions but not linked to it, so not reviewed.");
            foreach (var commit in unlinked.Take(50)) findings.Append("- ").AppendLine(commit);
            if (unlinked.Count > 50) findings.AppendLine($"- and {unlinked.Count - 50} more");
        }
        if (record.NotReviewed is { Count: > 0 } skipped)
        {
            var total = skipped.Count + record.NotReviewedOmitted;
            findings.AppendLine($"Not checked ({total} {(total == 1 ? "file" : "files")} in scope):");
            foreach (var file in skipped) findings.Append("- ").Append(file.Path).Append(": ").AppendLine(file.Reason);
            if (record.NotReviewedOmitted > 0)
                findings.AppendLine($"- and {record.NotReviewedOmitted} more; read_board_check checkId={record.Id} file=\"*\" lists the whole scope.");
        }
        findings.Append($"Per-file metrics and captured inputs: read_board_check checkId={record.Id}.");
        var scope = record.Scope switch
        {
            "card" => "the card's linked commits",
            "commit" => $"commit {record.HeadCommit}",
            "range" => $"{record.BaseCommit}..{record.HeadCommit}",
            _ => record.Scope
        };
        var reported = record.EndedUtc ?? DateTime.UtcNow;
        return new(CheckReviewPrefix + record.Id, record.CardId, "VibeRails", "Code quality check", record.StartedUtc,
            ProcessStatus: record.FailsAction ? "Failed" : "Succeeded", Error: record.FailureReason,
            Workspace: record.WorkspacePath, Scope: record.Scope,
            ScopeDescription: $"Code quality check of {scope}{(record.MinimumGrade is null ? "" : $", minimum grade {record.MinimumGrade}")}; evidence read_board_check checkId={record.Id}",
            BaseCommit: record.BaseCommit, HeadCommit: record.HeadCommit, CapturedUtc: record.StartedUtc, Result: result,
            Findings: Bound(findings.ToString(), 20_000),
            Validation: Bound($"Deterministic Code quality analysis (MintLint {record.ToolVersion}): {record.AnalyzedCount} files analyzed, {record.SkippedCount} skipped, {record.FindingCount} graded D or F. No code was run and no reviewer read the change.", 6_000),
            Limitations: Bound(record.Limitations.Count == 0 ? "None recorded." : string.Join("\n", record.Limitations), 6_000),
            ReportedUtc: reported, ScopeFiles: record.ScopeFiles?.Take(1000).ToList());
    }

    // The agent-facing report names every file to fix and its problems with their fixes; the comment,
    // which people read first, only the worst few. Both bound what one huge change can put into a record.
    private const int ReportFiles = 20, ReportProblems = 10, ReportOtherFiles = 50;
    private const int ReceiptFiles = 3, ReceiptProblems = 3;

    /// <summary>The grade a file must reach: the check's minimum, or C when findings are feedback only
    /// (below C a file is rated Needs work or At risk).</summary>
    private static char? Bar(BoardCheckRecord record) => record.MinimumGrade is { Length: 1 } grade ? grade[0] : null;

    /// <summary>The report's file section: each file below the bar with its grade and its problems, where they
    /// are and how to fix them, in the order that raises the overall grade fastest; then the other files
    /// the check read. A step that fails on its grade therefore always names something to change.</summary>
    private static void AppendCheckedFiles(StringBuilder findings, IReadOnlyList<QualityFile> files, char? bar)
    {
        var grade = bar ?? 'C';
        var toFix = CodeQualityFixList.ToFix(files, bar);
        findings.AppendLine(toFix.Count == 0
            ? $"No file grades below {grade} ({Plural(files.Count, "file")} checked)."
            : $"Files below {grade} ({toFix.Count} of {files.Count} checked), in the order to fix them:");
        foreach (var line in CodeQualityFixList.FixLines(files, bar, ReportFiles, ReportProblems)) findings.AppendLine(line);
        var others = files.Where(file => !CodeQualityFixList.Below(file, bar)).ToList();
        if (others.Count == 0) return;
        findings.AppendLine(toFix.Count == 0 ? "Files checked:" : "Other files checked:");
        foreach (var file in others.Take(ReportOtherFiles))
            findings.Append("- ").Append(file.Path).Append(" · ").Append(file.GradeText)
                .Append(" · ").AppendLine(file.Problems.Count == 0 ? "no measurement past a limit" : ProblemCount(file));
        if (others.Count > ReportOtherFiles) findings.Append("- and ").Append(others.Count - ReportOtherFiles).AppendLine(" more");
    }

    private static string ProblemCount(QualityFile file) => file.Problems.Count == 0
        ? "no single measurement past a limit"
        : file.CriticalCount == 0 ? Plural(file.Problems.Count, "problem")
        : file.CriticalCount == file.Problems.Count ? $"{file.Problems.Count} critical {(file.Problems.Count == 1 ? "problem" : "problems")}"
        : $"{Plural(file.Problems.Count, "problem")}, {file.CriticalCount} critical";

    /// <summary>
    /// The comment people read on the card (VIBE-338, VIBE-362): the outcome with a button that opens the
    /// full report, what to do first, then the files that need work with their worst problems, and one
    /// line on what was not checked. The report link is <c>[label](check:&lt;id&gt;)</c>: the card editor
    /// renders it as a button, and its id is what <c>read_board_check</c> takes.
    /// </summary>
    internal static string CheckReceipt(BoardCheckRecord record)
    {
        var text = new StringBuilder("**").Append(record.Status).Append("**")
            .Append(record.FailsAction ? " · this step failed" : record.MinimumGrade is null ? " · feedback only" : " · step passed")
            .Append(" · [Open the full report](check:").Append(record.Id).Append(')')
            .Append("\n\n");
        if (record.FailureReason is { } reason) text.Append(reason).Append("\n\n");
        else if (record.MinimumGradeResult == BoardCheckRecord.GradeMet) text.Append("Minimum grade ").Append(record.MinimumGrade).Append(" met.\n\n");
        if (record.Status is not ("Failed to run" or "Cancelled") && record.FileCount > 0)
            AppendReceiptFiles(text, record, CodeQualityFindings.Read(record.ResultJson));
        text.Append(NotCheckedSentence(record)).Append("Scope: ").Append(record.Scope switch
        {
            "card" => "the card's linked commits",
            "commit" => $"commit {record.HeadCommit?[..Math.Min(8, record.HeadCommit.Length)]}",
            "range" => "a pinned commit range",
            "working-tree" => "working-tree changes",
            "unpushed" => "unpushed commits",
            "repository" => "the whole repository",
            _ => record.Scope
        }).Append('.');
        return text.ToString();
    }

    private static void AppendReceiptFiles(StringBuilder text, BoardCheckRecord record, IReadOnlyList<QualityFile> files)
    {
        if (record.AnalyzedCount == 0) { text.Append("No supported added code to check.\n\n"); return; }
        var grade = Bar(record) ?? 'C';
        var toFix = CodeQualityFixList.ToFix(files, Bar(record));
        text.Append(ReceiptHeading(record, files.Count > 0, toFix.Count, grade)).Append(" (").Append(record.AnalyzedCount).Append(" checked)");
        if (toFix.Count == 0) { text.Append(files.Count > 0 ? ".\n\n" : "\n\n"); return; }
        // The editor clamps a long comment, so the first file follows its heading without a blank line.
        text.Append('\n').AppendJoin('\n', toFix.Take(ReceiptFiles).Select(ReceiptFile));
        var more = toFix.Count - ReceiptFiles;
        if (more > 0)
            text.Append('\n').Append(more == 1 ? "1 more file grades" : $"{more} more files grade")
                .Append(" below ").Append(grade).Append("; the full report lists them with how to fix each.\n");
        text.Append('\n');
    }

    /// <summary>"**2 files grade below C**", "No file grades below C", or the saved count for an older
    /// record without the engine's report (the full report still opens).</summary>
    private static string ReceiptHeading(BoardCheckRecord record, bool hasReport, int below, char grade)
    {
        if (!hasReport)
            return record.FindingCount switch { 0 => "No file needs work", 1 => "**1 file needs work**", var count => $"**{count} files need work**" };
        return below switch { 0 => $"No file grades below {grade}", 1 => $"**1 file grades below {grade}**", _ => $"**{below} files grade below {grade}**" };
    }

    /// <summary>One file in the comment: its path, grade and health, then its worst problems.</summary>
    private static string ReceiptFile(QualityFile file)
    {
        var text = new StringBuilder().Append('`').Append(file.Path.Replace('`', '\'')).Append("` · **").Append(file.Grade)
            .Append("** · health ").Append(file.HealthText).Append("/100\n");
        if (file.Problems.Count == 0) text.Append("- No measurement is past a limit; several are close to theirs.\n");
        foreach (var problem in file.Problems.Take(ReceiptProblems))
            text.Append("- ").Append(CodeQualityFixList.Describe(problem, markdown: true)).Append('\n');
        if (file.Problems.Count > ReceiptProblems)
            text.Append("- and ").Append(Plural(file.Problems.Count - ReceiptProblems, "more problem")).Append('\n');
        return text.ToString();
    }

    /// <summary>"Not checked: 4 files (2 hidden by your ignore list, 2 not a supported language). ", or empty.</summary>
    private static string NotCheckedSentence(BoardCheckRecord record)
    {
        if (record.Status is "Failed to run" or "Cancelled" || record.FileCount == 0) return "";
        var named = record.NotReviewed ?? [];
        var total = named.Count + record.NotReviewedOmitted;
        // Records from before VIBE-338 counted skipped files without naming them.
        if (total == 0) return record.NotReviewed is null && record.SkippedCount > 0 ? $"Not checked: {Plural(record.SkippedCount, "file")}. " : "";
        var groups = named.GroupBy(file => NotCheckedGroup(file.Reason))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Count()} {group.Key}").ToList();
        if (record.NotReviewedOmitted > 0) groups.Add($"{record.NotReviewedOmitted} more");
        return $"Not checked: {Plural(total, "file")} ({string.Join(", ", groups)}). ";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string WorstFiles(string? resultJson)
    {
        if (resultJson is null) return "";
        try
        {
            var result = JsonSerializer.Deserialize(resultJson, AppJsonSerializerContext.Default.GitPreflightStepResult);
            return result?.Details?.GetValueOrDefault("worstFiles")?.Trim() ?? "";
        }
        catch (JsonException) { return ""; }
    }

    private static string Bound(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

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
            return current ? "Captured inputs match; local ignore settings or exemptions may have changed" : "Stale: inputs or tool version changed";
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
            quality ? "Code quality is advisory. Only added code in supported languages is analyzed, and a function the change created or made worse is graded whole; reach is lexical, not compiler coverage. Local ignore settings apply."
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

    /// <summary>
    /// Records the minimum-grade decision. The grade is <see cref="CodeQualityRule"/>'s, the same code Git
    /// Guard's <c>Code quality minimum</c> rule runs, so the same added code fails both at the same grade.
    /// Below the minimum the outcome is Findings even when no single file reached NeedsWork. Changed source
    /// the capture could not read (the gate's metadata lists it) is not checked: the step fails, and the
    /// outcome records incomplete coverage rather than a finding.
    /// </summary>
    internal static BoardCheckRecord ApplyMinimumGrade(BoardCheckRecord record, char minimum,
        RuleValidationResult gate, int hiddenByIgnores)
    {
        var message = gate.Message ?? (gate.IsValid ? "The minimum grade is met." : "The minimum grade is not met.");
        var limitations = record.Limitations
            .Select(line => line.StartsWith("Code quality is advisory.", StringComparison.Ordinal)
                ? $"Below minimum grade {minimum} this step fails. The grade is the overall Code quality grade of all added code in scope, weighted by added lines, as in Git Guard's “Code quality minimum {minimum}” rule; explicit file exemptions apply. Only added code in supported languages is analyzed, and a function the change created or made worse is graded whole; reach is lexical, not compiler coverage."
                : line).ToList();
        if (hiddenByIgnores > 0)
            limitations.Add($"{hiddenByIgnores} files hidden from the report by local ignore settings still count toward the minimum grade. Use exempt_code_quality_file to exempt a file.");
        var summary = record.Summary.EndsWith(" Advisory analysis.", StringComparison.Ordinal)
            ? record.Summary[..^" Advisory analysis.".Length] : record.Summary;
        if (gate is { IsValid: false, Metadata: IReadOnlyList<string> })
        {
            limitations.Add($"Minimum grade {minimum} not checked. {message}");
            return record with { MinimumGradeResult = BoardCheckRecord.GradeNotChecked, GradeSummary = message, Limitations = limitations,
                Summary = $"{summary} Minimum grade {minimum} not checked: changed source could not be read. This step fails.",
                Status = record.Status is "Passed" or "Skipped/not applicable" ? "Completed with limitations" : record.Status };
        }
        summary = gate.IsValid ? $"{summary} Minimum grade {minimum} met." : $"{summary} Minimum grade {minimum} not met: {message} This step fails.";
        return record with { MinimumGradeResult = gate.IsValid ? BoardCheckRecord.GradeMet : BoardCheckRecord.GradeNotMet,
            GradeSummary = message, Summary = summary, Limitations = limitations,
            Status = gate.IsValid || record.Status is "Failed to run" or "Cancelled" ? record.Status : "Findings" };
    }

    // The reason is also the step's error text, so it names the worst few; exempting or fixing them shows the rest.
    private const int HiddenFiles = 5, HiddenProblems = 3;

    /// <summary>
    /// The minimum grade counts files that local ignore settings hide from the report. When it is not
    /// met, the reason goes on to name the hidden files that are below it, each with its problems, where
    /// they are and how to fix them. Without this a step could fail on a file that nothing shows: the
    /// report lists none of its problems (VIBE-274 review).
    /// </summary>
    internal static BoardCheckRecord WithHiddenFilesToFix(BoardCheckRecord record, IReadOnlyList<QualityFile> graded,
        IReadOnlyList<CodeAnalyzerIgnoredFile>? ignoreRules)
    {
        if (record.MinimumGradeResult != BoardCheckRecord.GradeNotMet || ignoreRules is null) return record;
        var hidden = graded.Where(file => CodeAnalyzerIgnoreRules.IsIgnored(file.Path, ignoreRules)).ToList();
        var lines = CodeQualityFixList.FixLines(hidden, Bar(record), HiddenFiles, HiddenProblems);
        if (lines.Count == 0) return record;
        return record with
        {
            GradeSummary = $"{record.GradeSummary}\nThese files count toward the minimum grade but are hidden from this report by local ignore settings. "
                + $"Fix them, or exempt a file with exempt_code_quality_file:\n{string.Join('\n', lines)}"
        };
    }

    /// <summary>Supported files the report leaves out only because of local ignore settings.</summary>
    private static int HiddenByIgnores(GitStagedSnapshot snapshot, IReadOnlyList<CodeAnalyzerIgnoredFile> ignoreRules) =>
        snapshot.Files.Count(file => file.ExistsInIndex && !file.IsBinary && MintLintAnalyzer.SupportsFile(file.RelativePath)
            && CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, ignoreRules)
            && !CodeAnalyzerIgnoreRules.IsIgnored(file.RelativePath, snapshot.QualityExemptions ?? []));

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
