using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public sealed class GlobalRuntimePathsTests
{
    [Fact]
    public void ResolveGlobalDirectory_DefaultsAndTrimsTheConfiguredName()
    {
        // The default name resolves through the data-directory policy (VIBE_RAILS_HOME, a Debug
        // build's dev directory) rather than to a fixed ~/.vibe_rails: a Debug test run must land
        // in the same place a Debug dashboard would, never in the shipped extension's data.
        var expectedDefault = PathConstants.GetInstallDirPath();

        Assert.Equal(expectedDefault, GlobalRuntimePaths.ResolveGlobalDirectory(null));
        Assert.Equal(expectedDefault, GlobalRuntimePaths.ResolveGlobalDirectory("   "));
        Assert.Equal(expectedDefault, GlobalRuntimePaths.ResolveGlobalDirectory(PathConstants.DEFAULT_INSTALL_DIR_NAME));
        // A padded override must resolve to the same directory Initialize creates; the raw
        // untrimmed value previously sent reads to a different directory than writes.
        Assert.Equal(
            GlobalRuntimePaths.ResolveGlobalDirectory(".vibe_rails"),
            GlobalRuntimePaths.ResolveGlobalDirectory("  .vibe_rails  "));
    }

    [Fact]
    public void ResolveGlobalDirectory_HonorsTheDocumentedOverrideSemantics()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // "VibeRails:InstallDirName" has always flowed through Path.Combine: a rooted value
        // replaces the profile base, a relative value may nest. Both were honored before VBD
        // and must not crash the app at startup now.
        var rooted = Path.Combine(Path.GetTempPath(), "vr_state_override");
        Assert.Equal(Path.GetFullPath(rooted), GlobalRuntimePaths.ResolveGlobalDirectory(rooted));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(profile, "data", "vr")),
            GlobalRuntimePaths.ResolveGlobalDirectory(Path.Combine("data", "vr")));
    }
}
