using VibeRails.Services.Board.Tracker;

namespace VibeRails.Services.Jira;

/// <summary>
/// The Jira API tokens for this machine, one per connection id, in <c>jira-tokens.json</c>.
/// See <see cref="ITrackerTokenStore"/> for how a token is kept.
/// </summary>
public interface IJiraSecretStore : ITrackerTokenStore;

public sealed class JiraSecretStore : TrackerTokenStore, IJiraSecretStore
{
    public const string FileName = "jira-tokens.json";

    public JiraSecretStore() : this(DefaultPath()) { }

    public JiraSecretStore(string path) : base(path) { }

    public static string DefaultPath() => PathInDataDirectory(FileName);
}
