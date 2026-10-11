using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// The MCP server path CLIs are registered with must track whatever the user has installed. The
/// VS Code bundle lives under a version-numbered directory, so registering the running binary goes
/// stale on every update; the per-user install wins whenever it exists (VB-K5SCD-210).
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
        File.WriteAllText(_bundled, "bundled build");
        File.WriteAllText(_installed, "installed build, possibly a different version");
    }

    [Fact]
    public void RegistersTheInstall_WheneverItExists_EvenWhenItIsADifferentBuild()
    {
        var parts = CommandService.ResolveMcpServerCommandParts(_bundled, _installed);

        Assert.Equal([_installed, "mcp"], parts);
    }

    [Fact]
    public void RegistersTheRunningBinary_WhenThereIsNoInstall()
    {
        File.Delete(_installed);

        var parts = CommandService.ResolveMcpServerCommandParts(_bundled, _installed);

        Assert.Equal([_bundled, "mcp"], parts);
    }

    [Fact]
    public void RunningFromTheInstall_RegistersThatSamePath()
    {
        var parts = CommandService.ResolveMcpServerCommandParts(_installed, _installed);

        Assert.Equal([_installed, "mcp"], parts);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
