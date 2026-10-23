using System.Diagnostics;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.SigningKeys;
using VibeRails.Utils;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>Answers from the person at the signing popup. Null means cancelled or timed out.</summary>
public interface IHumanSignatureConsole
{
    Task<string?> ReadLineAsync(string prompt, CancellationToken cancellationToken);
    Task<string?> ReadSecretAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>The local signing keys, seen from the popup.</summary>
public interface ICommitTreeSigner
{
    /// <summary>Keys that can sign commits: synced to viberails.ai for the current API key.</summary>
    Task<IReadOnlyList<SigningKeyInfo>> ListKeysAsync(CancellationToken cancellationToken);

    Task<CommitSignature> SignAsync(Guid keyId, string password, string treeId, CancellationToken cancellationToken);
}

/// <summary>What the popup signs and what it shows the person.</summary>
public sealed record HumanSignaturePopupRequest(
    string CommitMessagePath,
    string WorkingDirectory,
    string TreeId,
    string? BaseTree,
    IReadOnlyList<string> Approvers,
    IReadOnlyList<string> Requirements);

/// <summary>
/// The Git Guard signing popup. Shows the person what they are approving (repository, message,
/// staged tree and changed files), lets them pick a synced signing key, reads its password with echo
/// off, signs the staged tree, checks the signature with viberails.ai, and appends the
/// <c>vibe-signature:</c> line to the commit message. Exit code 0 means a verified signature was added.
/// </summary>
/// <remarks>
/// <para>
/// What makes this hard to automate is the key password, which is never stored and is read only from
/// the popup's own console (redirected input is refused). The popup is also the only VibeRails code
/// that signs the commit-approval payload: the dashboard's Sign message route refuses that prefix and
/// no MCP tool signs. The hook opens it as a separate console window rather than prompting in the
/// terminal that ran <c>git commit</c>, which may be an agent's pseudo-terminal.
/// </para>
/// <para>
/// These are speed bumps around the password, not a boundary against something that already runs as
/// the user and knows it: such a process can decrypt the key file and sign without VibeRails, or
/// drive the window with UI automation. The signature proves the key holder approved this exact
/// staged tree, not who was at the keyboard.
/// </para>
/// </remarks>
public sealed class HumanSignaturePopup
{
    public const int MaxPasswordAttempts = 3;
    private const int MaxListedChanges = 25;
    private const int MaxDescriptionLines = 12;
    private const int MaxLineChars = 160;
    private const int MaxListedCards = 3;

    private readonly TextWriter _output;
    private readonly IHumanSignatureConsole _console;
    private readonly ICommitTreeSigner _signer;
    private readonly ICommitSignatureVerifier _verifier;
    private readonly VcaConsoleStyle _style;
    private readonly Func<string, string, string, CancellationToken, Task<IReadOnlyList<string>>> _listChanges;
    private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<string>>> _findCards;

    /// <param name="listChanges">(repository, base tree, tree) → one display line per changed file.</param>
    /// <param name="findCards">(repository, commit message) → one display line per Board card.</param>
    public HumanSignaturePopup(
        TextWriter output,
        IHumanSignatureConsole console,
        ICommitTreeSigner signer,
        ICommitSignatureVerifier verifier,
        VcaConsoleStyle? style = null,
        Func<string, string, string, CancellationToken, Task<IReadOnlyList<string>>>? listChanges = null,
        Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>? findCards = null)
    {
        _output = output;
        _console = console;
        _signer = signer;
        _verifier = verifier;
        _style = style ?? VcaConsoleStyle.Plain;
        _listChanges = listChanges ?? ListChangesWithGitAsync;
        _findCards = findCards ?? HumanSignatureBoardCards.FindAsync;
    }

