namespace VibeRails.Services.VCA.Hooks;

/// <summary>Asks a person to sign the commit. True means a verified signature was added to the message.</summary>
public interface IHumanSignaturePrompt
{
    Task<bool> RequestAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        IReadOnlyList<VcaRuleFinding> pendingSignatures,
        CancellationToken cancellationToken);
}

/// <summary>
/// Shows the signing UI in a Git Guard popup window, and nowhere else.
/// <list type="bullet">
/// <item>If this hook already runs in the popup console (GUI and redirected commits on Windows), the
/// signing UI runs right there, below the transcript that explains why it is needed.</item>
/// <item>Otherwise, on Windows, it opens a new popup console with <c>--vca-hook sign-commit-msg</c>
/// and waits for it, the same CREATE_NEW_CONSOLE route the Git Guard popup itself uses. A terminal
/// commit therefore never reads the password from the terminal, which may be an agent's pty.</item>
/// <item>No popup can open in CI or without a desktop, and none exists off Windows yet, so the
/// commit is blocked with the rule's guidance instead.</item>
/// </list>
/// </summary>
public sealed class HumanSignaturePromptLauncher(TextWriter output, TextWriter error, VcaConsoleStyle? style = null)
    : IHumanSignaturePrompt
{
    public async Task<bool> RequestAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        IReadOnlyList<VcaRuleFinding> pendingSignatures,
        CancellationToken cancellationToken)
    {
        var signing = BuildArguments(pendingSignatures);
        if (signing is null || string.IsNullOrWhiteSpace(invocation.CommitMessagePath))
            return false;

        if (invocation.ConsoleWindowAttached && VcaHookProcessLaunch.CanPromptInCurrentConsole())
        {
            await output.WriteLineAsync();
            return await HumanSignaturePopup.RunInConsoleAsync(
                invocation with { Kind = VcaHookKind.SignCommitMessage, Signing = signing },
                output,
                style ?? VcaConsoleStyle.Plain,
                cancellationToken) == 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            await error.WriteLineAsync(
                "VibeRails VCA: the Git Guard signing popup is available on Windows only in this version, so this "
                + "commit cannot be signed here.");
            return false;
        }

        var exitCode = await VcaHookConsoleRespawn.TryRespawnAsync(
            BuildPopupArguments(invocation.CommitMessagePath, workingDirectory, signing),
            output,
            error,
            cancellationToken);
        if (exitCode is null)
        {
            await error.WriteLineAsync(
                "VibeRails VCA: the Git Guard signing popup could not open (CI, no interactive desktop, or no "
                + "console). A person has to make this commit from their own session.");
            return false;
        }

        return exitCode == 0;
    }

    internal static HumanSignaturePopupArguments? BuildArguments(IReadOnlyList<VcaRuleFinding> pendingSignatures)
    {
        var requests = pendingSignatures
            .Where(finding => finding.SignatureRequest is not null)
            .ToList();
        if (requests.Count == 0) return null;

        var first = requests[0].SignatureRequest!;
        var approvers = requests
            .SelectMany(finding => finding.SignatureRequest!.Approvers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requirements = requests
            .Select(finding => finding.SignatureRequest!.Approvers.Count == 0
                ? $"{finding.Rule} (any registered signing key)"
                : $"{finding.Rule} ({HumanSignatureRule.DescribeApprovers(finding.SignatureRequest.Approvers)})")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new HumanSignaturePopupArguments(first.TreeId, first.BaseTree, approvers, requirements);
    }

    internal static IReadOnlyList<string> BuildPopupArguments(
        string commitMessagePath,
        string workingDirectory,
        HumanSignaturePopupArguments signing)
    {
        var arguments = new List<string>
        {
            "--vca-hook", "sign-commit-msg",
            "--commit-message", commitMessagePath,
            "--workdir", workingDirectory,
            "--tree", signing.TreeId ?? string.Empty
        };
        if (!string.IsNullOrWhiteSpace(signing.BaseTree))
            arguments.AddRange(["--base-tree", signing.BaseTree]);
        foreach (var approver in signing.Approvers)
            arguments.AddRange(["--approver", approver]);
        foreach (var requirement in signing.Requirements)
            arguments.AddRange(["--require", requirement]);
        // TryRespawnAsync swaps this for --console-window-attached in the child it creates.
        arguments.Add("--console-window");
        return arguments;
    }
}
