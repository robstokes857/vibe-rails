using System.Reflection;
using Xunit;

namespace Tests;

public class VersionInfoTests
{
    [Fact]
    public void Version_ComesFromBuiltAssemblyInformationalVersion()
    {
        var informationalVersion = typeof(VibeRails.VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.NotNull(informationalVersion);
        Assert.Equal(
            informationalVersion.Split('+', 2)[0],
            VibeRails.VersionInfo.Version);
    }
}