    public async Task<int> RunAsync(HumanSignaturePopupRequest request, CancellationToken cancellationToken)
    {
        var s = _style;
        await _output.WriteLineAsync($"{s.Bold}{s.Cyan}Git Guard: human verification{s.Reset}");
        await _output.WriteLineAsync("A person must approve this commit before Git Guard lets it through.");
        await _output.WriteLineAsync();

        if (!CommitSignatureFormat.IsObjectId(request.TreeId))
        {
            await _output.WriteLineAsync($"{s.Red}Git Guard did not pass a valid staged tree id. Nothing was signed.{s.Reset}");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(request.CommitMessagePath) || !File.Exists(request.CommitMessagePath))
        {
            await _output.WriteLineAsync($"{s.Red}The commit message file was not found. Nothing was signed.{s.Reset}");
            return 1;
        }

        await WriteSummaryAsync(request, cancellationToken);

        var keys = await WaitForSyncedKeysAsync(cancellationToken);
        if (keys is null)
            return 1;

        var key = await ChooseKeyAsync(keys, cancellationToken);
        if (key is null)
        {
            await _output.WriteLineAsync("Cancelled. Nothing was signed.");
            return 1;
        }

        var signature = await SignWithPasswordAsync(key, request.TreeId, cancellationToken);
        if (signature is null) return 1;

        await _output.WriteLineAsync("Checking the signature with viberails.ai...");
        var verification = await _verifier.VerifyAsync(signature, cancellationToken);
        switch (verification.Status)
        {
            case CommitSignatureStatus.Valid:
                await _output.WriteLineAsync($"{s.Green}Verified: signed by {verification.SignerEmail}.{s.Reset}");
                if (request.Approvers.Count > 0 && verification.SignerEmail is not null
                    && !request.Approvers.Contains(verification.SignerEmail, StringComparer.OrdinalIgnoreCase))
                {
                    await _output.WriteLineAsync(
                        $"{s.Yellow}The approval rule asks for {HumanSignatureRule.DescribeApprovers(request.Approvers)}, "
                        + $"and {verification.SignerEmail} is not listed. The signature is still added; Git Guard reports what remains.{s.Reset}");
                }
                break;
            case CommitSignatureStatus.Unavailable:
                await _output.WriteLineAsync(
                    $"{s.Red}Could not confirm the signature: {verification.Detail}. Git Guard needs viberails.ai to "
                    + $"accept a commit signature, so nothing was added. Try the commit again when it is reachable.{s.Reset}");
                return 1;
            default:
                await _output.WriteLineAsync(
                    $"{s.Red}viberails.ai did not accept the signature: {verification.Detail}. Nothing was added.{s.Reset}");
                return 1;
        }

        await AppendSignatureAsync(request.CommitMessagePath, signature, cancellationToken);
        await _output.WriteLineAsync($"{s.Green}Signature added to the commit message. Git Guard checks it again now.{s.Reset}");
        return 0;
    }

