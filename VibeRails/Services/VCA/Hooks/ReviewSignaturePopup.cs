using System.Globalization;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.SigningKeys;
using VibeRails.Utils;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>What the review approval popup works on: the hosted reference and the reviewer's local copy of the repository.</summary>
public sealed record ReviewSignaturePopupRequest(string Reference, string RepositoryPath);

/// <summary>
/// The review approval popup (VB-50 slice D), the Git Guard signing popup's sibling. A reviewer reads a
/// request on viberails.ai and approves it here: the popup fetches the request with the account key,
/// shows what is being approved (repository, requester, card, note, sessions, every file with its
/// counts and omissions, the tree and base), proves on this machine that the files rebuild the tree
/// (<see cref="ReviewTreeRebuilder"/>; design decision 7), and only then lists the synced keys, reads
/// the password with echo off, signs the tree with the existing staged-tree statement, confirms the
/// signature with viberails.ai and posts the approval there. Exit code 0 means the approval was recorded.
/// </summary>
/// <remarks>
/// Nothing else in VibeRails can produce this approval: the statement is the one the dashboard Sign route
/// and MCP refuse, the password is read only from this window, and the decision is posted by this
/// process. The requester's text (card, note) is untrusted and printed through
/// <see cref="HumanSignaturePopup.Clean"/>, so an escape sequence cannot repaint the screen. The same
/// threat model as the commit popup applies: a process running as the user with the password can sign
/// without this window; the signature proves the key holder approved this tree, not who typed.
/// </remarks>
public sealed class ReviewSignaturePopup
{
    private const int MaxListedFiles = 25;
    private const int MaxListedMissing = 10;
    private const int MaxNoteLines = 12;
    private const int MaxLineChars = 160;

    /// <summary>(reference) → the request as viberails.ai shows it to this reviewer. Throws <see cref="ReviewRequestTransportException"/>.</summary>
    public delegate Task<ReviewHostedBundle> BundleFetcher(string reference, CancellationToken cancellationToken);

    /// <summary>(repository path, request) → whether the request's files rebuild its tree there.</summary>
    public delegate Task<ReviewTreeRebuildResult> TreeRebuilder(string repositoryPath, ReviewHostedBundle bundle, CancellationToken cancellationToken);

    /// <summary>(reference, decision) → the decision as recorded. Throws <see cref="ReviewRequestTransportException"/>.</summary>
    public delegate Task<ReviewRequestHostedDecision> DecisionPoster(string reference, ReviewInboxDecisionBody decision, CancellationToken cancellationToken);

    private readonly TextWriter _output;
    private readonly ICommitSignatureVerifier _verifier;
    private readonly VcaConsoleStyle _style;
    private readonly SigningKeyPrompt _prompt;
    private readonly BundleFetcher _fetch;
    private readonly TreeRebuilder _rebuild;
    private readonly DecisionPoster _post;
    private readonly string? _accountEmail;

    /// <param name="accountEmail">The verified email of the saved account key, when known, so a key from another account is warned about before the host refuses it.</param>
    public ReviewSignaturePopup(
        TextWriter output,
        IHumanSignatureConsole console,
        ICommitTreeSigner signer,
        ICommitSignatureVerifier verifier,
        BundleFetcher fetch,
        TreeRebuilder rebuild,
        DecisionPoster post,
        string? accountEmail,
        VcaConsoleStyle? style = null)
    {
        _output = output;
        _verifier = verifier;
        _style = style ?? VcaConsoleStyle.Plain;
        _prompt = new SigningKeyPrompt(output, console, signer, _style, "the request does not need to be opened again");
        _fetch = fetch;
        _rebuild = rebuild;
        _post = post;
        _accountEmail = accountEmail;
    }

