using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

public sealed class AppSettingsDataExportOptInTests
{
    [Fact]
    public void Settings_ShareCompletedSessionsByDefault()
    {
        Assert.True(new Settings().DataExportOptIn);
    }
}
