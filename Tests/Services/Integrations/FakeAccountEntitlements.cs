using VibeRails.Services.Integrations.VibeCodeRemote;

namespace Tests.Services.Integrations;

/// <summary>
/// A plan the test controls. Like the real client, a noted denial turns that feature off at once,
/// so a test can show that a loop stops after one refused request.
/// </summary>
internal sealed class FakeAccountEntitlements : IAccountEntitlements
{
    /// <summary>Features this account is known not to have.</summary>
    public HashSet<string> Off { get; } = [];
    /// <summary>Every <see cref="NoteDenied"/> call, in order.</summary>
    public List<string> Noted { get; } = [];
    public int Reads;

    public Task<AccountEntitlements> GetAsync(CancellationToken ct, bool refresh = false)
    {
        Interlocked.Increment(ref Reads);
        var features = Off.Aggregate(ProFeatureFlags.All, (flags, feature) => flags.Without(feature));
        return Task.FromResult(new AccountEntitlements(Off.Count == 0 ? AccountEntitlements.Pro : AccountEntitlements.Free,
            true, features, "https://plans.example.test/Billing", DateTime.UtcNow));
    }

    public async Task<bool> AllowsAsync(string feature, CancellationToken ct) => (await GetAsync(ct)).Allows(feature);

    public void NoteDenied(string feature)
    {
        Noted.Add(feature);
        Off.Add(feature);
    }

    public void Invalidate() => Off.Clear();
}
