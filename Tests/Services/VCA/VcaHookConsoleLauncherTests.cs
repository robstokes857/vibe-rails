using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public class VcaHookConsoleLauncherTests
{
    [Fact]
    public void Launch_MissingRepositoryPath_ReportsTheReasonInsteadOfThrowing()
    {
        var result = VcaHookConsoleLauncher.Launch(
            Path.Combine(Path.GetTempPath(), $"vca_launcher_missing_{Guid.NewGuid():N}"),
            "pre-commit");

        Assert.False(result.Success);
        Assert.Contains("repository path", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_EmptyRepositoryPath_ReportsTheReasonInsteadOfThrowing()
    {
        var result = VcaHookConsoleLauncher.Launch("   ", "pre-commit");

        Assert.False(result.Success);
        Assert.Contains("repository path", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Launch_OnNonWindows_ExplainsThePlatformLimitRatherThanFailingSilently()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = VcaHookConsoleLauncher.Launch(Path.GetTempPath(), "pre-commit");

        Assert.False(result.Success);
        Assert.Contains("Windows", result.Message, StringComparison.Ordinal);
    }
}
