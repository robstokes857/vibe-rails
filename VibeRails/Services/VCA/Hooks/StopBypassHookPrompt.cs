using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Offers the signed STOP exception (VB-46) to a person at the moment STOP blocks their commit
/// (VIBE-331). Before this, only an agent could ask for one, through <c>request_stop_bypass</c>.
/// </summary>
public interface IStopBypassPrompt
{
    /// <summary>
    /// Asks why the change is deliberate and, when a reason is given, has a person approve the
    /// exception with a signing key. True means an approval was recorded for the staged snapshot;
    /// the caller still re-validates, and that check is what lets the commit through.
    /// </summary>
    Task<bool> OfferAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        int blockedRuleCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// True once the person closed the offer (Enter alone, Esc or no answer). Git Guard's window
    /// then closes without a second "press Enter" pause: the offer was that pause.
    /// </summary>
    bool Declined { get; }
}

/// <summary>
/// Where a commit's STOP exception is asked for and approved.
/// <list type="bullet">
/// <item>In Git Guard's own window, the reason and the approval are both asked there, below the
/// transcript that explains the block. This is every Windows commit outside CI: Git gives hooks a
/// null stdin and redirects their stdout (checked 2026-11-16 with Git 2.54 in a real console), so
/// the hook script asks for that window from a terminal as well as from a Git GUI.</item>
/// <item>In any other console a person can type in, only the reason is asked there. The approval
/// opens in a new Git Guard window, so a key password is never read from a terminal that may be an
/// agent's.</item>
/// <item>Anywhere else nothing is asked, and the transcript's guidance stands.</item>
/// </list>
/// The reason is not the gate. The approval is: a person types APPROVE after reading the exact files,
/// rules and reason, then unlocks an Account settings > Keys key, and viberails.ai verifies the signature. An
/// agent that can type in the window can no more approve an exception here than it can through
/// <c>request_stop_bypass</c>, which opens the same approval.
/// </summary>
public sealed class StopBypassHookPrompt : IStopBypassPrompt
{
    /// <summary>
    /// How long the reason prompt waits without a keypress. An agent's commit opens Git Guard's
    /// window too, and nobody may be there to answer, so this replaces the blocked window's pause
    /// and lasts exactly as long. Every keypress restarts the wait.
    /// </summary>
    internal static TimeSpan ReasonIdleTimeout => VcaHookProcessHost.ConsolePauseTimeout;

    private const int TextWidth = 76;
    private const string Indent = "  ";

    /// <summary>Runs the approval for a saved request. Null means no window could open.</summary>
    internal delegate Task<int?> ApprovalWindow(
        VcaHookInvocation invocation,
        string repository,
        string requestId,
        CancellationToken cancellationToken);

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly StopBypassService _service;
    private readonly IHumanSignatureConsole _console;
    private readonly Func<bool> _canPrompt;
    private readonly ApprovalWindow _approve;
    private readonly VcaConsoleStyle _style;

    public StopBypassHookPrompt(
        TextWriter output,
        TextWriter error,
        IGitStagedSnapshotProvider snapshots,
        VcaConsoleStyle? style = null)
        : this(
            output,
            error,
            new StopBypassService(snapshots),
            new ConsoleHumanSignatureConsole(output, ReasonIdleTimeout),
            VcaHookProcessLaunch.CanPromptInCurrentConsole,
            approve: null,
            style)
    {
    }

    internal StopBypassHookPrompt(
        TextWriter output,
        TextWriter error,
        StopBypassService service,
        IHumanSignatureConsole console,
        Func<bool> canPrompt,
        ApprovalWindow? approve,
        VcaConsoleStyle? style = null)
    {
        _output = output;
        _error = error;
        _service = service;
        _console = console;
        _canPrompt = canPrompt;
        _approve = approve ?? OpenApprovalAsync;
        _style = style ?? VcaConsoleStyle.Plain;
    }

    public bool Declined { get; private set; }

    public async Task<bool> OfferAsync(
        VcaHookInvocation invocation,
        string workingDirectory,
        int blockedRuleCount,
        CancellationToken cancellationToken)
    {
        if (!invocation.PromptForAcknowledgment || !_canPrompt())
            return false;

        var rules = blockedRuleCount == 1 ? "A STOP rule blocks" : $"{blockedRuleCount} STOP rules block";
        var where = invocation.ConsoleWindowAttached ? "below" : "in the Git Guard window that opens";
        await _output.WriteLineAsync();
        await WriteWrappedAsync(
            $"{rules} this commit. If the change is deliberate, you can sign an exception: type why, then "
                + $"approve it {where} with an Account settings > Keys signing key. Git Guard records the reason in the "
                + "commit message.",
            _style.Yellow);
        await WriteWrappedAsync(
            $"Enter alone or Esc keeps the commit blocked. This prompt {VcaHookProcessHost.DescribeAutoClose(ReasonIdleTimeout)} "
                + "without typing.",
            _style.Dim);
        var answer = await _console.ReadLineAsync($"{Indent}{_style.Bold}Reason:{_style.Reset} ", cancellationToken);
        var reason = answer?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            Declined = true;
            await WriteWrappedAsync("No exception requested. The commit stays blocked.", _style.Dim);
            return false;
        }

        StopBypassRequest? request;
        string repository;
        try
        {
            // Pre-commit rules only: the message does not exist yet. Message rules are judged on the
            // final message, where an uncovered STOP still blocks.
            (request, repository) = await _service.CreateAsync(workingDirectory, reason, commitMessage: null, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await WriteWrappedAsync("No exception was requested: " + ex.Message, _style.Red);
            return false;
        }

        if (request is null)
        {
            await WriteWrappedAsync(
                "No STOP rule blocks the staged changes any more, so there is nothing to approve. Commit again.",
                _style.Dim);
            return false;
        }

        var exitCode = await _approve(invocation, repository, request.Id, cancellationToken);
        if (exitCode is null)
        {
            await WriteWrappedAsync(
                "The Git Guard approval window could not open (CI, no interactive desktop, or not Windows). "
                    + "Make this commit from your own Windows session to approve an exception.",
                _style.Red);
            return false;
        }

        return exitCode == 0;
    }

    private async Task<int?> OpenApprovalAsync(
        VcaHookInvocation invocation,
        string repository,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (invocation.ConsoleWindowAttached)
        {
            await _output.WriteLineAsync();
            return await StopBypassPopup.RunInConsoleAsync(
                invocation with
                {
                    Kind = VcaHookKind.SignStopBypass,
                    BypassRequest = requestId,
                    WorkingDirectory = repository
                },
                _output,
                cancellationToken);
        }

        if (!OperatingSystem.IsWindows())
            return null;

        return await VcaHookConsoleRespawn.TryRespawnAsync(
            BuildApprovalArguments(repository, requestId),
            _output,
            _error,
            cancellationToken);
    }

    internal static IReadOnlyList<string> BuildApprovalArguments(string repository, string requestId) =>
        [
            "--vca-hook", "sign-stop-bypass",
            "--bypass-request", requestId,
            "--workdir", repository,
            // TryRespawnAsync swaps this for --console-window-attached in the child it creates.
            "--console-window"
        ];

    private async Task WriteWrappedAsync(string text, string color)
    {
        foreach (var line in VcaConsoleHookPresenter.WrapText(text, TextWidth - Indent.Length))
            await _output.WriteLineAsync($"{Indent}{color}{line}{_style.Reset}");
        await _output.FlushAsync();
    }
}
