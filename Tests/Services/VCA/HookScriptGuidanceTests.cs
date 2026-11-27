using System.Text.RegularExpressions;
using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// What the installed hook scripts and Git Guard tell someone whose commit was blocked (VIBE-331).
/// <c>--no-verify</c> skips every hook, not the one rule in the way, so guidance never offers it;
/// a deliberate change gets past STOP through a signed exception instead.
/// </summary>
public sealed class HookScriptGuidanceTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Theory]
    [InlineData("pre-commit-hook.sh")]
    [InlineData("commit-msg-hook.sh")]
    [InlineData("post-commit-hook.sh")]
    public void HookScriptsNeverSuggestSkippingEveryHook(string script)
    {
        var text = File.ReadAllText(Path.Combine(Root, "VibeRails", "scripts", script));
        Assert.DoesNotContain("--no-verify", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GitGuardMessagesNeverSuggestSkippingEveryHook()
    {
        var source = Path.Combine(Root, "VibeRails", "Services", "VCA");
        var offenders = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"""[^""\r\n]*\b(use|or)\b[^""\r\n]*--no-verify"))
            .Select(path => Path.GetRelativePath(source, path))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void PreCommitLetsAPersonAnswerWhereCommitMsgDoes()
    {
        // The STOP exception is offered at pre-commit, so pre-commit has to say when a person can answer,
        // by the same rule commit-msg uses: a terminal on both ends, or the Git Guard console.
        const string condition = "if { [ -t 0 ] && [ -t 1 ]; } || [ -n \"$viberails_console_arg\" ]; then\n    viberails_prompt_arg=--prompt-acknowledgment";
        var preCommit = ReadScript("pre-commit-hook.sh");
        Assert.Contains(condition, preCommit, StringComparison.Ordinal);
        Assert.Contains(condition, ReadScript("commit-msg-hook.sh"), StringComparison.Ordinal);
        Assert.Contains(
            "--vca-hook pre-commit --enqueue-automations \"$viberails_prompt_arg\" \"$viberails_console_arg\"",
            preCommit,
            StringComparison.Ordinal);
    }

    private static string ReadScript(string name) =>
        File.ReadAllText(Path.Combine(Root, "VibeRails", "scripts", name)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
