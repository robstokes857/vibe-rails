using VibeRails.Services.Jira;
using Xunit;

namespace Tests.Services.Jira;

public sealed class JiraSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-jira-secrets-{Guid.NewGuid():N}");

    public JiraSecretStoreTests() => Directory.CreateDirectory(_root);

    private string TokenPath => Path.Combine(_root, "jira-tokens.json");

    /// <summary>
    /// Two stores on one file share nothing in-process, exactly like two root backends (browser
    /// app and VS Code). Only the OS file lock keeps their read-modify-writes from losing tokens
    /// or colliding on the temporary file.
    /// </summary>
    [Fact]
    public async Task TwoStoresOnOneFile_NeverLoseEachOthersTokens()
    {
        var first = new JiraSecretStore(TokenPath);
        var second = new JiraSecretStore(TokenPath);

        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 40; i++) first.SaveToken($"a{i}", "token-a"); }, TestContext.Current.CancellationToken),
            Task.Run(() => { for (var i = 0; i < 40; i++) second.SaveToken($"b{i}", "token-b"); }, TestContext.Current.CancellationToken));

        var reader = new JiraSecretStore(TokenPath);
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal("token-a", reader.ReadToken($"a{i}"));
            Assert.Equal("token-b", reader.ReadToken($"b{i}"));
        }
        Assert.False(File.Exists(TokenPath + ".tmp"));
    }

    [Fact]
    public async Task PruneRemovesOnlyTokensWhoseConnectionIsGone()
    {
        var store = new JiraSecretStore(TokenPath);
        store.SaveToken("jira_live", "kept");
        store.SaveToken("jira_deleted", "dropped");

        await store.PruneAsync(_ => Task.FromResult<IReadOnlyCollection<string>>(["jira_live"]), TestContext.Current.CancellationToken);

        Assert.Equal("kept", store.ReadToken("jira_live"));
        Assert.False(store.HasToken("jira_deleted"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
