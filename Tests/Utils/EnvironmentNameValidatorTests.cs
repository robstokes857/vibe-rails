using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// The validator gates a user-supplied name that becomes:
///   1. A path segment under ~/.vibe_rails/envs/{name}
///   2. The value of CLAUDE_CONFIG_DIR / CODEX_HOME / XDG_*_HOME for spawned CLIs
/// Without validation, a name like "../../.ssh" would point those env vars at
/// the user's SSH directory and the CLI could read or overwrite it.
/// </summary>
public class EnvironmentNameValidatorTests
{
    [Theory]
    [InlineData("nightly-summary")]
    [InlineData("Daily Codex Review")]
    [InlineData("job_42")]
    [InlineData("a")]
    public void Accepts_SafeNames(string name)
    {
        Assert.Null(EnvironmentNameValidator.Validate(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_NullOrBlank(string? name)
    {
        Assert.NotNull(EnvironmentNameValidator.Validate(name));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../.ssh")]
    [InlineData("..\\..\\Windows")]
    [InlineData("evil/../../etc")]
    public void Rejects_TraversalAttempts(string name)
    {
        Assert.NotNull(EnvironmentNameValidator.Validate(name));
    }

    [Theory]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("C:\\Windows")]
    [InlineData("name:with:colons")]
    [InlineData("has\0null")]
    [InlineData("has\nnewline")]
    [InlineData("has*wildcard")]
    [InlineData("has\"quote")]
    public void Rejects_UnsafeCharacters(string name)
    {
        Assert.NotNull(EnvironmentNameValidator.Validate(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("Aux")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    public void Rejects_WindowsReservedDeviceNames(string name)
    {
        // These resolve to a device, not a folder, so envs/{name} would break on Windows.
        Assert.NotNull(EnvironmentNameValidator.Validate(name));
    }

    [Theory]
    [InlineData("-evil")]     // leading hyphen looks like a CLI flag if ever emitted unquoted
    [InlineData("-rf")]
    [InlineData("_leading")]  // first char must be alphanumeric
    public void Rejects_NamesNotStartingWithAlphanumeric(string name)
    {
        Assert.NotNull(EnvironmentNameValidator.Validate(name));
    }

    [Theory]
    [InlineData("CON-backup")]  // reserved name as a prefix is still a valid, distinct folder
    [InlineData("nul2")]
    [InlineData("2024-backup")] // leading digit is allowed
    public void Accepts_NamesThatOnlyResembleReserved(string name)
    {
        Assert.Null(EnvironmentNameValidator.Validate(name));
    }

    [Fact]
    public void Rejects_TooLongName()
    {
        var tooLong = new string('a', 65);
        Assert.NotNull(EnvironmentNameValidator.Validate(tooLong));
    }

    [Fact]
    public void Accepts_LeadingDot_OnlyIfNotTraversal()
    {
        // A single dot at the start IS unsafe (it would let "." resolve to the
        // envs/ root itself), so the regex bans leading dots. ".." is already
        // covered by the explicit substring check.
        Assert.NotNull(EnvironmentNameValidator.Validate("."));
        Assert.NotNull(EnvironmentNameValidator.Validate(".hidden"));
    }

    // ResolveEnvironmentDirectory is the launch-path containment guard: the launch
    // routes pass envName straight from the request without calling Validate(), so this
    // must stop "../" / absolute names from escaping the envs root regardless of charset.

    [Fact]
    public void ResolveEnvironmentDirectory_ReturnsPathWithinRoot_ForSafeName()
    {
        var root = Path.Combine(Path.GetTempPath(), "vb-envtest-root");
        var resolved = EnvironmentNameValidator.ResolveEnvironmentDirectory(root, "job_42");
        var expectedPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedPrefix, resolved);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../.ssh")]
    [InlineData("a/../../etc")]
    public void ResolveEnvironmentDirectory_Throws_OnTraversal(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "vb-envtest-root");
        Assert.Throws<ArgumentException>(
            () => EnvironmentNameValidator.ResolveEnvironmentDirectory(root, name));
    }

    [Fact]
    public void ResolveEnvironmentDirectory_Throws_OnAbsolutePath()
    {
        // Path.Combine discards the base when the second arg is rooted — the classic
        // Windows escape (an absolute EnvironmentName fully replaces the envs root).
        var root = Path.Combine(Path.GetTempPath(), "vb-envtest-root");
        var absolute = OperatingSystem.IsWindows() ? "C:\\Windows" : "/etc";
        Assert.Throws<ArgumentException>(
            () => EnvironmentNameValidator.ResolveEnvironmentDirectory(root, absolute));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveEnvironmentDirectory_Throws_OnEmpty(string? name)
    {
        var root = Path.Combine(Path.GetTempPath(), "vb-envtest-root");
        Assert.Throws<ArgumentException>(
            () => EnvironmentNameValidator.ResolveEnvironmentDirectory(root, name));
    }
}
