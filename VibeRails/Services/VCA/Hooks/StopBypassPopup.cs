using VibeRails.DTOs;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.SigningKeys;
using VibeRails.Utils;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>Human-only approval of a captured STOP exception, using the existing signing-key prompt.</summary>
public sealed class StopBypassPopup(TextWriter output, IHumanSignatureConsole console,
    Func<StopBypassScope, ICommitTreeSigner> signer, IStopBypassVerifier verifier,
    IGitStagedSnapshotProvider snapshots)
{
    public async Task<int> RunAsync(string repository, string requestId, CancellationToken ct)
    {
        var store = new StopBypassStore();
        var request = await store.ReadAsync(repository, ct);
        if (request is null || request.Id != requestId || request.Status != "pending")
        {
            await output.WriteLineAsync("This STOP bypass request is no longer pending. Nothing was signed.");
            return 1;
        }
        var snapshot = await snapshots.CaptureAsync(repository, ct);
        if (!StopBypassFormat.Matches(request.Scope, snapshot))
        {
            await store.SaveAsync(repository, request with { Status = "stale", Error = "The staged snapshot changed." }, request.Id, ct);
            await output.WriteLineAsync("The staged snapshot changed. Request a new STOP bypass. Nothing was signed.");
            return 1;
        }
        StopBypassFormat.Validate(request.Scope);
        await output.WriteLineAsync("Git Guard: approve a STOP bypass");
        await output.WriteLineAsync("You are approving an exception, not confirming that these rules passed.");
        await output.WriteLineAsync("Repository: " + HumanSignaturePopup.Clean(snapshot.RepositoryPath));
        await output.WriteLineAsync("Staged tree: " + request.Scope.StagedTree);
        await output.WriteLineAsync("Files in this staged change:");
        foreach (var file in snapshot.Files)
            await output.WriteLineAsync("  " + HumanSignaturePopup.Clean(file.RelativePath)
                + (file.PreviousRelativePath is { } previous ? " (from " + HumanSignaturePopup.Clean(previous) + ")" : ""));
        await output.WriteLineAsync("STOP rules being bypassed:");
        foreach (var rule in request.Scope.Rules)
            await output.WriteLineAsync("  " + HumanSignaturePopup.Clean(rule.SourcePath) + ": " + HumanSignaturePopup.Clean(rule.RuleText));
        await output.WriteLineAsync("Reason to record in the commit: " + HumanSignaturePopup.Clean(request.Scope.Reason));
        await output.WriteLineAsync("Your signature covers this snapshot, these rules and this exact reason. It cannot approve other changes.");
        var answer = await console.ReadLineAsync("Type APPROVE to sign this STOP bypass (anything else cancels): ", ct);
        if (!string.Equals(answer?.Trim(), "APPROVE", StringComparison.Ordinal))
            return await FinishAsync("cancelled", "The person declined the STOP bypass.");

        var prompt = new SigningKeyPrompt(output, console, signer(request.Scope), VcaConsoleStyle.Plain,
            "the approval request stays open here");
        var keys = await prompt.WaitForSyncedKeysAsync(ct);
        var key = keys is null ? null : await prompt.ChooseKeyAsync(keys, ct);
        if (key is null) return await FinishAsync("cancelled", "No signing key was selected.");
        var signature = await prompt.SignWithPasswordAsync(key, request.Scope.StagedTree, ct);
        if (signature is null) return await FinishAsync("cancelled", "No signature was created.");
        var approval = new StopBypassApproval(request.Scope, signature);
        var verified = await verifier.VerifyAsync(approval, ct);
        if (verified.Status != CommitSignatureStatus.Valid || string.IsNullOrWhiteSpace(verified.SignerEmail))
            return await FinishAsync("failed", "The signature could not be verified: " + verified.Detail);
        foreach (var rule in request.Scope.Rules)
            if (HumanSignatureRule.TryParseSignedApproval(rule.RuleText, out var required)
                && !required.Approvers.Contains(verified.SignerEmail, StringComparer.OrdinalIgnoreCase))
                return await FinishAsync("failed", "This rule requires approval by " + HumanSignatureRule.DescribeApprovers(required.Approvers) + ".");
        if (!StopBypassFormat.Matches(request.Scope, await snapshots.CaptureAsync(repository, ct)))
            return await FinishAsync("stale", "The staged snapshot changed while the prompt was open. Request a new approval.");
        await store.SaveAsync(repository, request with { Status = "approved", Approval = approval }, request.Id, ct);
        await output.WriteLineAsync("STOP bypass approved. Git Guard records the signed exception and its reason in the commit message. "
            + "A commit waiting on this window continues; otherwise retry the commit without restaging.");
        return 0;

        async Task<int> FinishAsync(string status, string message)
        {
            await store.SaveAsync(repository, request with { Status = status, Error = message }, request.Id, ct);
            await output.WriteLineAsync(message);
            return 1;
        }
    }

    internal static async Task<int> RunInConsoleAsync(VcaHookInvocation invocation, TextWriter output, CancellationToken ct)
    {
        if (!invocation.ConsoleWindowAttached || !VcaHookProcessLaunch.CanPromptInCurrentConsole()
            || VcaHookConsoleRespawn.AutomatedEnvironmentVariable() is not null || invocation.BypassRequest is null)
        {
            await output.WriteLineAsync("STOP bypass signing requires the separate human popup. Redirected input and agent terminals cannot sign.");
            return 1;
        }
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var keys = new SigningKeyService(http, new SigningKeyStore(), () => Config.LoadFresh().ApiKey);
        var popup = new StopBypassPopup(output, new ConsoleHumanSignatureConsole(output, HumanSignaturePopup.IdleTimeout),
            scope => new ScopeSigner(keys, scope), new StopBypassVerifier(), new GitStagedSnapshotProvider());
        return await popup.RunAsync(invocation.WorkingDirectory ?? Directory.GetCurrentDirectory(), invocation.BypassRequest, ct);
    }

    private sealed class ScopeSigner(SigningKeyService keys, StopBypassScope scope) : ICommitTreeSigner
    {
        public async Task<IReadOnlyList<SigningKeyInfo>> ListKeysAsync(CancellationToken ct) =>
            (await keys.ListAsync(ct)).Keys.Where(key => key.CloudKeyId is not null).ToArray();
        public Task<CommitSignature> SignAsync(Guid keyId, string password, string treeId, CancellationToken ct) =>
            keys.SignStopBypassAsync(keyId, password, scope, ct);
    }
}
