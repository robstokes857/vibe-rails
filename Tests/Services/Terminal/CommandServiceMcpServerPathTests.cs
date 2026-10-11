using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// The MCP server path CLIs are registered with must survive extension upgrades. The VS Code
/// bundle lives under a version-numbered directory, so registering the running binary goes stale
/// on every update; the per-user install is preferred whenever it is the same build (VB-K5SCD-210).
/// </summary>
public sealed class CommandServiceMcpServerPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-mcp-path-{Guid.NewGuid():N}");
    private readonly string _bundled;
    private readonly string _installed;

    public CommandServiceMcpServerPathTests()
    {
        _bundled = Path.Combine(_root, "extensions", "viberails-10.11.19", "bin", "vb.exe");
        _installed = Path.Combine(_root, ".vibe_rails", "vb.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_bundled)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_installed)!);
        File.WriteAllText(_bundled, "bundled");
        File.WriteAllText(_installed, "install");
    }

    [Fact]
    public void PrefersTheStableInstall_WhenItIsTheSameBuild()
    {
        var parts = CommandService.ResolveMcpServerCommandParts(_bundled, _installed, (_, _) => true);

        Assert.Equal([_installed, "mcp"], parts);
    }

    [Fact]
    public void KeepsTheRunningBinary_WhenTheInstallIsADifferentBuild()
    {
        var parts = CommandService.ResolveMcpServerCommandParts(_bundled, _installed, (_, _) => false);

        Assert.Equal([_bundled, "mcp"], parts);
    }

    [Fact]
    public void KeepsTheRunningBinary_WhenThereIsNoInstall()
    {
        File.Delete(_installed);
        var calls = 0;

        var parts = CommandService.ResolveMcpServerCommandParts(_bundled, _installed, (_, _) => { calls++; return true; });

        Assert.Equal([_bundled, "mcp"], parts);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RunningFromTheInstall_RegistersItWithoutComparingBuilds()
    {
        var calls = 0;

        var parts = CommandService.ResolveMcpServerCommandParts(_installed, _installed, (_, _) => { calls++; return false; });

        Assert.Equal([_installed, "mcp"], parts);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void IsSameBuild_ComparesContent_WhenThereIsNoVersionResource()
    {
        var copy = Path.Combine(_root, "copy-of-bundled.exe");
        File.Copy(_bundled, copy);
        var sameLengthDifferentBytes = Path.Combine(_root, "other.exe");
        File.WriteAllText(sameLengthDifferentBytes, "BUNDLED");

        Assert.True(CommandService.IsSameBuild(_bundled, copy));
        Assert.False(CommandService.IsSameBuild(_bundled, sameLengthDifferentBytes), "same length, different bytes");
        Assert.False(CommandService.IsSameBuild(_bundled, _installed), "different length");
        Assert.False(CommandService.IsSameBuild(_bundled, Path.Combine(_root, "missing.exe")), "unreadable means not the same");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