    /// <summary>
    /// What the person is approving, before any key or password prompt (VIBE-200): the Board card,
    /// the whole commit message, every changed file with its line counts, then the tree and the
    /// rules that asked for the signature, last so they sit right above the prompt. The card and the
    /// message are the committer's own words, possibly an agent's, so every line is stripped of
    /// control characters (an escape sequence could otherwise repaint this screen) and the person is
    /// told that the files and the tree are what the signature covers.
    /// </summary>
    private async Task WriteSummaryAsync(HumanSignaturePopupRequest request, CancellationToken cancellationToken)
    {
        var s = _style;
        await WriteFieldAsync("Repository", [request.WorkingDirectory]);

        var message = ReadMessage(request.CommitMessagePath);
        IReadOnlyList<string> cards;
        try { cards = await _findCards(request.WorkingDirectory, message, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { cards = []; }
        await WriteFieldAsync("Card", cards.Count == 0
            ? ["(none found)"]
            : cards.Take(MaxListedCards).Select(card => Clean(card)).ToList());

        var (subject, description, more) = DescribeMessage(message);
        await WriteFieldAsync("Message", [subject]);
        if (description.Count > 0)
            await WriteFieldAsync(string.Empty, more > 0 ? [.. description, $"... and {more} more line(s)"] : description);

        IReadOnlyList<string> changes = [];
        if (request.BaseTree is { } baseTree && CommitSignatureFormat.IsObjectId(baseTree))
        {
            try { changes = await _listChanges(request.WorkingDirectory, baseTree, request.TreeId, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { changes = []; }
        }
        if (changes.Count > 0)
        {
            var listed = changes.Take(MaxListedChanges).Select(change => Clean(change)).ToList();
            if (changes.Count > MaxListedChanges)
                listed.Add($"... and {changes.Count - MaxListedChanges} more");
            await WriteFieldAsync("Changes", [$"{changes.Count} file(s)", .. listed.Select(line => "  " + line)]);
        }
        else
        {
            await WriteFieldAsync("Changes", ["(none listed; the tree below is still what you sign)"]);
        }

        await WriteFieldAsync("Tree", [request.TreeId]);
        // Longer bound: an approval names every accepted signer, and a cut list would hide one.
        await WriteFieldAsync("Required", request.Requirements.Select(requirement => Clean(requirement, 600)).ToList());
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(
            $"{s.Dim}The card and the message are written by whoever made the commit, which may be an agent. "
            + $"The signature covers the changed files above (the tree), nothing else.{s.Reset}");
        await _output.WriteLineAsync();
    }

    private async Task WriteFieldAsync(string label, IReadOnlyList<string> lines)
    {
        var s = _style;
        for (var i = 0; i < lines.Count; i++)
            await _output.WriteLineAsync($"  {s.Dim}{(i == 0 ? label : string.Empty),-10}{s.Reset}  {lines[i]}");
    }

    /// <summary>
    /// Lists the keys that can sign. With none synced, the window waits for the person to create and
    /// sync one in Settings &gt; KEYS and checks again on Enter, instead of failing and leaving a
    /// "no signing key" window behind while they set one up for a second attempt (VIBE-200).
    /// Null means cancelled, timed out or unreadable.
    /// </summary>
    private async Task<IReadOnlyList<SigningKeyInfo>?> WaitForSyncedKeysAsync(CancellationToken cancellationToken)
    {
        var s = _style;
        for (var check = 0; ; check++)
        {
            IReadOnlyList<SigningKeyInfo> keys;
            try
            {
                keys = await _signer.ListKeysAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await _output.WriteLineAsync($"{s.Red}Could not read your signing keys: {ex.Message}{s.Reset}");
                return null;
            }
            if (keys.Count > 0)
                return keys;

            if (check == 0)
            {
                await _output.WriteLineAsync($"{s.Yellow}No signing key on this computer is synced to viberails.ai.{s.Reset}");
                await _output.WriteLineAsync(
                    "Create one in VibeRails Settings > KEYS with your API key saved and sync it. This window waits; "
                    + "the commit does not need to be started again.");
            }
            else
            {
                await _output.WriteLineAsync($"{s.Yellow}Still no synced signing key.{s.Reset}");
            }

            if (await _console.ReadLineAsync("Press Enter to check again (Esc cancels): ", cancellationToken) is null)
            {
                await _output.WriteLineAsync("Cancelled. Nothing was signed.");
                return null;
            }
        }
    }

    private async Task<SigningKeyInfo?> ChooseKeyAsync(IReadOnlyList<SigningKeyInfo> keys, CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync("Signing keys synced to viberails.ai:");
        for (var i = 0; i < keys.Count; i++)
            await _output.WriteLineAsync($"  {i + 1}) {keys[i].Name}  fingerprint {keys[i].Fingerprint[..12]}");
        if (keys.Count == 1)
            return keys[0];

        while (true)
        {
            var answer = await _console.ReadLineAsync($"Key number [1-{keys.Count}, Enter for 1, Esc cancels]: ", cancellationToken);
            if (answer is null) return null;
            answer = answer.Trim();
            if (answer.Length == 0) return keys[0];
            if (int.TryParse(answer, out var number) && number >= 1 && number <= keys.Count)
                return keys[number - 1];
            await _output.WriteLineAsync($"Enter a number from 1 to {keys.Count}.");
        }
    }

    private async Task<CommitSignature?> SignWithPasswordAsync(SigningKeyInfo key, string treeId, CancellationToken cancellationToken)
    {
        var s = _style;
        for (var attempt = 1; attempt <= MaxPasswordAttempts; attempt++)
        {
            var password = await _console.ReadSecretAsync($"Password for \"{key.Name}\" (hidden, Esc cancels): ", cancellationToken);
            if (password is null)
            {
                await _output.WriteLineAsync("Cancelled. Nothing was signed.");
                return null;
            }

            try
            {
                return await _signer.SignAsync(key.Id, password, treeId, cancellationToken);
            }
            catch (SigningKeyLockedException ex)
            {
                await _output.WriteLineAsync($"{s.Red}{ex.Message}{s.Reset}");
                return null;
            }
            catch (ArgumentException ex)
            {
                await _output.WriteLineAsync($"{s.Red}{ex.Message}{s.Reset}");
                // Only a wrong password is worth another try; every other refusal repeats itself.
                if (!ex.Message.StartsWith("Incorrect password", StringComparison.Ordinal)
                    && !ex.Message.StartsWith("Password ", StringComparison.Ordinal))
                    return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException
                or System.Text.Json.JsonException)
            {
                await _output.WriteLineAsync($"{s.Red}Could not use the signing key: {ex.Message}{s.Reset}");
                return null;
            }
        }

        await _output.WriteLineAsync("Too many incorrect passwords. Nothing was signed.");
        return null;
    }

    /// <summary>
    /// Adds the signature as a trailer: after a blank line, before a scissors line if there is one
    /// (everything below it is dropped by <c>git commit -v</c>), keeping the file's own line endings.
    /// Signature lines over another tree, and earlier ones from the same key, are removed first: they
    /// cannot help this commit, and kept they would pile up across amends and signing rounds.
    /// </summary>
    internal static async Task AppendSignatureAsync(string path, CommitSignature signature, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var line = CommitSignatureFormat.Format(signature);

        var scissors = FindScissorsLine(text);
        var head = scissors >= 0 ? text[..scissors] : text;
        var tail = scissors >= 0 ? text[scissors..] : string.Empty;

        var trimmedHead = RemoveSupersededSignatures(head, signature).TrimEnd('\r', '\n');
        var lastLine = trimmedHead[(trimmedHead.LastIndexOf('\n') + 1)..].Trim();
        var separator = trimmedHead.Length == 0
            ? string.Empty
            : lastLine.StartsWith(CommitSignatureFormat.Trailer, StringComparison.OrdinalIgnoreCase)
                ? newline
                : newline + newline;
        var updated = trimmedHead + separator + line + newline + (tail.Length > 0 ? newline + tail : string.Empty);
        await File.WriteAllTextAsync(path, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private static string RemoveSupersededSignatures(string text, CommitSignature replacement)
    {
        var kept = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var end = text.IndexOf('\n', position);
            var next = end < 0 ? text.Length : end + 1;
            var line = text[position..next];
            var superseded = CommitSignatureFormat.TryParseTrailer(line, out var existing)
                && (existing.TreeId != replacement.TreeId || existing.KeyId == replacement.KeyId);
            if (!superseded) kept.Append(line);
            position = next;
        }
        return kept.ToString();
    }

    private static int FindScissorsLine(string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var end = text.IndexOf('\n', position);
            var line = (end < 0 ? text[position..] : text[position..end]).TrimEnd('\r');
            if (line.Length > 1 && line.Contains(" >8 ", StringComparison.Ordinal) && line.TrimStart().StartsWith('#'))
                return position;
            if (end < 0) break;
            position = end + 1;
        }
        return -1;
    }

    private static string ReadMessage(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var scissors = FindScissorsLine(text);
            return scissors >= 0 ? text[..scissors] : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The subject and the body Git will record: comment lines and earlier <c>vibe-signature:</c>
    /// lines dropped, blank runs collapsed, each line cleaned and bounded.
    /// </summary>
    internal static (string Subject, IReadOnlyList<string> Description, int More) DescribeMessage(string message)
    {
        var lines = new List<string>();
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            if (line.TrimStart().StartsWith('#') || CommitSignatureFormat.TryParseTrailer(line, out _))
                continue;
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
                continue;
            lines.Add(line.Length == 0 ? string.Empty : Clean(line));
        }
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            return ("(empty message)", [], 0);

        var body = lines.Skip(1).SkipWhile(line => line.Length == 0).ToList();
        var shown = body.Take(MaxDescriptionLines).ToList();
        return (lines[0], shown, body.Count - shown.Count);
    }

    /// <summary>One display line with control characters and bidi overrides removed, bounded.</summary>
    private static string Clean(string text, int maxChars = MaxLineChars) => BoardPromptComposer.SanitizeLine(text, maxChars);

    private static async Task<IReadOnlyList<string>> ListChangesWithGitAsync(
        string workingDirectory, string baseTree, string treeId, CancellationToken cancellationToken)
    {
        // Two tree objects, no index: the popup never reads the live index, which may already have
        // moved, and the trees are exactly what the hook validated. Paths stay C-quoted by Git, so
        // both listings name a file identically and an odd path cannot inject control characters.
        var status = await GitCli.RunAsync(
            workingDirectory,
            ["diff-tree", "-r", "--name-status", "--no-renames", baseTree, treeId],
            cancellationToken,
            maxOutputChars: 64 * 1024);
        if (!status.Succeeded) return [];
        var numstat = await GitCli.RunAsync(
            workingDirectory,
            ["diff-tree", "-r", "--numstat", "--no-renames", baseTree, treeId],
            cancellationToken,
            maxOutputChars: 64 * 1024);

        var counts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (numstat.Succeeded)
        {
            foreach (var line in numstat.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split('\t', 3);
                if (fields.Length == 3)
                    counts[fields[2]] = fields[0] == "-" ? "binary" : $"+{fields[0]} -{fields[1]}";
            }
        }

        return status.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 2))
            .Where(fields => fields.Length == 2)
            .Select(fields => counts.TryGetValue(fields[1], out var count)
                ? $"{fields[0]} {fields[1]} ({count})"
                : $"{fields[0]} {fields[1]}")
            .ToList();
    }

    /// <summary>Production wiring for the popup process: the real console, key store and verifier.</summary>
    internal static async Task<int> RunInConsoleAsync(
        VcaHookInvocation invocation,
        TextWriter output,
        VcaConsoleStyle style,
        CancellationToken cancellationToken)
    {
        var arguments = invocation.Signing;
        if (!invocation.ConsoleWindowAttached || !VcaHookProcessLaunch.CanPromptInCurrentConsole() || arguments is null)
        {
            await output.WriteLineAsync(
                "The commit signing prompt only runs in the Git Guard popup window that commit-msg opens. "
                + "It cannot be run in a terminal or with redirected input.");
            return 1;
        }

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var keys = new SigningKeyService(http, new SigningKeyStore(), () => Config.LoadFresh().ApiKey);
        var popup = new HumanSignaturePopup(
            output,
            new ConsoleHumanSignatureConsole(output, IdleTimeout),
            new SigningKeyServiceCommitSigner(keys),
            ViberailsCommitSignatureVerifier.Shared,
            style);
        return await popup.RunAsync(
            new HumanSignaturePopupRequest(
                invocation.CommitMessagePath ?? string.Empty,
                invocation.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                arguments.TreeId ?? string.Empty,
                arguments.BaseTree,
                arguments.Approvers,
                arguments.Requirements),
            cancellationToken);
    }

    /// <summary>
    /// How long one prompt waits for a key before the popup gives up. The commit is waiting on this
    /// window; a person who walked away should not hold it forever.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
}

/// <summary>Adapts <see cref="SigningKeyService"/> to the popup.</summary>
internal sealed class SigningKeyServiceCommitSigner(SigningKeyService keys) : ICommitTreeSigner
{
    public async Task<IReadOnlyList<SigningKeyInfo>> ListKeysAsync(CancellationToken cancellationToken) =>
        (await keys.ListAsync(cancellationToken)).Keys.Where(key => key.CloudKeyId is not null).ToList();

    public Task<CommitSignature> SignAsync(Guid keyId, string password, string treeId, CancellationToken cancellationToken) =>
        keys.SignCommitTreeAsync(keyId, password, treeId, cancellationToken);
}

/// <summary>
/// Reads from the popup's own console, key by key, so it can time out and so the password is never
/// echoed. Redirected input is refused outright: a password must be typed, not piped.
/// </summary>
internal sealed class ConsoleHumanSignatureConsole(TextWriter output, TimeSpan idleTimeout) : IHumanSignatureConsole
{
    public Task<string?> ReadLineAsync(string prompt, CancellationToken cancellationToken) =>
        ReadAsync(prompt, secret: false, cancellationToken);

    public Task<string?> ReadSecretAsync(string prompt, CancellationToken cancellationToken) =>
        ReadAsync(prompt, secret: true, cancellationToken);

    private async Task<string?> ReadAsync(string prompt, bool secret, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
            return null;

        await output.WriteAsync(prompt);
        await output.FlushAsync(cancellationToken);
        var builder = new StringBuilder();
        var idle = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (idle.Elapsed > idleTimeout)
            {
                await output.WriteLineAsync();
                await output.WriteLineAsync("No answer; the prompt timed out.");
                return null;
            }

            if (!Console.KeyAvailable)
            {
                await Task.Delay(50, cancellationToken);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            idle.Restart();
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    await output.WriteLineAsync();
                    return builder.ToString();
                case ConsoleKey.Escape:
                    await output.WriteLineAsync();
                    return null;
                case ConsoleKey.Backspace:
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                        if (!secret) await output.WriteAsync("\b \b");
                    }
                    continue;
            }

            if (char.IsControl(key.KeyChar)) continue;
            builder.Append(key.KeyChar);
            if (!secret) await output.WriteAsync(key.KeyChar);
            await output.FlushAsync(cancellationToken);
        }
    }
}
