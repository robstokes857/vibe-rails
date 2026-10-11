using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// Paths recorded for other programs to call VibeRails back (MCP registrations, git hooks) must
/// point at the per-user install, which is what the user updates and what is on PATH, never at a
/// version-numbered extension directory (VB-K5SCD-210).
/// </summary>
public sealed class InstalledVibeRailsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-installed-{Guid.NewGuid():N}");
    private readonly string _bundled;
    private readonly string _installed;

    public InstalledVibeRailsTests()
    {
        _bundled = Path.Combine(_root, "extensions", "viberails-10.11.19", "bin", "vb.exe");
        _installed = Path.Combine(_root, ".vibe_rails", "vb.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_bundled)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_installed)!);
        File.WriteAllText(_bundled, "bundled");
        File.WriteAllText(_installed, "installed");
    }

    [Fact]
    public void ExecutableForCallbacks_PrefersTheInstall_OverTheRunningBundle()
    {
        Assert.Equal(_installed, InstalledVibeRails.ExecutableForCallbacks(_bundled, _installed));
    }

    [Fact]
    public void ExecutableForCallbacks_FallsBackToTheRunningBinary_WithoutAnInstall()
    {
        File.Delete(_installed);

        Assert.Equal(_bundled, InstalledVibeRails.ExecutableForCallbacks(_bundled, _installed));
    }

    [Fact]
    public void ExecutableForCallbacks_LooksUpTheRealInstall_WhenNoPathIsGiven()
    {
        // Null means "the installer's location on this machine", so the answer depends on whether
        // this machine has an install: the real one when it exists, else the running binary.
        var realInstall = InstalledVibeRails.InstallExecutablePath();
        var expected = realInstall is not null && File.Exists(realInstall) ? realInstall : _bundled;

        Assert.Equal(expected, InstalledVibeRails.ExecutableForCallbacks(_bundled, installPath: null));
    }

    [Fact]
    public void ExecutableForCallbacks_LeavesANonVibeRailsProcessAlone()
    {
        // A framework-dependent build runs under the dotnet host; the caller resolves the entry dll.
        var dotnetHost = Path.Combine(_root, "dotnet.exe");
        File.WriteAllText(dotnetHost, "host");

        Assert.Equal(dotnetHost, InstalledVibeRails.ExecutableForCallbacks(dotnetHost, _installed));
        Assert.Null(InstalledVibeRails.ExecutableForCallbacks(null, _installed));
        Assert.Equal(string.Empty, InstalledVibeRails.ExecutableForCallbacks(string.Empty, _installed));
    }

    [Fact]
    public void InstallExecutablePath_IsTheInstallerLocationUnderTheProfile()
    {
        var path = InstalledVibeRails.InstallExecutablePath();

        Assert.NotNull(path);
        Assert.Equal(PathConstants.GetInstallDirPath(), Path.GetDirectoryName(path));
        Assert.Equal(OperatingSystem.IsWindows() ? "vb.exe" : "vb", Path.GetFileName(path));
    }

    [Fact]
    public void ProductVersionOf_IsNull_ForAFileWithoutAVersionResource()
    {
        Assert.Null(InstalledVibeRails.ProductVersionOf(_bundled));
        Assert.Null(InstalledVibeRails.ProductVersionOf(Path.Combine(_root, "missing.exe")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
