using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services;

/// <summary>Board publication and Send to handoffs name their source the same way (VIBE-363).</summary>
public sealed class RemoteSourceNamesTests
{
    [Theory]
    [InlineData("C:/source/vibe-rails", "vibe-rails")]
    [InlineData("C:/source/vibe-rails/", "vibe-rails")]
    [InlineData("/home/dev/project", "project")]
    public void Repository_IsTheLastFolderName(string path, string expected) =>
        Assert.Equal(expected, RemoteSourceNames.Repository(path));

    [Fact]
    public void Names_AreCapped()
    {
        Assert.Equal(new string('r', RemoteSourceNames.MaxRepositoryChars),
            RemoteSourceNames.Repository("C:/source/" + new string('r', 200)));
        var computer = RemoteSourceNames.Computer();
        Assert.InRange(computer.Length, 1, RemoteSourceNames.MaxComputerChars);
        Assert.StartsWith(computer, Environment.MachineName, StringComparison.Ordinal);
    }
}
