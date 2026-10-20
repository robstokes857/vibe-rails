using VibeRails.DTOs;
using VibeRails.Services.SigningKeys;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public sealed class HumanSignaturePopupTests : IDisposable
{
    private static readonly string Tree = new('d', 40);
    private static readonly string BaseTree = new('e', 40);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"human_signature_popup_{Guid.NewGuid():N}");
    private readonly string _messagePath;

    public HumanSignaturePopupTests()
    {
        Directory.CreateDirectory(_directory);
        _messagePath = Path.Combine(_directory, "COMMIT_EDITMSG");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignsTheStagedTreeVerifiesItAndAppendsTheTrailer()
    {
        await File.WriteAllTextAsync(_messagePath, "Fix the database layer\n\nBody text.\n", Ct);
        var signer = new FakeSigner(Key("Laptop"));
        var console = new ScriptedConsole(secrets: ["correct horse battery"]);
        var output = new StringWriter();

        var exit = await Popup(output, console, signer, Verifier("owner@example.com")).RunAsync(Request(["owner@example.com"]), Ct);

        Assert.Equal(0, exit);
        var signed = Assert.Single(signer.Signed);
        Assert.Equal((signer.Keys[0].Id, "correct horse battery", Tree), signed);
        var message = await File.ReadAllTextAsync(_messagePath, Ct);
        Assert.StartsWith("Fix the database layer\n\nBody text.\n\nvibe-signature: v1 ", message);
        Assert.Equal(Tree, Assert.Single(CommitSignatureFormat.Parse(message)).TreeId);
        var transcript = output.ToString();
        Assert.Contains("Fix the database layer", transcript);
        Assert.Contains(Tree, transcript);
        Assert.Contains("M db.cs", transcript);
        Assert.Contains("Signed Approval('db.cs', 'owner@example.com') (owner@example.com)", transcript);
        Assert.Contains("Verified: signed by owner@example.com", transcript);
        Assert.DoesNotContain("correct horse battery", transcript);
    }

    [Fact]
    public async Task NoSyncedKeyExplainsHowToGetOne()
    {
        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(), new FakeSigner(), Verifier("a@example.com")).RunAsync(Request([]), Ct);

        Assert.Equal(1, exit);
        Assert.Contains("Settings > KEYS", output.ToString());
        Assert.Equal("Subject\n", await File.ReadAllTextAsync(_messagePath, Ct));
    }

    [Fact]
    public async Task WrongPasswordRetriesAndEscapeCancels()
    {
        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var signer = new FakeSigner(Key("Laptop")) { Password = "right password" };
        var retried = new ScriptedConsole(secrets: ["wrong one", "right password"]);

        Assert.Equal(0, await Popup(new StringWriter(), retried, signer, Verifier("a@example.com")).RunAsync(Request([]), Ct));
        Assert.Equal(2, signer.Attempts);

        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var cancelled = new ScriptedConsole(secrets: [null]);
        Assert.Equal(1, await Popup(new StringWriter(), cancelled, signer, Verifier("a@example.com")).RunAsync(Request([]), Ct));
        Assert.Equal("Subject\n", await File.ReadAllTextAsync(_messagePath, Ct));
    }

    [Fact]
    public async Task StopsAfterThreeWrongPasswords()
    {
        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var signer = new FakeSigner(Key("Laptop")) { Password = "right password" };
        var console = new ScriptedConsole(secrets: ["a wrong one", "b wrong one", "c wrong one", "right password"]);

        Assert.Equal(1, await Popup(new StringWriter(), console, signer, Verifier("a@example.com")).RunAsync(Request([]), Ct));
        Assert.Equal(HumanSignaturePopup.MaxPasswordAttempts, signer.Attempts);
    }

    [Fact]
    public async Task ChoosesAmongSeveralKeys()
    {
        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var signer = new FakeSigner(Key("Laptop"), Key("Desktop"));
        var console = new ScriptedConsole(lines: ["7", "2"], secrets: ["correct horse battery"]);
        var output = new StringWriter();

        Assert.Equal(0, await Popup(output, console, signer, Verifier("a@example.com")).RunAsync(Request([]), Ct));
        Assert.Equal(signer.Keys[1].Id, Assert.Single(signer.Signed).KeyId);
        Assert.Contains("Enter a number from 1 to 2", output.ToString());
    }

    [Theory]
    [InlineData(CommitSignatureStatus.Rejected)]
    [InlineData(CommitSignatureStatus.Unavailable)]
    public async Task SignatureTheServerDoesNotConfirmIsNotAdded(CommitSignatureStatus status)
    {
        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var verifier = new FakeCommitSignatureVerifierForPopup(new CommitSignatureVerification(status, "nope"));

        var exit = await Popup(new StringWriter(), new ScriptedConsole(secrets: ["correct horse battery"]),
            new FakeSigner(Key("Laptop")), verifier).RunAsync(Request([]), Ct);

        Assert.Equal(1, exit);
        Assert.Equal("Subject\n", await File.ReadAllTextAsync(_messagePath, Ct));
    }

    [Fact]
    public async Task SignerOutsideTheApproverListIsWarnedButStillRecorded()
    {
        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), new FakeSigner(Key("Laptop")),
            Verifier("someone@example.com")).RunAsync(Request(["owner@example.com"]), Ct);

        Assert.Equal(0, exit);
        Assert.Contains("someone@example.com is not listed", output.ToString());
    }

    [Fact]
    public async Task RefusesABadTreeBeforeTouchingKeys()
    {
        await File.WriteAllTextAsync(_messagePath, "Subject\n", Ct);
        var signer = new FakeSigner(Key("Laptop"));

        var exit = await Popup(new StringWriter(), new ScriptedConsole(), signer, Verifier("a@example.com"))
            .RunAsync(Request([]) with { TreeId = "HEAD" }, Ct);

        Assert.Equal(1, exit);
        Assert.Equal(0, signer.ListCalls);
    }

    [Theory]
    [InlineData("Subject\n", "Subject\n\n{0}\n")]
    [InlineData("Subject", "Subject\n\n{0}\n")]
    [InlineData("Subject\r\n\r\nBody\r\n", "Subject\r\n\r\nBody\r\n\r\n{0}\r\n")]
    [InlineData("Subject\n\nvibe-signature: v1 old\n", "Subject\n\nvibe-signature: v1 old\n{0}\n")]
    [InlineData("Subject\n# Please enter the commit message\n# ------------------------ >8 ------------------------\ndiff --git a b\n",
        "Subject\n# Please enter the commit message\n\n{0}\n\n# ------------------------ >8 ------------------------\ndiff --git a b\n")]
    public async Task AppendKeepsLineEndingsAndStaysAboveTheScissors(string original, string expected)
    {
        await File.WriteAllTextAsync(_messagePath, original, Ct);
        var signature = new CommitSignature(Guid.NewGuid(), new string('c', 64), Tree,
            Convert.ToBase64String(new byte[256]));

        await HumanSignaturePopup.AppendSignatureAsync(_messagePath, signature, Ct);

        Assert.Equal(string.Format(expected, CommitSignatureFormat.Format(signature)), await File.ReadAllTextAsync(_messagePath, Ct));
    }

    [Fact]
    public async Task RefusesToRunOutsideThePopupWindow()
    {
        var output = new StringWriter();
        var invocation = new VcaHookCommandParser().Parse(
            ["--vca-hook", "sign-commit-msg", "--commit-message", _messagePath, "--tree", Tree]);

        var exit = await HumanSignaturePopup.RunInConsoleAsync(invocation, output, VcaConsoleStyle.Plain, Ct);

        Assert.Equal(1, exit);
        Assert.Contains("only runs in the Git Guard popup", output.ToString());
    }

    [Fact]
    public void ParserReadsThePopupArguments()
    {
        var invocation = new VcaHookCommandParser().Parse(HumanSignaturePromptLauncher.BuildPopupArguments(
            _messagePath, _directory,
            new HumanSignaturePopupArguments(Tree, BaseTree, ["a@example.com", "b@example.com"],
                ["Human verified (any registered signing key)"])).ToArray());

        Assert.Equal(VcaHookKind.SignCommitMessage, invocation.Kind);
        Assert.True(invocation.ShowConsoleWindow);
        Assert.Equal(_messagePath, invocation.CommitMessagePath);
        Assert.Equal(_directory, invocation.WorkingDirectory);
        Assert.Equal(Tree, invocation.Signing!.TreeId);
        Assert.Equal(BaseTree, invocation.Signing.BaseTree);
        Assert.Equal(["a@example.com", "b@example.com"], invocation.Signing.Approvers);
        Assert.Equal(["Human verified (any registered signing key)"], invocation.Signing.Requirements);
    }

    private HumanSignaturePopupRequest Request(IReadOnlyList<string> approvers) => new(
        _messagePath, _directory, Tree, BaseTree, approvers,
        approvers.Count == 0
            ? ["Human verified (any registered signing key)"]
            : [$"Signed Approval('db.cs', '{approvers[0]}') ({approvers[0]})"]);

    private static HumanSignaturePopup Popup(TextWriter output, IHumanSignatureConsole console, ICommitTreeSigner signer,
        ICommitSignatureVerifier verifier) =>
        new(output, console, signer, verifier, VcaConsoleStyle.Plain,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(["M db.cs"]));

    private static FakeCommitSignatureVerifierForPopup Verifier(string email) =>
        new(new CommitSignatureVerification(CommitSignatureStatus.Valid, "valid", email));

    private static SigningKeyInfo Key(string name) =>
        new(Guid.NewGuid(), name, "-----BEGIN PUBLIC KEY-----", Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + new string('0', 32),
            DateTimeOffset.UtcNow, Guid.NewGuid(), DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ScriptedConsole(IEnumerable<string?>? lines = null, IEnumerable<string?>? secrets = null) : IHumanSignatureConsole
    {
        private readonly Queue<string?> _lines = new(lines ?? []);
        private readonly Queue<string?> _secrets = new(secrets ?? []);

        public Task<string?> ReadLineAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);

        public Task<string?> ReadSecretAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(_secrets.Count > 0 ? _secrets.Dequeue() : null);
    }

    private sealed class FakeSigner(params SigningKeyInfo[] keys) : ICommitTreeSigner
    {
        public SigningKeyInfo[] Keys { get; } = keys;
        public string Password { get; init; } = "correct horse battery";
        public int Attempts { get; private set; }
        public int ListCalls { get; private set; }
        public List<(Guid KeyId, string Password, string TreeId)> Signed { get; } = [];

        public Task<IReadOnlyList<SigningKeyInfo>> ListKeysAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<SigningKeyInfo>>(Keys);
        }

        public Task<CommitSignature> SignAsync(Guid keyId, string password, string treeId, CancellationToken cancellationToken)
        {
            Attempts++;
            if (password != Password) throw new ArgumentException("Incorrect password or damaged key file.");
            Signed.Add((keyId, password, treeId));
            var key = Keys.Single(candidate => candidate.Id == keyId);
            return Task.FromResult(new CommitSignature(key.CloudKeyId!.Value, new string('c', 64), treeId,
                Convert.ToBase64String(new byte[512])));
        }
    }

    private sealed class FakeCommitSignatureVerifierForPopup(CommitSignatureVerification answer) : ICommitSignatureVerifier
    {
        public Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken) =>
            Task.FromResult(answer);
    }
}