    public async Task<int> RunAsync(ReviewSignaturePopupRequest request, CancellationToken cancellationToken)
    {
        var s = _style;
        await _output.WriteLineAsync($"{s.Bold}{s.Cyan}Review request: your approval{s.Reset}");
        await _output.WriteLineAsync("Someone asked you to review a change before it is committed. Approving signs that change's tree with your key.");
        await _output.WriteLineAsync();

        if (!ReviewRequestTransport.IsReference(request.Reference))
        {
            await _output.WriteLineAsync($"{s.Red}That is not a review request reference. Nothing was signed.{s.Reset}");
            return 1;
        }

        await _output.WriteLineAsync("Fetching the request from viberails.ai...");
        ReviewHostedBundle bundle;
        try
        {
            bundle = await _fetch(request.Reference, cancellationToken);
        }
        catch (ReviewRequestTransportException ex)
        {
            await _output.WriteLineAsync($"{s.Red}{ex.Message} Nothing was signed.{s.Reset}");
            return 1;
        }

        await WriteSummaryAsync(bundle, request.RepositoryPath);

        if (bundle.Status is ReviewRequestStatus.Cancelled or "expired")
        {
            await _output.WriteLineAsync($"{s.Yellow}This request is {bundle.Status} on viberails.ai and no longer takes a decision. Nothing was signed.{s.Reset}");
            return 1;
        }
        if (bundle.Status != ReviewRequestStatus.Open)
        {
            // viberails.ai keeps taking decisions after the first one: a rule may name several approvers,
            // and an approval after another reviewer's rejection is still this reviewer's to give.
            await _output.WriteLineAsync(
                $"{s.Yellow}Another reviewer already {bundle.Status} this request. viberails.ai still records your decision, "
                + $"and the requester's commit needs the approvers its rules name.{s.Reset}");
        }
        if (!bundle.CanDecide)
        {
            await _output.WriteLineAsync(
                $"{s.Yellow}viberails.ai says your account is not a listed reviewer of this request (or its email is not verified), "
                + $"so it would not record your decision. Nothing was signed.{s.Reset}");
            return 1;
        }
        if (bundle.YourDecisions!.FirstOrDefault(decision => decision.Decision == ReviewDecisionKind.Approved) is { } earlier)
        {
            await _output.WriteLineAsync(
                $"{s.Yellow}You already approved this request on {earlier.DecidedUtc:yyyy-MM-dd HH:mm} UTC. Nothing more to sign.{s.Reset}");
            return 1;
        }

        var missing = ReviewTreeRebuilder.FilesWithoutContent(bundle.Files!);
        if (missing.Count > 0)
        {
            await _output.WriteLineAsync(
                $"{s.Red}{missing.Count} file(s) are listed without their content (binary, oversized or over the bundle limit):{s.Reset}");
            foreach (var path in missing.Take(MaxListedMissing))
                await _output.WriteLineAsync("  " + HumanSignaturePopup.Clean(path));
            if (missing.Count > MaxListedMissing)
                await _output.WriteLineAsync($"  ... and {missing.Count - MaxListedMissing} more");
            await _output.WriteLineAsync(
                $"{s.Red}The tree cannot be rebuilt from this request, so it cannot be approved. Nothing was signed. "
                + $"Ask the requester for a request whose files all carry their content.{s.Reset}");
            return 1;
        }

        if (ReviewTreeRebuilder.ShapeProblem(bundle.Files!) is { } inconsistent)
        {
            await _output.WriteLineAsync(
                $"{s.Red}The request describes its files inconsistently: {HumanSignaturePopup.Clean(inconsistent)} "
                + $"A side shown to you would not be a side of the change, so it cannot be approved. Nothing was signed.{s.Reset}");
            return 1;
        }

        await _output.WriteLineAsync($"Rebuilding the tree from your copy of the repository ({request.RepositoryPath})...");
        ReviewTreeRebuildResult rebuilt;
        try
        {
            rebuilt = await _rebuild(request.RepositoryPath, bundle, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _output.WriteLineAsync($"{s.Red}Could not rebuild the tree: {HumanSignaturePopup.Clean(ex.Message)}. Nothing was signed.{s.Reset}");
            return 1;
        }
        // The verdict names request paths, which are the requester's text: cleaned like the summary.
        if (!rebuilt.Matches)
        {
            await _output.WriteLineAsync($"{s.Red}{HumanSignaturePopup.Clean(rebuilt.Message)}{s.Reset}");
            await _output.WriteLineAsync($"{s.Red}Nothing was signed.{s.Reset}");
            return 1;
        }
        await _output.WriteLineAsync($"{s.Green}Verified. {HumanSignaturePopup.Clean(rebuilt.Message)}{s.Reset}");
        await _output.WriteLineAsync();

        var keys = await _prompt.WaitForSyncedKeysAsync(cancellationToken);
        if (keys is null)
            return 1;

        var key = await _prompt.ChooseKeyAsync(keys, cancellationToken);
        if (key is null)
        {
            await _output.WriteLineAsync("Cancelled. Nothing was signed.");
            return 1;
        }

        var signature = await _prompt.SignWithPasswordAsync(key, bundle.TreeId, cancellationToken);
        if (signature is null) return 1;

        await _output.WriteLineAsync("Checking the signature with viberails.ai...");
        var verification = await _verifier.VerifyAsync(signature, cancellationToken);
        switch (verification.Status)
        {
            case CommitSignatureStatus.Valid:
                await _output.WriteLineAsync($"{s.Green}Verified: signed by {verification.SignerEmail}.{s.Reset}");
                if (_accountEmail is not null && verification.SignerEmail is not null
                    && !string.Equals(_accountEmail, verification.SignerEmail, StringComparison.OrdinalIgnoreCase))
                {
                    await _output.WriteLineAsync(
                        $"{s.Yellow}Your saved viberails.ai account is {_accountEmail}, and this key belongs to {verification.SignerEmail}. "
                        + $"viberails.ai records only a signature from the account the request is addressed to.{s.Reset}");
                }
                break;
            case CommitSignatureStatus.Unavailable:
                await _output.WriteLineAsync(
                    $"{s.Red}Could not confirm the signature: {verification.Detail}. Nothing was recorded; try again when viberails.ai is reachable.{s.Reset}");
                return 1;
            default:
                await _output.WriteLineAsync($"{s.Red}viberails.ai did not accept the signature: {verification.Detail}. Nothing was recorded.{s.Reset}");
                return 1;
        }

        await _output.WriteLineAsync("Recording your approval on viberails.ai...");
        ReviewRequestHostedDecision recorded;
        try
        {
            recorded = await _post(request.Reference,
                new ReviewInboxDecisionBody(ReviewDecisionKind.Approved, CommitSignatureFormat.Format(signature), null), cancellationToken);
        }
        catch (ReviewRequestTransportException ex)
        {
            await _output.WriteLineAsync($"{s.Red}{ex.Message} Your approval was not recorded.{s.Reset}");
            return 1;
        }
        await _output.WriteLineAsync(
            $"{s.Green}Approval recorded on viberails.ai ({recorded.DecidedUtc:yyyy-MM-dd HH:mm} UTC). The requester's VibeRails collects it on its "
            + $"next check, and the commit hook applies it to exactly this tree.{s.Reset}");
        return 0;
    }

    /// <summary>
    /// What the person is approving, before any verification or prompt: the repository and the local copy
    /// the tree is rebuilt in, who asked, the card and note (the requester's words, cleaned and bounded),
    /// the attached sessions, every file with its change kind, counts and omission, then the tree and
    /// its base, last so they sit right above the verdict.
    /// </summary>
    private async Task WriteSummaryAsync(ReviewHostedBundle bundle, string repositoryPath)
    {
        var s = _style;
        await WriteFieldAsync("Repository", [HumanSignaturePopup.Clean(bundle.Repository), $"your copy: {repositoryPath}"]);
        await WriteFieldAsync("Requested by", [HumanSignaturePopup.Clean(bundle.RequestedBy)]);
        await WriteFieldAsync("Card", [bundle.CardKey is { Length: > 0 } ? HumanSignaturePopup.Clean(bundle.CardKey, MaxLineChars) : "(none)"]);

        var note = NoteLines(bundle.Note);
        await WriteFieldAsync("Note", note.Lines.Count == 0 ? ["(none)"] : note.More > 0 ? [.. note.Lines, $"... and {note.More} more line(s)"] : note.Lines);

        var sessions = bundle.Sessions!;
        await WriteFieldAsync("Sessions", sessions.Count == 0
            ? ["(none attached)"]
            : sessions.Select(session => HumanSignaturePopup.Clean(
                $"{session.Number}) {session.Name ?? "session"} ({session.Cli ?? "cli"}{(session.Status is { Length: > 0 } status ? ", " + status : string.Empty)})", MaxLineChars)).ToList());

        var files = bundle.Files!;
        var listed = files.Take(MaxListedFiles).Select(file => "  " + DescribeFile(file)).ToList();
        if (files.Count > MaxListedFiles)
            listed.Add($"  ... and {files.Count - MaxListedFiles} more");
        await WriteFieldAsync("Files", [$"{files.Count} file(s)", .. listed]);

        await WriteFieldAsync("Tree", [bundle.TreeId]);
        await WriteFieldAsync("Base", [bundle.BaseCommit is not null ? $"commit {bundle.BaseCommit}" : $"tree {bundle.BaseTree}"]);
        await WriteFieldAsync("Read it at", [ReviewInboxService.ReadUrl(bundle.Id)]);
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(
            $"{s.Dim}The card and the note are the requester's words, which may be an agent's. What you sign is the tree: "
            + $"the files above, rebuilt below from your own copy of the repository.{s.Reset}");
        await _output.WriteLineAsync();
    }

    /// <summary>
    /// One file line: change kind, exact path (a rename names both), and the line counts of the base and
    /// staged text as carried, or why it carries no content. The counts are computed from the text the
    /// rebuild then verifies, never taken from the requester's <c>changedLines</c>.
    /// </summary>
    internal static string DescribeFile(ReviewBundleFile file)
    {
        var letter = file.Change switch
        {
            "added" => "A", "modified" => "M", "deleted" => "D", "renamed" => "R", "copied" => "C", "unmerged" => "U", _ => "?"
        };
        var path = HumanSignaturePopup.Clean(file.Path);
        if (file.PreviousPath is { } previous && previous != file.Path)
            path = HumanSignaturePopup.Clean(previous) + " -> " + path;
        var detail = file.Omitted is { } omitted
            ? $"no content: {omitted}"
            : $"{LineCount(file.Before).ToString(CultureInfo.InvariantCulture)} -> {LineCount(file.After).ToString(CultureInfo.InvariantCulture)} lines";
        return $"{letter} {path} ({detail})";
    }

    internal static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = text.Count(c => c == '\n');
        return text[^1] == '\n' ? count : count + 1;
    }

