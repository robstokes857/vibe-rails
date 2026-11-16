using System.Diagnostics;
using System.Text;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.VCA.Hooks;
using VibeRails.Utils;

namespace VibeRails.Services.VCA;

public interface IStopBypassVerifier
{
    Task<CommitSignatureVerification> VerifyAsync(StopBypassApproval approval, CancellationToken ct);
}

public sealed class StopBypassVerifier : IStopBypassVerifier
{
    public Task<CommitSignatureVerification> VerifyAsync(StopBypassApproval approval, CancellationToken ct) =>
        ViberailsCommitSignatureVerifier.Shared.VerifyPayloadAsync(approval.Signature, StopBypassFormat.BuildPayload(approval.Scope), ct);
}

/// <summary>Only cryptographically verified approvals reach the rule evaluator.</summary>
public sealed record VerifiedStopBypass(StopBypassScope Scope, string SignerEmail)
{
    public bool Covers(string source, string rule, IReadOnlyList<string>? approvers) =>
        Scope.Rules.Any(item => item.RuleText == rule && string.Equals(item.SourcePath, source,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        && (approvers is not { Count: > 0 } || approvers.Contains(SignerEmail, StringComparer.OrdinalIgnoreCase));
}

public sealed record StopBypassResolution(VerifiedStopBypass? Verified = null, string? Error = null);

public interface IStopBypassPromptLauncher
{
    string? Launch(string repository, string requestId);
}

/// <summary>Starts only the separate human popup; never supplies a password or signs in the MCP process.</summary>
public sealed class StopBypassPromptLauncher : IStopBypassPromptLauncher
{
    public string? Launch(string repository, string requestId)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive || VcaHookConsoleRespawn.AutomatedEnvironmentVariable() is not null)
            return "The signing popup requires an interactive Windows desktop and is unavailable in CI.";
        try
        {
            var start = BuildStartInfo(repository, requestId);
            using var process = Process.Start(start);
            return process is null ? "The signing popup could not start." : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { return "The signing popup could not start. Check the VibeRails installation."; }
    }

    internal static ProcessStartInfo BuildStartInfo(string repository, string requestId)
    {
        var executable = InstalledVibeRails.ExecutableForCallbacks(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The VibeRails executable is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in VcaHookProcessLaunch.WithManagedEntry(executable,
            ["--vca-hook", "sign-stop-bypass", "--bypass-request", requestId, "--workdir", repository, "--console-window"]))
            start.ArgumentList.Add(arg);
        return start;
    }
}

/// <summary>Captures requests, verifies approvals and carries the signed reason into a real commit.</summary>
public sealed class StopBypassService(IGitStagedSnapshotProvider snapshots,
    IStopBypassPromptLauncher? launcher = null, IStopBypassVerifier? verifier = null)
{
    private readonly StopBypassStore store = new();
    private readonly IStopBypassVerifier signatures = verifier ?? new StopBypassVerifier();

    public async Task<string> RequestAsync(string repository, string reason, string? commitMessage, CancellationToken ct)
    {
        var (request, repositoryPath) = await CreateAsync(repository, reason, commitMessage ?? "", ct);
        if (request is null) return "No STOP violations need a bypass. Supply the intended commit message to evaluate message rules.";
        var rules = request.Scope.Rules;
        var error = (launcher ?? new StopBypassPromptLauncher()).Launch(repositoryPath, request.Id);
        if (error is not null)
        {
            await store.SaveAsync(repositoryPath, request with { Status = "failed", Error = error }, request.Id, ct);
            return "STOP bypass not approved. " + error;
        }
        return $"STOP bypass request {request.Id} is pending human approval for {rules.Length} rule(s). "
            + "The signing popup shows the exact staged changes and reason. The person chooses a key and enters its password there; "
            + "never request or transmit the password through MCP. Poll get_stop_bypass_status. After approval, retry the commit without restaging; "
            + "the hook adds the signed bypass and its reason to the message. Any uncovered STOP rule still blocks.";
    }

    /// <summary>
    /// Captures the staged snapshot's STOP violations and saves them as a pending request, replacing
    /// any earlier one. Opens nothing and approves nothing. A null <paramref name="commitMessage"/>
    /// leaves message rules out (pre-commit, before the message exists): they are judged again on
    /// the final message, where an uncovered STOP still blocks. Returns a null request when no STOP
    /// rule is violated.
    /// </summary>
    internal async Task<(StopBypassRequest? Request, string RepositoryPath)> CreateAsync(
        string repository, string reason, string? commitMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Explain why the STOP rules must be bypassed.");
        var snapshot = await snapshots.CaptureAsync(repository, ct);
        var identity = snapshot.StagedIdentity ?? throw new ArgumentException("The staged snapshot is unavailable.");
        // Pure evaluation only: this must never enqueue remediation or itself approve anything.
        var report = await RulesTool.ValidateVcaReportAsync(snapshots, snapshot.RepositoryPath,
            commitMessage, validateCommitMessage: commitMessage is not null, cancellationToken: ct, stagedSnapshot: snapshot);
        if (report.HasError) throw new ArgumentException("VCA could not evaluate the staged snapshot. Fix the validation error before requesting a bypass.");
        var rules = (report.Violations ?? []).Where(rule => rule.Enforcement == "STOP")
            .Select(rule => new StopBypassRule(rule.SourcePath, rule.Rule)).Distinct()
            .OrderBy(rule => rule.SourcePath, StringComparer.Ordinal).ThenBy(rule => rule.RuleText, StringComparer.Ordinal).ToArray();
        if (rules.Length == 0) return (null, snapshot.RepositoryPath);
        var scope = new StopBypassScope(StopBypassFormat.RepositoryId(snapshot.RepositoryPath), identity.BaseCommit,
            identity.BaseTree, identity.StagedTree, reason.Trim(), rules);
        StopBypassFormat.Validate(scope);
        var request = new StopBypassRequest(Guid.NewGuid().ToString("N"), scope, DateTimeOffset.UtcNow);
        await store.SaveAsync(snapshot.RepositoryPath, request, null, ct);
        return (request, snapshot.RepositoryPath);
    }

    public async Task<string> StatusAsync(string repository, CancellationToken ct)
    {
        var snapshot = await snapshots.CaptureAsync(repository, ct);
        var request = await store.ReadAsync(snapshot.RepositoryPath, ct);
        if (request is null) return "No STOP bypass has been requested for this worktree.";
        if (!StopBypassFormat.Matches(request.Scope, snapshot)) return "The staged snapshot or base changed. Request a new STOP bypass.";
        if (request.Status == "approved" && request.Approval is { } approval)
        {
            var result = await VerifyAsync(approval, snapshot, ct);
            return result.Error is not null ? "STOP bypass is not valid: " + result.Error
                : $"STOP bypass approved by {result.Verified!.SignerEmail}. Reason: {approval.Scope.Reason}. Retry the commit without restaging.";
        }
        return $"STOP bypass {request.Id}: {request.Status}. {request.Error}".TrimEnd();
    }

    public async Task<StopBypassResolution> ResolveAsync(GitStagedSnapshot snapshot, string? messagePath, string? message, CancellationToken ct)
    {
        StopBypassApproval? approval;
        var inMessage = message is not null && message.Split('\n').Any(StopBypassFormat.IsTrailer);
        if (inMessage)
        {
            approval = StopBypassFormat.Parse(message!);
            if (approval is null) return new(Error: "The STOP bypass signature, rule list or required reason is malformed or was changed.");
            if (!StopBypassFormat.Matches(approval.Scope, snapshot))
            {
                // Amend carries the previous commit's audit trailers. Replace those only when
                // they are genuine and a freshly approved exception covers the current snapshot.
                var prior = await signatures.VerifyAsync(approval, ct);
                var current = await store.ReadAsync(snapshot.RepositoryPath, ct);
                if (prior.Status == CommitSignatureStatus.Valid && current is { Status: "approved", Approval: { } replacement }
                    && StopBypassFormat.Matches(replacement.Scope, snapshot))
                {
                    var verified = await VerifyAsync(replacement, snapshot, ct);
                    if (verified.Verified is not null && messagePath is not null)
                        await AppendAsync(messagePath, replacement, ct, replaceExisting: true);
                    return verified;
                }
            }
        }
        else
        {
            var request = await store.ReadAsync(snapshot.RepositoryPath, ct);
            approval = request is { Status: "approved" } ? request.Approval : null;
            if (approval is null || !StopBypassFormat.Matches(approval.Scope, snapshot)) return new();
        }
        var result = await VerifyAsync(approval, snapshot, ct);
        if (result.Verified is not null && messagePath is not null && !inMessage)
            await AppendAsync(messagePath, approval, ct);
        return result;
    }

    private async Task<StopBypassResolution> VerifyAsync(StopBypassApproval approval, GitStagedSnapshot snapshot, CancellationToken ct)
    {
        StopBypassFormat.Validate(approval.Scope);
        if (approval.Signature is null
            || !CommitSignatureFormat.TryParseTrailer(CommitSignatureFormat.Format(approval.Signature), out _))
            return new(Error: "The STOP bypass signature is malformed.");
        if (!StopBypassFormat.Matches(approval.Scope, snapshot) || approval.Signature.TreeId != approval.Scope.StagedTree)
            return new(Error: "The STOP bypass covers different staged changes or a different base. Request approval again.");
        var checkedSignature = await signatures.VerifyAsync(approval, ct);
        if (checkedSignature.Status != CommitSignatureStatus.Valid || checkedSignature.SignerEmail is not { Length: > 0 } email)
            return new(Error: checkedSignature.Detail);
        foreach (var rule in approval.Scope.Rules)
            if (HumanSignatureRule.TryParseSignedApproval(rule.RuleText, out var required)
                && !required.Approvers.Contains(email, StringComparer.OrdinalIgnoreCase))
                return new(Error: "The STOP rule requires approval by " + HumanSignatureRule.DescribeApprovers(required.Approvers) + ".");
        return new(new(approval.Scope, email));
    }

    internal static async Task AppendAsync(string messagePath, StopBypassApproval approval, CancellationToken ct, bool replaceExisting = false)
    {
        var text = await File.ReadAllTextAsync(messagePath, ct);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n');
        var scissors = StopBypassFormat.FindScissorsLine(lines);
        var head = scissors < 0 ? text : string.Join("\n", lines.Take(scissors));
        var tail = scissors < 0 ? "" : string.Join("\n", lines.Skip(scissors));
        if (replaceExisting) head = string.Join("\n", head.Split('\n').Where(line => !StopBypassFormat.IsTrailer(line)));
        var updated = head.TrimEnd('\r', '\n') + newline + newline
            + StopBypassFormat.Format(approval).Replace("\n", newline) + newline + (tail.Length == 0 ? "" : newline + tail);
        await File.WriteAllTextAsync(messagePath, updated, new UTF8Encoding(false), ct);
    }
}
