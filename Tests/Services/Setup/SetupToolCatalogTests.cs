using VibeRails.Services.Setup;
using Xunit;

namespace Tests.Services.Setup;

public sealed class SetupToolCatalogTests
{
    public static TheoryData<SetupPlatform> Platforms => [SetupPlatform.Windows, SetupPlatform.MacOS, SetupPlatform.Linux];

    [Theory]
    [MemberData(nameof(Platforms))]
    public void ToolAndMethodIdsAreUniqueAndDependenciesExist(SetupPlatform platform)
    {
        var tools = SetupToolCatalog.For(platform);
        var ids = tools.Select(tool => tool.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var tool in tools)
        {
            Assert.StartsWith("https://", tool.DocsUrl);
            Assert.NotEmpty(tool.Commands);
            var methodIds = tool.Methods.Select(method => method.Id).ToList();
            Assert.Equal(methodIds.Count, methodIds.Distinct().Count());
            // A method may only depend on something the same page can show and install.
            Assert.All(tool.Methods.Where(method => method.Requires is not null),
                method => Assert.Contains(method.Requires, ids));
            Assert.All(tool.Methods, method => Assert.False(string.IsNullOrWhiteSpace(method.Command)));
        }
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void EveryAgentCliTheLauncherKnowsIsListed(SetupPlatform platform)
    {
        var agents = SetupToolCatalog.For(platform)
            .Where(tool => tool.Group == SetupToolGroup.AgentCli)
            .SelectMany(tool => tool.Commands)
            .ToHashSet();
        // CommandService.ResolveCliExecutable's binaries.
        Assert.Superset(new HashSet<string> { "claude", "codex", "copilot", "opencode", "agy", "grok" }, agents);
    }

    [Fact]
    public void PowerShellIsRequiredOnlyOnWindowsAndInstallsWithoutItself()
    {
        var windows = SetupToolCatalog.For(SetupPlatform.Windows).Single(tool => tool.Id == SetupToolCatalog.PowerShell);
        Assert.Equal(SetupToolGroup.Required, windows.Group);
        // The install terminal falls back to Windows PowerShell 5.1, so the method must not need pwsh or npm.
        Assert.All(windows.Methods, method => Assert.NotEqual(SetupToolCatalog.Node, method.Requires));

        Assert.Equal(SetupToolGroup.Optional,
            SetupToolCatalog.For(SetupPlatform.MacOS).Single(tool => tool.Id == SetupToolCatalog.PowerShell).Group);
    }

    [Fact]
    public void OpenCodeOnWindowsInstallsOnlyThroughNpmBecauseMcpRegistrationCallsTheCmdShim()
    {
        var opencode = SetupToolCatalog.For(SetupPlatform.Windows).Single(tool => tool.Id == "opencode");
        var method = Assert.Single(opencode.Methods);
        Assert.Equal("npm", method.Id);
    }

    [Fact]
    public void FindMethodOnlyResolvesCatalogEntriesForThatPlatform()
    {
        Assert.NotNull(SetupToolCatalog.FindMethod(SetupPlatform.Windows, "claude", "native"));
        Assert.Null(SetupToolCatalog.FindMethod(SetupPlatform.Windows, "claude", "brew"));
        Assert.Null(SetupToolCatalog.FindMethod(SetupPlatform.Windows, "calc", "native"));
        Assert.Null(SetupToolCatalog.FindMethod(SetupPlatform.Windows, null, null));
        // Ordinal: a differently cased ID is not an alias.
        Assert.Null(SetupToolCatalog.FindMethod(SetupPlatform.Windows, "Claude", "native"));
    }

    [Fact]
    public void TheRecommendedMethodIsTheVendorsNativeInstallerOffMac()
    {
        foreach (var platform in new[] { SetupPlatform.Windows, SetupPlatform.Linux })
        {
            var claude = SetupToolCatalog.For(platform).Single(tool => tool.Id == "claude");
            Assert.Equal("native", claude.Methods[0].Id);
            Assert.Null(claude.Methods[0].Requires);
        }
    }

    [Fact]
    public void OnMacHomebrewIsRecommendedWhereverItIsOffered()
    {
        var tools = SetupToolCatalog.For(SetupPlatform.MacOS);
        foreach (var tool in tools.Where(tool => tool.Methods.Any(method => method.Id == SetupToolCatalog.Homebrew)))
            Assert.Equal(SetupToolCatalog.Homebrew, tool.Methods[0].Id);
        // Every brew-first tool still offers a route that does not need Homebrew.
        foreach (var id in new[] { "git", "claude", "codex", "copilot", "opencode" })
            Assert.Contains(tools.Single(tool => tool.Id == id).Methods, method => method.Requires is null);
    }

    [Fact]
    public void HomebrewInstallPutsBrewOnTheLoginShellPath()
    {
        var brew = SetupToolCatalog.For(SetupPlatform.MacOS).Single(tool => tool.Id == SetupToolCatalog.Homebrew);
        var command = Assert.Single(brew.Methods).Command;
        Assert.StartsWith("/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\" && ", command);
        Assert.Contains("grep -qs 'brew shellenv'", command);
        Assert.Contains("eval \"$(/opt/homebrew/bin/brew shellenv)\"", command);
        // One line on the page: printf writes the newlines, the command text has none.
        Assert.DoesNotContain('\n', command);
    }
}
