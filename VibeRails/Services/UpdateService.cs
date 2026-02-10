using System.Text.Json;

namespace VibeRails.Services;

public class UpdateService
{
    private readonly HttpClient _httpClient;
    private readonly string _githubRepo = "robstokes857/vibe-rails";
    private UpdateInfo? _cachedUpdateInfo;
    private DateTime _lastCheck = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(1);

    public UpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "VibeRails-UpdateChecker");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Return cached result if still valid
        if (_cachedUpdateInfo != null && DateTime.UtcNow - _lastCheck < _cacheExpiration)
        {
            return _cachedUpdateInfo;
        }

        try
        {
            var currentVersion = VersionInfo.Version;
            var releaseUrl = $"https://api.github.com/repos/{_githubRepo}/releases/latest";

            var response = await _httpClient.GetAsync(releaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse JSON using JsonDocument for AOT compatibility
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement))
            {
                return null;
            }

            var tagName = tagElement.GetString();
            if (string.IsNullOrEmpty(tagName))
            {
                return null;
            }

            // Parse version (remove 'v' prefix if present)
            var latestVersion = tagName.TrimStart('v');
            var updateAvailable = IsNewerVersion(currentVersion, latestVersion);

            var releaseNotesUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            _cachedUpdateInfo = new UpdateInfo
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                ReleaseNotesUrl = releaseNotesUrl
            };

            _lastCheck = DateTime.UtcNow;
            return _cachedUpdateInfo;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VibeRails] Error checking for updates: {ex.Message}");
            return null;
        }
    }

    private static bool IsNewerVersion(string current, string latest)
    {
        try
        {
            var currentParts = current.Split('.').Select(int.Parse).ToArray();
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();

            for (int i = 0; i < Math.Min(currentParts.Length, latestParts.Length); i++)
            {
                if (latestParts[i] > currentParts[i]) return true;
                if (latestParts[i] < currentParts[i]) return false;
            }

            return latestParts.Length > currentParts.Length;
        }
        catch
        {
            return false;
        }
    }

}

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool UpdateAvailable { get; set; }
    public string? ReleaseNotesUrl { get; set; }
}
