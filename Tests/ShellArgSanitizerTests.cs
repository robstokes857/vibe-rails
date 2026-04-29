using VibeRails.Utils;
using Xunit;

namespace Tests;

public class ShellArgSanitizerTests
{
    [Fact]
    public void ParseAndValidate_PreservesQuotedValuesWithSpacesAndParentheses()
    {
        var args = ShellArgSanitizer.ParseAndValidate("--allowedTools \"Bash(git log *)\" \"Bash(git diff *)\" Read");

        Assert.Equal(
            ["--allowedTools", "Bash(git log *)", "Bash(git diff *)", "Read"],
            args);
    }

    [Fact]
    public void Validate_ReturnsError_ForUnterminatedQuote()
    {
        var error = ShellArgSanitizer.Validate("--system-prompt \"You are a Python expert");

        Assert.Equal("CustomArgs contains an unterminated quoted value.", error);
    }

    [Fact]
    public void Validate_AcceptsCommonPromptCharacters()
    {
        // Shell metacharacters inside a quoted prompt are safe because EscapeArg
        // always wraps args in single quotes (POSIX) or backtick-escaped double
        // quotes (PowerShell) before they reach a shell. Rejecting them here would
        // just block legitimate system prompts.
        Assert.Null(ShellArgSanitizer.Validate("--system-prompt \"Use $HOME and rm -rf /tmp/foo; echo done!\""));
        Assert.Null(ShellArgSanitizer.Validate("--system-prompt \"Backticks `like this` and pipes | & ; redirects > <\""));
        Assert.Null(ShellArgSanitizer.Validate("--system-prompt \"Braces {a,b} and parens (x)\""));
    }

    [Fact]
    public void Validate_RejectsControlCharacters()
    {
        // NUL, CR, and LF cannot survive per-arg quoting (and our own tokenizer
        // treats CR/LF as whitespace separators), so they remain forbidden.
        Assert.NotNull(ShellArgSanitizer.Validate("--prompt \"with\0null\""));
        Assert.NotNull(ShellArgSanitizer.Validate("--prompt \"line1\nline2\""));
        Assert.NotNull(ShellArgSanitizer.Validate("--prompt \"line1\rline2\""));
    }

    [Fact]
    public void ParseAndValidate_QuotedSystemPromptParsesAsSingleArg()
    {
        var args = ShellArgSanitizer.ParseAndValidate("--system-prompt \"Use $HOME and avoid `eval`\"");

        Assert.Equal(["--system-prompt", "Use $HOME and avoid `eval`"], args);
    }
}
