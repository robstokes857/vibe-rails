using VibeRails.Services.Setup;
using Xunit;

namespace Tests.Services.Setup;

/// <summary>Describes machines with a fake file set; nothing here touches the real PATH or disk.</summary>
public sealed class SetupToolProbeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vb-setup-probe");
    private static string Dir(string name) => Path.Combine(Root, name);

    private static SetupProbeEnvironment Machine(
        IEnumerable<string> files,
        IReadOnlyList<string>? launch = null,
        IReadOnlyList<string>? current = null,
        bool isWindows = true)
    {
        var set = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SetupProbeEnvironment(isWindows, launch ?? [Dir("launch")], current ?? [], [],
            set.Contains, path => path.Replace("%HOME%", Dir("home")), _ => "1.2.3");
    }

    private static SetupTool Tool(string id, params string[] knownPaths) =>
        SetupToolCatalog.For(SetupPlatform.Windows).Single(tool => tool.Id == id) with { KnownPaths = knownPaths };

    [Fact]
    public void ToolOnTheLaunchPathIsReady()
    {
        var result = SetupToolProbe.Probe(Tool("git"), Machine([Path.Combine(Dir("launch"), "git.exe")]));
        Assert.Equal(SetupToolState.Ready, result.State);
        Assert.Equal(Path.Combine(Dir("launch"), "git.exe"), result.Path);
        Assert.Equal("1.2.3", result.Version);
    }

    [Fact]
    public void NpmCmdShimCountsAsInstalled()
    {
        var result = SetupToolProbe.Probe(Tool("codex"), Machine([Path.Combine(Dir("launch"), "codex.cmd")]));
        Assert.Equal(SetupToolState.Ready, result.State);
    }

    [Fact]
    public void ToolOnlyOnTheStoredPathNeedsARestart()
    {
        // Installed after VibeRails started: the registry PATH has it, this process does not.
        var result = SetupToolProbe.Probe(Tool("pwsh"),
            Machine([Path.Combine(Dir("pwsh7"), "pwsh.exe")], current: [Dir("pwsh7")]));
        Assert.Equal(SetupToolState.RestartNeeded, result.State);
    }

    [Fact]
    public void ToolOnlyAtItsVendorPathNeedsARestart()
    {
        var known = Path.Combine(Dir("home"), "agy.exe");
        var result = SetupToolProbe.Probe(Tool("agy", "%HOME%" + Path.DirectorySeparatorChar + "agy.exe"), Machine([known]));
        Assert.Equal(SetupToolState.RestartNeeded, result.State);
        Assert.Equal(known, result.Path);
    }

    [Fact]
    public void MissingToolHasNoPathOrVersion()
    {
        var result = SetupToolProbe.Probe(Tool("grok"), Machine([]));
        Assert.Equal(new SetupToolProbeResult("grok", SetupToolState.Missing, null, null), result);
    }

    [Fact]
    public void WindowsAppsPythonStoreStubIsIgnoredButTheInstallManagerAliasCounts()
    {
        var windowsApps = Path.Combine(Root, "AppData", "Local", "Microsoft", "WindowsApps");
        var stubOnly = SetupToolProbe.Probe(Tool("python"),
            Machine([Path.Combine(windowsApps, "python.exe")], launch: [windowsApps]));
        Assert.Equal(SetupToolState.Missing, stubOnly.State);

        var installManager = SetupToolProbe.Probe(Tool("python"),
            Machine([Path.Combine(windowsApps, "python.exe"), Path.Combine(windowsApps, "py.exe")], launch: [windowsApps]));
        Assert.Equal(SetupToolState.Ready, installManager.State);
        Assert.EndsWith("py.exe", installManager.Path);
    }

    [Fact]
    public void RelativePathEntriesAreNeverSearched()
    {
        var result = SetupToolProbe.Probe(Tool("git"), Machine([Path.Combine("relative", "git.exe")], launch: ["relative"]));
        Assert.Equal(SetupToolState.Missing, result.State);
    }

    [Fact]
    public void SplitPathDropsBlanksAndQuotes()
    {
        var separator = Path.PathSeparator;
        Assert.Equal([Dir("a"), Dir("b")],
            SetupProbeEnvironment.SplitPath($"\"{Dir("a")}\"{separator}{separator} {Dir("b")} "));
    }
}