    internal static (IReadOnlyList<string> Lines, int More) NoteLines(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return ([], 0);
        var lines = note.Split('\n').Select(line => line.TrimEnd('\r').TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var shown = lines.Take(MaxNoteLines).Select(line => line.Length == 0 ? string.Empty : HumanSignaturePopup.Clean(line, MaxLineChars)).ToList();
        return (shown, lines.Count - shown.Count);
    }

    private async Task WriteFieldAsync(string label, IReadOnlyList<string> lines)
    {
        var s = _style;
        for (var i = 0; i < lines.Count; i++)
            await _output.WriteLineAsync($"  {s.Dim}{(i == 0 ? label : string.Empty),-12}{s.Reset}  {lines[i]}");
    }

    /// <summary>Production wiring for the popup process: the real console, key store, verifier and the hosted transport under the saved account key.</summary>
    internal static async Task<int> RunInConsoleAsync(
        VcaHookInvocation invocation,
        TextWriter output,
        VcaConsoleStyle style,
        CancellationToken cancellationToken)
    {
        if (!invocation.ConsoleWindowAttached || !VcaHookProcessLaunch.CanPromptInCurrentConsole() || string.IsNullOrWhiteSpace(invocation.ReviewReference))
        {
            await output.WriteLineAsync(
                "The review approval prompt only runs in the popup window VibeRails opens from \"Reviews requested of you\". "
                + "It cannot be run in a terminal or with redirected input.");
            return 1;
        }

        var settings = Config.LoadFresh();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            await output.WriteLineAsync("Sign in to viberails.ai in VibeRails Settings first; the request is read with your account key. Nothing was signed.");
            return 1;
        }
        var apiKey = settings.ApiKey;

        using var hosted = new HttpClient(ReviewRequestTransport.CreateHandler()) { Timeout = ReviewRequestTransport.SendDeadline };
        var transport = new ReviewRequestTransport(hosted);
        using var registration = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var keys = new SigningKeyService(registration, new SigningKeyStore(), () => Config.LoadFresh().ApiKey);
        var popup = new ReviewSignaturePopup(
            output,
            new ConsoleHumanSignatureConsole(output, HumanSignaturePopup.IdleTimeout),
            new SigningKeyServiceCommitSigner(keys),
            ViberailsCommitSignatureVerifier.Shared,
            (reference, ct) => transport.BundleAsync(apiKey, reference, ct),
            (repository, bundle, ct) => ReviewTreeRebuilder.RebuildAsync(repository, bundle.TreeId, bundle.BaseTree, bundle.BaseCommit, bundle.Files!, ct),
            (reference, decision, ct) => transport.DecideAsync(apiKey, reference, decision, ct),
            ApiKeyStore.GetAccountEmail(settings),
            style);
        return await popup.RunAsync(
            new ReviewSignaturePopupRequest(invocation.ReviewReference, invocation.WorkingDirectory ?? Directory.GetCurrentDirectory()),
            cancellationToken);
    }
}
